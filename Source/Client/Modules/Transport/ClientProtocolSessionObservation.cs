using System;
using System.Text.Json;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Transport;

internal enum ClientProtocolExchange { Json, Download, Multipart }

// Observe only credentials actually serialized into this request. A multipart
// middleware rejection identifies the header; an endpoint may check both header
// and body, so differing credentials cannot identify which session failed.
internal sealed class ClientProtocolSessionObservation
{
    private readonly Action? header;
    private readonly Action? envelope;
    private ClientProtocolSessionObservation(Action? header, Action? envelope)
    { this.header = header; this.envelope = envelope; }

    internal static ClientProtocolSessionObservation Capture(IClientProtocolEnvironment environment,
        ClashOfRimClientNetworkContext context, string? requestJson, ClientProtocolExchange exchange)
    {
        bool headerMatches = exchange != ClientProtocolExchange.Json && !string.IsNullOrWhiteSpace(context.AuthToken);
        bool bodyMatches = false;
        string? sessionId = null;
        if (requestJson is not null)
        {
            JsonDocument parsed;
            try { parsed = JsonDocument.Parse(requestJson); }
            catch (JsonException) { return new(null, null); }
            using var document = parsed;
            var request = document.RootElement;
            if (Property(request, "authToken", out var token))
                bodyMatches = token.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(context.AuthToken)
                    && token.GetString() == context.AuthToken;
            else if (Property(request, "sessionId", out var session))
            {
                sessionId = session.ValueKind == JsonValueKind.String ? session.GetString() : null;
                bodyMatches = !string.IsNullOrWhiteSpace(sessionId)
                    && Property(request, "userId", out var user) && user.ValueKind == JsonValueKind.String && user.GetString() == context.UserId
                    && Property(request, "colonyId", out var colony) && colony.ValueKind == JsonValueKind.String && colony.GetString() == context.ColonyId;
            }
            else bodyMatches = exchange == ClientProtocolExchange.Multipart && headerMatches;
        }
        var tokenObserver = (headerMatches || (bodyMatches && sessionId is null))
            ? environment.CaptureSessionUnavailable(context, null) : null;
        var bodyObserver = bodyMatches ? sessionId is null ? tokenObserver
            : environment.CaptureSessionUnavailable(context, sessionId) : null;
        return new(headerMatches ? tokenObserver : null, bodyObserver);
    }

    internal void Observe(string? json, bool allowHeaderRejection)
    {
        if (json is null || (header is null && envelope is null)) return;
        Action? invalidation;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("result", out _))
                invalidation = Property(root, "result", out var result) && Rejected(result) ? envelope : null;
            else invalidation = allowHeaderRejection && Rejected(root) ? header : null;
        }
        catch (JsonException) { return; } // Error bodies may be HTML or otherwise non-protocol data.
        invalidation?.Invoke();
    }

    private static bool Rejected(JsonElement result) =>
        Property(result, "accepted", out var accepted) && accepted.ValueKind == JsonValueKind.False
        && Property(result, "errorCode", out var code) && code.ValueKind == JsonValueKind.Number
        && code.TryGetInt32(out int value) && value == (int)ProtocolErrorCode.SessionUnavailable;

    // Duplicate control fields must not disagree with the typed response decoder.
    private static bool Property(JsonElement source, string name, out JsonElement value)
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
