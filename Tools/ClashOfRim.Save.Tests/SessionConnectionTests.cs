using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using static RecoveryTestSupport;

internal static class SessionConnectionTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        int count = 0;
        void Case(Action test) { test(); count++; }
        foreach (string route in new[] { "logout", "presence", "ws" })
        foreach (string invalid in new[] { "user", "colony", "session", "missing-user", "missing-colony", "missing-session", "expired", "replace", "ended" })
            Case(() => Invalid(factory, route, invalid));
        foreach (string route in new[] { "logout", "presence" })
        foreach (string invalid in new[] { "malformed", "empty", "null" })
            Case(() => Invalid(factory, route, invalid));
        Case(() => Invalid(factory, "ws", "no-upgrade"));
        foreach (string route in new[] { "presence", "ws" }) Case(() => Invalid(factory, route, "duplicate"));
        foreach (string entry in new[] { "logout", "close", "open" })
        foreach (string change in new[] { "none", "replace", "other-colony", "ended", "expired", "failure", "replace-failure", "compete" })
            Case(() => Preparing(factory, entry, change));
        foreach (string entry in new[] { "logout", "close", "open" }) Case(() => Published(factory, entry));
        foreach (string transport in new[] { "ws", "presence" })
        foreach (string finish in new[] { "normal", "replace", "logout" }) Case(() => LateTransport(factory, transport, finish));
        Case(() => PendingLogout(factory));
        Case(PresenceOwnership);
        Case(TokenOwnership);
        Console.WriteLine($"Session connection ownership scenarios passed: {count}");
    }

    private static void Invalid(Func<string, SaveSnapshotPackage> factory, string route, string invalid)
    {
        using var e = new Env(factory);
        if (invalid == "duplicate" || route == "logout") e.ConnectOwned();
        if (invalid is "expired" or "replace" or "ended") e.Change(invalid);
        e.F.Seed();
        string before = e.F.Evidence(), business = e.Business();
        string user = invalid == "user" ? "intruder" : invalid == "missing-user" ? "" : "receiver";
        string colony = invalid == "colony" ? "other" : invalid == "missing-colony" ? "" : "away";
        string? session = invalid == "session" ? "forged" : invalid == "missing-session" ? null : e.Session;
        if (route == "ws" && invalid != "no-upgrade")
        {
            using var ws = new ClientWebSocket();
            bool rejected = false;
            try { ws.ConnectAsync(e.StreamUri(user, colony, session), CancellationToken.None).GetAwaiter().GetResult(); }
            catch (WebSocketException) { rejected = true; }
            Require(rejected, "Invalid WebSocket admitted: " + invalid);
        }
        else
        {
            using var r = invalid == "no-upgrade" ? e.F.Client.GetAsync(e.StreamUri(user, colony, session).PathAndQuery).GetAwaiter().GetResult()
                : invalid is "malformed" or "empty" or "null" ? PostInvalidBody(e.F.Client, Kind(route), invalid)
                : e.F.Post(Kind(route), new { userId = user, colonyId = colony, sessionId = session });
            if (invalid is "malformed" or "empty" or "null" or "no-upgrade") Require(r.StatusCode == HttpStatusCode.BadRequest, "Malformed request accepted.");
            else Require(!ReadAccepted(r), "Invalid session request accepted: " + route + "/" + invalid);
        }
        e.F.AssertPending(before, route + "/" + invalid);
        Require(business == e.Business(), "Rejected request changed connection or colony state: " + route + "/" + invalid);
    }

    private static void Preparing(Func<string, SaveSnapshotPackage> factory, string entry, string change)
    {
        using var e = new Env(factory);
        if (entry != "open") e.ConnectOwned();
        e.F.Seed();
        string? before = null, business = null;
        bool accepted = false; Exception? failure = null;
        SnapshotPreparationGateProbe.Run(e.F.State.RaidSettlementSnapshotMutationGate, h => e.F.Store.BeforePrepare = h,
            () => { try { accepted = e.Call(entry); } catch (IOException ex) { failure = ex; } },
            () =>
            {
                e.Change(change);
                if (change.Contains("failure")) e.F.Store.FailPreparation = true;
                if (change == "compete") ClashOfRimNetworkServer.RecoverSnapshotCommits(e.F.State);
                before = e.F.Evidence(); business = e.Business(includeOriginal: entry != "close");
            });
        if (change is "none" or "compete")
        {
            Require(accepted && failure is null, "Valid session operation failed: " + entry);
            e.F.AssertRecovered();
            Require(entry == "open" ? e.CurrentValid() && e.F.State.OnlinePresence.IsUserOnline("receiver")
                : !e.CurrentValid() && !e.F.State.OnlinePresence.IsUserOnline("receiver") && e.PendingGone(), "Session transition incomplete.");
            Require(e.TokenValid() == (entry == "open"), "Connection termination retained its token.");
            Require(e.F.Store.RawWrites == (change == "compete" ? 1 : 0), "Session recovery prepared inside the mutation gate.");
        }
        else
        {
            Require(!accepted && (entry == "logout" || (failure is not null) == (change == "failure")), "Session operation ignored invalidation/failure.");
            if (entry == "logout") Require(e.LastStatus == (change == "failure" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK), "Logout hid a recovery failure or failed an obsolete request.");
            e.F.AssertPending(before!, entry + "/" + change);
            if (entry != "close" || change != "failure")
                Require(business == e.Business(includeOriginal: entry != "close"), "Obsolete session operation touched replacement state: " + entry + "/" + change);
            else Require(!e.CurrentValid() && !e.TokenValid() && !e.F.State.OnlinePresence.IsUserOnline("receiver") && !e.PendingGone(), "Broken transport leaked resources after recovery failure.");
            e.F.Store.FailPreparation = false;
            e.F.State.LoginSessions.EndUser("receiver"); e.F.State.OnlinePresence.ForceDisconnect("receiver");
            e.F.RenewToken();
            e.F.State.AuthTokens.TryGetPrincipal(e.F.Token, DateTimeOffset.UtcNow, out var retry);
            using var response = e.F.Post(ProtocolMessageKind.Logout, new LogoutRequest("receiver", "away", retry!.SessionId));
            Require(ReadAccepted(response) && e.PendingGone(), "Fresh session could not resume recovery.");
            e.F.AssertRecovered();
        }
    }

    private static void Published(Func<string, SaveSnapshotPackage> factory, string entry)
    {
        using var e = new Env(factory);
        if (entry != "open") e.ConnectOwned();
        e.F.Seed();
        string? after = null;
        e.Publication = () => { e.Change("replace"); after = e.Business(includeOriginal: entry != "close"); };
        Require(!e.Call(entry), "Recovery callback bypassed session revalidation: " + entry);
        e.F.AssertRecovered();
        Require(after == e.Business(includeOriginal: entry != "close"), "Post-recovery cleanup touched the replacement session.");
    }

    private static void LateTransport(Func<string, SaveSnapshotPackage> factory, string transport, string finish)
    {
        using var e = new Env(factory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var ws = new ClientWebSocket();
        Task<HttpResponseMessage>? pending = null;
        if (transport == "ws")
        {
            ws.ConnectAsync(e.StreamUri("receiver", "away", e.Session), cancellation.Token).GetAwaiter().GetResult();
            ws.ReceiveAsync(new ArraySegment<byte>(new byte[4096]), cancellation.Token).GetAwaiter().GetResult();
        }
        else
        {
            pending = e.F.Client.PostAsJsonAsync(Route(ProtocolMessageKind.MaintainPresence), new MaintainPresenceRequest("receiver", "away", "base", e.Session), cancellation.Token);
            Require(SpinWait.SpinUntil(() => e.F.State.OnlinePresence.IsUserOnline("receiver"), TimeSpan.FromSeconds(5)), "Presence did not connect.");
        }
        if (finish == "logout") Require(e.Call("logout") && e.PendingGone(), "Live logout did not clean up.");
        if (finish != "normal")
        {
            e.Change("replace");
            if (e.PendingGone()) e.SeedLoan();
        }
        string business = e.Business(includeOriginal: false);
        // Close the old transport while its successor owns presence. Real endpoint finally blocks must be harmless.
        if (transport == "ws") ws.Abort();
        else cancellation.Cancel();
        if (pending is not null)
        {
            try { pending.GetAwaiter().GetResult().Dispose(); } catch (OperationCanceledException) { }
        }
        // A real request exercises the route after the old connection began exiting. StopAsync below drains finally blocks.
        if (finish != "normal")
            using (var response = e.F.Post(ProtocolMessageKind.Logout, new LogoutRequest("receiver", "away", e.Session)))
                Require(!ReadAccepted(response), "Stale HTTP logout accepted after transport replacement.");
        e.F.Stop();
        if (finish == "normal") Require(!e.CurrentValid() && !e.TokenValid() && !e.F.State.OnlinePresence.IsUserOnline("receiver") && e.PendingGone(), "Normal transport closure did not clean up.");
        else Require(!e.TokenValid() && business == e.Business(includeOriginal: false), "Late " + transport + " cleanup changed the new session or its pending loan.");
    }

    private static void PendingLogout(Func<string, SaveSnapshotPackage> factory)
    {
        using var e = new Env(factory); e.F.Seed();
        using var first = e.F.Post(ProtocolMessageKind.Logout, new LogoutRequest("receiver", "away", e.Session));
        var reply = first.Content.ReadFromJsonAsync<LogoutResponse>().GetAwaiter().GetResult()!;
        Require(reply.Result.Accepted && !reply.Disconnected && !e.CurrentValid() && !e.TokenValid() && e.PendingGone(), "Pending-session logout failed.");
        e.F.AssertRecovered(); e.SeedLoan(); string business = e.Business();
        Require(!e.Call("logout") && business == e.Business(), "Repeated logout reran lifecycle cleanup.");
    }

    private static void PresenceOwnership()
    {
        var presence = new OnlinePresenceRegistry();
        Require(presence.TryConnectExclusive("user", "old", out var old), "Old lease failed.");
        Require(!presence.DisconnectSession("user", "new") && old!.IsActive(), "Foreign session disconnected presence.");
        Require(presence.DisconnectSession("user", "old"), "Owner did not disconnect presence.");
        Require(presence.TryConnectExclusive("user", "new", out var current), "Replacement lease failed.");
        old!.Dispose();
        Require(!old.Touch() && !old.IsActive() && !presence.DisconnectSession("user", "old") && current!.Touch(), "Old lease changed replacement presence.");
        current!.Dispose();
    }

    private static void TokenOwnership()
    {
        var tokens = new AuthTokenRegistry(); var now = DateTimeOffset.UtcNow;
        string first = tokens.Issue("user", "user", "colony", "old", now);
        string second = tokens.Issue("user", "user", "colony", "old", now);
        string current = tokens.Issue("user", "user", "colony", "new", now);
        string foreign = tokens.Issue("other", "other", "colony", "old", now);
        tokens.RevokeForSession("user", "old");
        Require(!tokens.IsValid(first, "user", "colony", now) && !tokens.IsValid(second, "user", "colony", now)
            && tokens.IsValid(current, "user", "colony", now) && tokens.IsValid(foreign, "other", "colony", now), "Session revocation crossed identity boundaries.");
        tokens.RevokeForSession("user", "old");
        tokens.RevokeForColony("other", "colony");
        Require(tokens.IsValid(current, "user", "colony", now) && !tokens.IsValid(foreign, "other", "colony", now), "Colony revocation lost scope.");
        tokens.RevokeForUser("user"); Require(!tokens.IsValid(current, "user", "colony", now), "User revocation did not revoke remaining tokens.");
    }

    private sealed class Env : IDisposable
    {
        public readonly HttpSnapshotRecoveryTests.Fixture F;
        public readonly string Session;
        public Action? Publication;
        private OnlinePresenceLease? original, replacement, opened;
        private string? replacementSession;
        private string? replacementToken;
        private string replacementColony = "away";
        public HttpStatusCode? LastStatus;
        public Env(Func<string, SaveSnapshotPackage> factory)
        {
            F = new(factory, plugins: PublicationPlugins("session-publication", () => Publication?.Invoke()));
            F.State.AuthTokens.TryGetPrincipal(F.Token, DateTimeOffset.UtcNow, out var principal);
            Session = principal!.SessionId; F.Start(); SeedLoan();
        }
        public void SeedLoan() => F.State.BankLoans.Create("pending", "receiver", "away", "base", 100, 10, 7, 1000, DateTimeOffset.UtcNow.AddHours(-1));
        public bool PendingGone() => F.State.BankLoans.FindByIdempotencyKey("pending") is null;
        public bool CurrentValid() => F.State.LoginSessions.IsValid("receiver", "away", Session, DateTimeOffset.UtcNow);
        public bool TokenValid() => F.State.AuthTokens.IsValid(F.Token, "receiver", "away", DateTimeOffset.UtcNow);
        public string Business(bool includeOriginal = true) => JsonSerializer.Serialize(new
        {
            loan = F.State.BankLoans.FindByIdempotencyKey("pending"), players = F.State.Players.List(),
            original = includeOriginal ? original?.IsActive() : null, replacement = replacement?.IsActive(),
            token = includeOriginal ? (bool?)TokenValid() : null,
            successorToken = replacementToken is not null && F.State.AuthTokens.IsValid(replacementToken, "receiver", replacementColony, DateTimeOffset.UtcNow),
            current = CurrentValid(), successor = replacementSession is not null && F.State.LoginSessions.IsValid("receiver", replacementColony, replacementSession, DateTimeOffset.UtcNow)
        });
        public void ConnectOwned()
        {
            Require(F.State.LoginSessions.TryBeginStream("receiver", "away", Session, DateTimeOffset.UtcNow), "Could not begin stream.");
            Require(F.State.OnlinePresence.TryConnectExclusive("receiver", Session, out original), "Could not create presence.");
        }
        public void Change(string change)
        {
            if (change == "expired") F.State.LoginSessions.IsValid("receiver", "away", Session, DateTimeOffset.UtcNow.AddHours(1));
            if (change == "ended") F.State.LoginSessions.End("receiver", Session);
            if (change is "replace" or "replace-failure" or "other-colony")
            {
                using var mutation = F.State.EnterSnapshotMutation();
                F.State.LoginSessions.EndUser("receiver"); F.State.OnlinePresence.ForceDisconnect("receiver");
                replacementColony = change == "other-colony" ? "elsewhere" : "away";
                Require(F.State.LoginSessions.TryCreate("receiver", replacementColony, DateTimeOffset.UtcNow, out replacementSession), "Could not replace session.");
                replacementToken = F.State.AuthTokens.Issue("receiver", "receiver", replacementColony, replacementSession!, DateTimeOffset.UtcNow);
                Require(F.State.LoginSessions.TryBeginStream("receiver", replacementColony, replacementSession, DateTimeOffset.UtcNow), "Could not start successor.");
                Require(F.State.OnlinePresence.TryConnectExclusive("receiver", replacementSession, out replacement), "Could not connect successor.");
            }
        }
        public bool Call(string entry)
        {
            if (entry == "logout")
            {
                using var response = F.Post(ProtocolMessageKind.Logout, new LogoutRequest("receiver", "away", Session));
                LastStatus = response.StatusCode;
                return response.StatusCode == HttpStatusCode.OK && ReadAccepted(response);
            }
            if (entry == "close")
            {
                bool owned = CurrentValid();
                F.State.SessionConnections.Close("receiver", "away", Session, original);
                return owned && replacementSession is null && PendingGone();
            }
            return F.State.SessionConnections.Open("receiver", "away", Session, "base", out opened) is null;
        }
        public Uri StreamUri(string user, string colony, string? session)
            => new UriBuilder(F.Client.BaseAddress!) { Scheme = "ws", Path = Route(ProtocolMessageKind.StreamSession),
                Query = "userId=" + Uri.EscapeDataString(user) + "&colonyId=" + Uri.EscapeDataString(colony) + "&sessionId=" + Uri.EscapeDataString(session ?? "") }.Uri;
        public void Dispose() { opened?.Dispose(); original?.Dispose(); replacement?.Dispose(); F.Dispose(); }
    }
    private static ProtocolMessageKind Kind(string route) => route == "logout" ? ProtocolMessageKind.Logout : ProtocolMessageKind.MaintainPresence;
    private static string Route(ProtocolMessageKind kind) => ProtocolContractManifest.Find(kind).Route;
    private static bool ReadAccepted(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        return json.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean();
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
