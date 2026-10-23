using System;
using System.Text.Json;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;
using static AIRsLight.ClashOfRim.Modules.Transport.ClientProtocolRejection;

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
        Observe(Read(json), allowHeaderRejection);
    }

    internal void Observe(ClientProtocolRejection? rejection, bool allowHeaderRejection)
    {
        if (rejection?.Code != ProtocolErrorCode.SessionUnavailable) return;
        (rejection.IsEnvelope ? envelope : allowHeaderRejection ? header : null)?.Invoke();
    }
}
