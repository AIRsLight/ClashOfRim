using System.Security.Cryptography;
using System.Text.Json;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

// Metadata is the commit point. Referenced blobs are immutable and are never
// deleted by a failed or later write, so readers can finish using an older record.
internal static class WorldSubstratePersistence
{
    private const string Prefix = "world-substrate-sha256-";
    internal const string LegacyKey = "world-substrate";

    internal static string KeyFor(byte[] payload) => Prefix + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    internal static string? ReadKey(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("WorldSubstrateKey", out var element) || element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw new InvalidOperationException("Invalid world substrate reference.");
        string key = element.GetString()!;
        if (key.Length != 0) ValidateKey(key);
        return key;
    }

    internal static byte[]? Read(IBinaryPersistenceSlot? slot, string? key)
    {
        // Missing field is the pre-v28 format; empty string explicitly means no substrate.
        if (key is null) return slot?.ReadBinary(LegacyKey);
        if (key.Length == 0) return null;
        ValidateKey(key);
        byte[]? bytes;
        try { bytes = slot?.ReadBinary(key); }
        catch (IOException ex) { throw new InvalidOperationException("Cannot read the referenced world substrate.", ex); }
        if (bytes is null || KeyFor(bytes) != key)
            throw new InvalidOperationException("The referenced world substrate is missing or does not match its content hash.");
        if (!WorldSubstratePackageCodec.TryDecode(bytes, out var package, out _) || package is null)
            throw new InvalidOperationException("The referenced world substrate package is invalid.");
        var geometry = WorldTileGeometryBinaryCodec.Decode(package.TileGeometryPayload);
        if (geometry is null || geometry.Layers.Count == 0 || geometry.Layers.All(layer => layer.TileCenters.Count == 0))
            throw new InvalidOperationException("The referenced world substrate has no usable geometry.");
        return bytes;
    }

    internal static string Store(IBinaryPersistenceSlot? slot, byte[]? bytes)
    {
        if (bytes is null) return string.Empty;
        string key = KeyFor(bytes);
        if (slot is not null)
        {
            byte[]? existing = slot.ReadBinary(key);
            if (existing is null) slot.WriteBinary(key, bytes);
            else if (!existing.AsSpan().SequenceEqual(bytes))
                throw new InvalidOperationException("An immutable world substrate blob has conflicting content.");
        }
        return key;
    }

    private static void ValidateKey(string key)
    {
        if (!key.StartsWith(Prefix, StringComparison.Ordinal) || key.Length != Prefix.Length + 64
            || key.AsSpan(Prefix.Length).ContainsAnyExcept("0123456789abcdef"))
            throw new InvalidOperationException("Invalid world substrate reference.");
    }
}
