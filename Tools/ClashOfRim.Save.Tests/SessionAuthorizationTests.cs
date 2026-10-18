using System.Globalization;
using System.Net;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;

internal static class SessionAuthorizationTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "ja-JP" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (var code in new[] { ProtocolErrorCode.Unauthorized, ProtocolErrorCode.ServerRejected })
                    foreach (string mode in new[] { "valid", "missing", "blank", "unknown", "revoked", "token-expired", "session-expired", "ended", "replaced" })
                        Principal(mode, code);
                Permissions();
                HeaderCache();
                StreamAdmission();
            }
            HttpShapes(factory);
            // Appending a code must not renumber earlier wire values.
            Check((int)ProtocolErrorCode.Unauthorized == 1 && (int)ProtocolErrorCode.AccountNotFound == 11
                && (int)ProtocolErrorCode.SessionUnavailable == 12, "Protocol error codes changed wire values.");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void Principal(string mode, ProtocolErrorCode denialCode)
    {
        var now = DateTimeOffset.UtcNow;
        var tokens = new AuthTokenRegistry(); var sessions = new LoginSessionRegistry();
        sessions.TryCreate("user", "colony", now, out string session);
        if (mode == "session-expired") sessions.TryCreate("user", "colony", now.AddMinutes(-3), out session);
        string? token = tokens.Issue("steam", "user", "colony", session,
            mode == "token-expired" ? now.AddHours(-13) : now);
        if (mode == "missing") token = null;
        if (mode == "blank") token = " \t";
        if (mode == "unknown") token = "unrecognized";
        if (mode == "revoked") tokens.RevokeForUser("user");
        if (mode == "ended") sessions.EndUser("user");
        if (mode == "replaced") sessions.TryCreate("user", "colony", now, out _);
        var policy = new SessionPrincipalAuthorization(tokens, sessions);
        var result = policy.Authorize(token, now, out var principal, denialCode);
        if (mode == "valid")
        {
            Check(result is null && principal is { UserId: "user", ColonyId: "colony" } && principal.SessionId == session,
                "Successful authorization lost its principal.");
            Check(policy.Authorize(token, now.AddMinutes(1), out _, denialCode) is null
                && policy.Authorize(token, now.AddMinutes(2.5), out _, denialCode) is null, "Authorization stopped refreshing the pending session.");
        }
        else
        {
            AssertError(result, mode is "missing" or "blank" ? denialCode : ProtocolErrorCode.SessionUnavailable);
            Check(principal is null, "Rejected authorization retained a usable principal: " + mode);
        }
    }

    private static void Permissions()
    {
        var f = new SessionModuleTestContext(); var owner = f.Issue();
        var principals = new SessionPrincipalAuthorization(f.Tokens, f.Sessions);
        var colonies = new ColonyAccessAuthorization(principals, new(), new(), f.Ledger, () => false);
        var world = new WorldConfigurationRegistry(WorldConfigurationExtensionService.Empty);
        var admin = new AdministratorAuthorization(principals, world);
        foreach (var code in new[] { ProtocolErrorCode.Unauthorized, ProtocolErrorCode.ServerRejected })
        {
            AssertError(principals.AuthorizeOwner(owner.Token, "USER", "colony", DateTimeOffset.UtcNow, code), code);
            AssertError(colonies.Authorize(owner.Token, "user", "other", null, null, DateTimeOffset.UtcNow, code), code);
            AssertError(admin.Authorize("user", "colony", owner.Token, code), code);
        }
        Check(f.Valid(owner), "A permission denial retired the valid session.");
        f.Sessions.EndUser("user");
        AssertError(admin.Authorize("user", "colony", owner.Token), ProtocolErrorCode.SessionUnavailable);
        AssertError(colonies.Authorize(owner.Token, "user", "colony", null, null, DateTimeOffset.UtcNow), ProtocolErrorCode.SessionUnavailable);
    }

    private static void HeaderCache()
    {
        var state = new ClashOfRimNetworkState(); var now = DateTimeOffset.UtcNow;
        state.LoginSessions.TryCreate("user", "colony", now, out var session);
        string token = state.AuthTokens.Issue("steam", "user", "colony", session, now);
        var request = new DefaultHttpContext().Request;
        request.Headers[MultipartSnapshotAuthentication.HeaderName] = token;
        Check(MultipartSnapshotAuthentication.Authorize(request, state, now) is null
            && MultipartSnapshotAuthentication.MatchesPrincipal(request, "user", "colony"), "Header admission failed.");
        state.LoginSessions.EndUser("user");
        AssertError(MultipartSnapshotAuthentication.Authorize(request, state, now), ProtocolErrorCode.SessionUnavailable);
        Check(!MultipartSnapshotAuthentication.MatchesPrincipal(request, "user", "colony"), "Rejection retained a cached header principal.");
        request.Headers.Remove(MultipartSnapshotAuthentication.HeaderName);
        AssertError(MultipartSnapshotAuthentication.Authorize(request, state, now), ProtocolErrorCode.Unauthorized);
    }

    private static void StreamAdmission()
    {
        var f = new SessionModuleTestContext(); var owner = f.Issue();
        Check(f.Connections.Open("user", "colony", owner.Session, null, out var lease) is null, "Stream did not open.");
        using (lease)
        {
            AssertError(f.Connections.Open("user", "colony", owner.Session, null, out _), ProtocolErrorCode.ServerRejected);
            Check(f.Valid(owner) && lease!.IsActive(), "Duplicate stream invalidated the active session.");
            f.Sessions.EndUser("user");
            AssertError(f.Connections.Check("user", "colony", owner.Session, lease!), ProtocolErrorCode.SessionUnavailable);
            AssertError(f.Connections.Heartbeat("user", "colony", owner.Session, lease!), ProtocolErrorCode.SessionUnavailable);
            AssertError(f.Connections.Logout(new("user", "colony", owner.Session)).Result, ProtocolErrorCode.SessionUnavailable);
        }
        var next = f.Issue();
        Check(f.Connections.Open("user", "colony", next.Session, null, out var nextLease) is null, "Replacement stream did not open.");
        using (nextLease)
        {
            f.Connections.Close("user", "colony", owner.Session, lease);
            Check(f.Valid(next) && nextLease!.IsActive(), "Late cleanup invalidated the replacement session.");
        }
    }

    private static void HttpShapes(Func<string, SaveSnapshotPackage> factory)
    {
        using var f = new HttpSnapshotRecoveryTests.Fixture(factory); f.Start(); f.Seed();
        string before = f.Evidence();
        foreach (string shape in new[] { "header", "json", "binary" })
        foreach (string mode in new[] { "missing", "invalid", "ended", "identity" })
        {
            f.RenewToken();
            string? token = mode switch { "missing" => null, "invalid" => "unknown", _ => f.Token };
            if (mode == "ended") f.State.LoginSessions.EndUser("receiver");
            string user = mode == "identity" ? "other-user" : "receiver";
            if (shape == "header" && mode == "identity") continue; // The header has no requested target identity.
            using var response = Send();
            Check(response.StatusCode == (shape == "json" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized),
                "Authorization changed the HTTP status contract: " + shape);
            using var json = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var result = shape == "header" ? json.RootElement : json.RootElement.GetProperty("result");
            var ordinary = shape == "binary" ? ProtocolErrorCode.ServerRejected : ProtocolErrorCode.Unauthorized;
            var expected = mode is "invalid" or "ended" ? ProtocolErrorCode.SessionUnavailable : ordinary;
            Check(!result.GetProperty("accepted").GetBoolean() && result.GetProperty("errorCode").GetInt32() == (int)expected,
                "HTTP discarded or misclassified authorization: " + shape + "/" + mode + " " + result);
            f.AssertPending(before, shape + "/" + mode);

            HttpResponseMessage Send()
            {
                if (shape == "json") return f.Post(ProtocolMessageKind.ListChatMessages, new ListChatMessagesRequest(user, "away", token, 0, 20));
                if (shape == "binary") return f.Post(ProtocolMessageKind.DownloadLatestSnapshotPayload,
                    new DownloadLatestSnapshotPayloadRequest(user, "away", "base", token));
                using var request = new HttpRequestMessage(HttpMethod.Post, ProtocolContractManifest.Find(ProtocolMessageKind.UploadSnapshot).Route);
                if (token is not null) request.Headers.Add(MultipartSnapshotAuthentication.HeaderName, token);
                return f.Client.Send(request);
            }
        }
    }

    private static void AssertError(ProtocolResponse? result, ProtocolErrorCode code) =>
        Check(result is { Accepted: false } && result.ErrorCode == code, "Expected " + code + ", got " + result?.ErrorCode);
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
}
