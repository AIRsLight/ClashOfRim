using System.Text.Json;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Transport;

// A response can reject either the endpoint envelope or the multipart header.
// Human messages alone never identify a protocol or credential failure.
internal sealed record ClientProtocolRejection(ProtocolErrorCode Code, string? Message, bool IsEnvelope, string? ServerProtocolVersion)
{
    internal static ClientProtocolRejection? Read(string? json)
    {
        if (json is null) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            bool envelope = root.TryGetProperty("result", out _);
            var result = root;
            if (envelope && !Property(root, "result", out result)) return null;
            if (!Property(result, "accepted", out var accepted) || accepted.ValueKind != JsonValueKind.False
                || !Property(result, "errorCode", out var code) || code.ValueKind != JsonValueKind.Number
                || !code.TryGetInt32(out int value)) return null;
            string? message = Property(result, "message", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            string? server = Property(root, "serverProtocolVersion", out var version) && version.ValueKind == JsonValueKind.String
                ? version.GetString() : Property(root, "protocolVersion", out version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null;
            return new((ProtocolErrorCode)value, message, envelope, server);
        }
        catch (JsonException) { return null; } // Proxies may return HTML or plain text.
    }

    // Duplicate control fields must not disagree with the typed response decoder.
    internal static bool Property(JsonElement source, string name, out JsonElement value)
    {
        value = default; bool found = false;
        if (source.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in source.EnumerateObject())
        {
            if (property.Name != name) continue;
            if (found) { value = default; return false; }
            found = true; value = property.Value;
        }
        return found;
    }
}
