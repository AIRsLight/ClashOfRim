using System.Security.Cryptography;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

internal static class SnapshotRequestBinding
{
    // v1 binds the typed request (including identities, metadata and nested packages) and actual payload bytes.
    // Object property order is immaterial, but array order and all values are retained.
    internal static string Hash<T>(T request, byte[] payload) =>
        "v1:" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            Canonical(request) + "\n" + Convert.ToHexString(SHA256.HashData(payload)))));

    internal static string Canonical<T>(T value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(JsonSerializer.SerializeToElement(value), writer);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); Write(property.Value, writer); }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) Write(item, writer);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }
}
