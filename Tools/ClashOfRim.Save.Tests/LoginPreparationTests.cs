using System.Collections;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class LoginPreparationTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        int count = 0; void Case(Action action) { action(); count++; }
        foreach (string mode in new[] { "protocol", "user", "colony", "password", "account", "manifest", "manifest-null", "manifest-missing", "shape", "mismatch", "ban", "maintenance", "lockout", "active", "malformed", "null", "empty" }) Case(() => Invalid(factory, mode));
        foreach (string mode in new[] { "none", "password", "same-password", "lockout", "auth-mode", "steam-key", "app-id", "ban", "maintenance", "role", "mismatch", "compatible", "active", "failure", "publication-failure", "compete" }) Case(() => Preparing(factory, mode));
        foreach (string mode in new[] { "password", "same-password", "lockout", "auth-mode", "ban", "maintenance", "role", "mismatch", "active" }) Case(() => Published(factory, mode));
        foreach (string mode in new[] { "failure", "active", "refresh", "replace", "logout", "ban", "maintenance", "password", "same-password", "mismatch", "defense" }) Case(() => DuringPlayerWrite(factory, mode));
        foreach (string mode in new[] { "valid", "identity", "denied", "config", "recovery-config", "publication-ban" }) Case(() => Steam(factory, mode));
        foreach (string mode in new[] { "normal", "id", "summary", "trimmed", "administrator", "fresh", "fresh-ban", "fresh-maintenance", "baseline-failure" }) Case(() => Success(factory, mode));
        foreach (string mode in new[] { "existing", "arrives", "closes", "administrator" }) Case(() => Defense(factory, mode));
        foreach (string mode in new[] { "normal", "failure", "ban" }) Case(() => Lifecycle(factory, mode));
        foreach (string mode in new[] { "normal", "failure", "ban" }) Case(() => FirstBaseline(factory, mode));
        foreach (string mode in new[] { "empty", "pending", "active", "expired", "stream", "refresh", "replace", "logout", "competing", "ttl", "expired-cleanup", "republish" }) Case(() => Registry(mode));
        Case(() => Concurrent(factory));
        Console.WriteLine($"Login preparation scenarios passed: {count}");
    }

    private static void Invalid(Func<string, SaveSnapshotPackage> factory, string mode)
    {
        using var e = new Env(factory); Change(e, mode); string before = e.F.Evidence(), domain = e.Domain();
        using var response = e.Call(mode);
        if (mode is "malformed" or "null" or "empty") Require(response.StatusCode == HttpStatusCode.BadRequest, "Invalid body not rejected.");
        else Denied(response);
        e.F.AssertPending(before, "login/" + mode); Require(e.Domain() == domain && e.OldValid(), "Rejected login changed the session or domain.");
    }
    private static void Preparing(Func<string, SaveSnapshotPackage> factory, string mode)
    {
        using var e = new Env(factory); if (mode == "role") e.F.State.WorldConfiguration.PromoteAdministrator("receiver");
        string? before = null, domain = null; HttpResponseMessage? response = null;
        SnapshotPreparationGateProbe.Run(e.F.State.RaidSettlementSnapshotMutationGate, hook => e.F.Store.BeforePrepare = hook, () => response = e.Call(), () =>
        {
            Change(e, mode); e.F.Store.FailPreparation = mode == "failure"; e.F.Store.FailPublication = mode == "publication-failure";
            if (mode == "compete") ClashOfRimNetworkServer.RecoverSnapshotCommits(e.F.State);
            before = e.F.Evidence(); domain = e.Domain(); Require(e.OldValid(), "Preparation displaced a session before recovery.");
        });
        using (response)
        {
            if (mode is "none" or "compatible" or "compete") { e.Accepted(response!); e.F.AssertRecovered(); }
            else
            {
                if (mode is "failure" or "publication-failure") Require(response!.StatusCode == HttpStatusCode.InternalServerError, "Recovery failure was swallowed."); else Denied(response!);
                e.F.AssertPending(before!, "login/preparing/" + mode); Require(e.OldValid() && e.Domain() == domain, "Rejected preparation changed session state.");
                e.Restore(); using var retry = e.Call(); e.Accepted(retry); e.F.AssertRecovered();
            }
        }
    }
    private static void Published(Func<string, SaveSnapshotPackage> factory, string mode)
    {
        using var e = new Env(factory); if (mode == "role") e.F.State.WorldConfiguration.PromoteAdministrator("receiver");
        string? domain = null; e.Publication = () => { Change(e, mode); domain = e.Domain(); };
        using var response = e.Call(); Denied(response); e.F.AssertRecovered();
        Require(e.OldValid() && e.Domain() == domain, "Recovery publication bypassed authorization.");
        e.Publication = null; e.Restore(); using var retry = e.Call(); e.Accepted(retry);
    }
    private static void DuringPlayerWrite(Func<string, SaveSnapshotPackage> factory, string mode)
    {
        using var e = new Env(factory); int tokens = TokenCount(e.F.State); string? replacement = null; bool invoked = false;
        e.Players.Before = json =>
        {
            if (!json.Contains("\"receiver\"", StringComparison.Ordinal)) return;
            e.Players.Before = null; invoked = true;
            Require(e.OldValid(), "New login displaced the pending session before player persistence.");
            if (mode == "failure") throw new IOException("Injected player persistence failure.");
            if (mode == "refresh") Require(e.F.State.LoginSessions.Refresh("receiver", "away", e.OldSession, DateTimeOffset.UtcNow), "Refresh failed.");
            else if (mode == "replace") Require(e.F.State.LoginSessions.TryCreate("receiver", "away", DateTimeOffset.UtcNow, out replacement), "Replacement failed.");
            else if (mode == "logout") e.F.State.LoginSessions.EndUser("receiver");
            else if (mode == "defense") e.F.State.Ledger.Append(Raid());
            else Change(e, mode);
        };
        using var response = e.Call();
        if (mode == "failure") Require(response.StatusCode == HttpStatusCode.InternalServerError, "Player persistence failure swallowed."); else Denied(response);
        Require(invoked && TokenCount(e.F.State) == tokens, "Failure leaked an unreturned auth token or missed write boundary.");
        if (mode == "logout") Require(!e.OldValid(), "Login undid logout.");
        else if (mode == "replace") Require(e.F.State.LoginSessions.IsValid("receiver", "away", replacement, DateTimeOffset.UtcNow), "Login overwrote another login.");
        else Require(e.OldValid(), "Failure lost the prior session.");
        e.F.AssertRecovered();
        if (mode == "defense") foreach (var raid in e.F.State.Ledger.ListByTypeForTarget(ServerEventType.Raid, "receiver", "away")) e.F.State.Ledger.ChangeStatus(raid.EventId, ServerEventStatus.Cancelled);
        e.Restore(); using var retry = e.Call(); e.Accepted(retry);
    }
    private static void Steam(Func<string, SaveSnapshotPackage> factory, string mode)
    {
        var validator = new SteamValidator(); using var e = new Env(factory, steam: validator);
        e.F.State.UpdateServerConfiguration(new(authenticationDebugMode: true));
        validator.Before = () => { Require(!Monitor.IsEntered(e.F.State.RaidSettlementSnapshotMutationGate), "Steam validation held global gate."); if (mode == "config") e.F.State.UpdateServerConfiguration(new()); };
        validator.Identity = mode == "identity" ? "other" : "receiver"; validator.Deny = mode == "denied";
        if (mode == "publication-ban") e.Publication = () => e.F.State.AdminControl.Ban("receiver");
        string before = e.F.Evidence(); HttpResponseMessage? response = null;
        if (mode == "recovery-config") SnapshotPreparationGateProbe.Run(e.F.State.RaidSettlementSnapshotMutationGate, hook => e.F.Store.BeforePrepare = hook,
            () => response = e.Call(), () => e.F.State.UpdateServerConfiguration(new()));
        else response = e.Call();
        using (response) { if (mode == "valid") e.Accepted(response!); else { Denied(response!); Require(e.OldValid(), "Steam rejection replaced session."); } }
        Require(validator.Calls == 1 && e.F.State.Players.FindByUserId("other") is null, "Steam ticket repeated or subject binding failed.");
        if (mode is "valid" or "publication-ban") e.F.AssertRecovered(); else e.F.AssertPending(before, "login/steam/" + mode);
    }
    private static void Success(Func<string, SaveSnapshotPackage> factory, string mode)
    {
        bool fresh = mode.StartsWith("fresh", StringComparison.Ordinal) || mode == "baseline-failure";
        using var e = new Env(factory, fresh); string before = e.F.Evidence();
        if (mode == "administrator") { e.F.State.WorldConfiguration.PromoteAdministrator("receiver"); e.F.State.AdminControl.SetMaintenanceLoginLocked(true, "maintenance"); }
        if (mode == "fresh-ban") e.F.State.AdminControl.Ban("receiver");
        if (mode == "fresh-maintenance") e.F.State.AdminControl.SetMaintenanceLoginLocked(true, "maintenance");
        if (mode == "baseline-failure") e.Baseline.Fail = true;
        using var response = e.Call(mode);
        if (mode is "fresh-ban" or "fresh-maintenance") { Denied(response); e.F.AssertPending(before, mode); Require(e.Baseline.Read() is null && e.OldValid(), "Rejected login published baseline/session."); }
        else if (mode == "baseline-failure")
        {
            Require(response.StatusCode == HttpStatusCode.InternalServerError && e.Baseline.Read() is null && e.OldValid(), "Baseline failure replaced session/cache.");
            e.Baseline.Fail = false; using var retry = e.Call(); e.Accepted(retry); e.F.AssertRecovered();
        }
        else
        {
            var body = e.Accepted(response); e.F.AssertRecovered();
            Require(body.IsAdministrator == (mode == "administrator") && body.CanOverrideCompatibilityBaseline == body.IsAdministrator
                && body.EventQueue is not null && body.WorldMapMarkers is not null && e.F.State.Players.FindByUserId("receiver")?.ColonyId == "away", "Successful login lost its business payload.");
            if (fresh) Require(e.Baseline.Read() is not null && e.F.State.WorldConfiguration.ListAdministrators().Count == 0, "Login bootstrapped an administrator.");
        }
    }
    private static void Defense(Func<string, SaveSnapshotPackage> factory, string mode)
    {
        using var e = new Env(factory); var raid = Raid(); if (mode == "administrator") e.F.State.WorldConfiguration.PromoteAdministrator("receiver");
        if (mode != "arrives") e.F.State.Ledger.Append(raid);
        HttpResponseMessage? response = null;
        SnapshotPreparationGateProbe.Run(e.F.State.RaidSettlementSnapshotMutationGate, hook => e.F.Store.BeforePrepare = hook, () => response = e.Call(), () =>
        { if (mode == "arrives") e.F.State.Ledger.Append(raid); if (mode == "closes") e.F.State.Ledger.ChangeStatus(raid.EventId, ServerEventStatus.Cancelled); });
        using (response)
        {
            if (mode == "closes") e.Accepted(response!);
            else { var b = Denied(response!); Require(e.OldValid() && b.AuthenticatedUserId == "receiver" && b.IsAdministrator == (mode == "administrator") && e.F.State.Players.FindByUserId("receiver") is null, "Defense rejection lost identity or ran login effects."); }
        }
        e.F.AssertRecovered();
    }
    private static void FirstBaseline(Func<string, SaveSnapshotPackage> factory, string mode)
    {
        using var e = new Env(factory, fresh: true); HttpResponseMessage? response = null;
        SnapshotPreparationGateProbe.Run(e.F.State.RaidSettlementSnapshotMutationGate, hook => e.F.Store.BeforePrepare = hook, () => response = e.Call(), () =>
        {
            Require(e.Baseline.Read() is null && e.F.State.CompatibilityBaseline.Current is null && e.OldValid(), "Login published first baseline/session before recovery.");
            if (mode == "failure") e.F.Store.FailPreparation = true;
            if (mode == "ban") e.F.State.AdminControl.Ban("receiver");
        });
        using (response)
        {
            if (mode == "normal") { e.Accepted(response!); e.F.AssertRecovered(); Require(e.Baseline.Read() is not null, "First login omitted baseline."); }
            else
            {
                if (mode == "failure") Require(response!.StatusCode == HttpStatusCode.InternalServerError, "Preparation failure swallowed."); else Denied(response!);
                Require(e.Baseline.Read() is null && e.F.State.CompatibilityBaseline.Current is null && e.OldValid(), "Failed first login published baseline/session.");
            }
        }
    }
    private static void Lifecycle(Func<string, SaveSnapshotPackage> factory, string mode)
    {
        using var e = new Env(factory); var bank = e.F.State.BankLoans; int tokens = TokenCount(e.F.State);
        bank.Create("pending", "receiver", "away", "base", 100, 1, 7, 1000, DateTimeOffset.UtcNow.AddHours(-1));
        bool invoked = false; e.Bank.Before = _ =>
        {
            invoked = true; Require(e.OldValid(), "Lifecycle hook saw a prematurely published login.");
            if (mode == "failure") throw new IOException("Injected lifecycle persistence failure.");
            if (mode == "ban") e.F.State.AdminControl.Ban("receiver");
        };
        using var response = e.Call();
        Require(invoked, "Login skipped pending-confirmation lifecycle reconciliation.");
        if (mode == "normal") e.Accepted(response);
        else
        {
            if (mode == "failure") Require(response.StatusCode == HttpStatusCode.InternalServerError && bank.FindByIdempotencyKey("pending") is not null, "Failed lifecycle cleanup lost its pending record.");
            else Denied(response);
            Require(e.OldValid() && TokenCount(e.F.State) == tokens, "Failed lifecycle published session or token.");
            e.Bank.Before = null; e.Restore(); using var retry = e.Call(); e.Accepted(retry);
        }
        Require(bank.FindByIdempotencyKey("pending") is null, "Successful lifecycle did not clear pending confirmation."); e.F.AssertRecovered();
    }
    private static void Registry(string mode)
    {
        var sessions = new LoginSessionRegistry(); var now = DateTimeOffset.UtcNow; string old = "";
        if (mode is not ("empty" or "republish")) sessions.TryCreate("u", "c", mode is "expired" or "expired-cleanup" ? now.AddMinutes(-3) : now, out old);
        if (mode == "active") sessions.TryBeginStream("u", "c", old, now);
        var candidate = sessions.Prepare("u", "c", now);
        if (mode == "active") { Require(candidate is null && !sessions.CanCreate("u", now), "Active stream was replaceable."); return; }
        Require(candidate is not null && !sessions.IsValid("u", "c", candidate.SessionId, now), "Prepared session was visible.");
        if (mode == "republish")
        {
            Require(candidate!.TryPublish(now), "Initial candidate publication failed."); sessions.EndUser("u");
            Require(!candidate.TryPublish(now) && !sessions.IsValid("u", "c", candidate.SessionId, now), "Consumed candidate revived a logged-out session."); return;
        }
        // IsValid above purges expired records. Use a fresh capture for the success expiry case.
        if (mode == "expired") candidate = sessions.Prepare("u", "c", now);
        if (mode == "stream") sessions.TryBeginStream("u", "c", old, now);
        if (mode == "refresh") sessions.Refresh("u", "c", old, now);
        if (mode == "replace") sessions.TryCreate("u", "c", now, out old);
        if (mode == "logout") sessions.EndUser("u");
        if (mode == "competing") { var other = sessions.Prepare("u", "c", now)!; Require(other.TryPublish(now), "Competing publication failed."); old = other.SessionId; }
        bool succeeds = mode is "empty" or "pending" or "expired" or "ttl";
        var published = mode == "ttl" ? now.AddHours(1) : now;
        Require(candidate!.TryPublish(published) == succeeds, "Session publication ignored its captured predecessor: " + mode);
        Require(!candidate.TryPublish(published), "A prepared session published twice.");
        if (succeeds) Require(sessions.IsValid("u", "c", candidate.SessionId, published.AddSeconds(119)) && !sessions.IsValid("u", "c", candidate.SessionId, published.AddMinutes(2)), "Pending TTL did not start at publication.");
        else if (mode is not ("logout" or "expired-cleanup")) Require(sessions.IsValid("u", "c", old, now), "Conflict destroyed the winner.");
    }
    private static void Concurrent(Func<string, SaveSnapshotPackage> factory)
    {
        using var e = new Env(factory);
        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(() => { using var r = e.Call(); return Body(r); })).ToArray(); Task.WaitAll(tasks);
        var accepted = tasks.Select(t => t.Result).Where(r => r.Result.Accepted).ToArray();
        Require(accepted.Length > 0 && accepted.Count(r => e.F.State.LoginSessions.IsValid("receiver", "away", r.SessionId, DateTimeOffset.UtcNow)) == 1, "Concurrent pending logins did not leave exactly one winner.");
        Require(accepted.Select(r => r.SessionId).Distinct().Count() == accepted.Length, "Session ids were reused."); e.F.AssertRecovered();
    }
    private static void Change(Env e, string mode)
    {
        var s = e.F.State;
        if (mode is "password" or "same-password") s.OfflineAccounts.ResetPassword("receiver", mode == "password" ? "changed" : "pw", DateTimeOffset.UtcNow, out _);
        if (mode == "lockout") for (int i = 0; i < 5; i++) s.OfflineAccounts.Authenticate("receiver", "wrong", DateTimeOffset.UtcNow);
        if (mode == "auth-mode") s.UpdateServerConfiguration(new(authenticationDebugMode: true));
        if (mode == "steam-key") s.UpdateServerConfiguration(new(steamWebApiKey: "changed"));
        if (mode == "app-id") s.UpdateServerConfiguration(new(steamAppId: 1));
        if (mode == "ban") s.AdminControl.Ban("receiver");
        if (mode is "maintenance" or "role") s.AdminControl.SetMaintenanceLoginLocked(true, "maintenance");
        if (mode == "role") s.WorldConfiguration.RevokeAdministrator("receiver");
        if (mode is "mismatch" or "compatible") s.CompatibilityBaseline.ReplaceBaseline(Manifest("updated", mode == "mismatch" ? "different" : "1.7"), "admin", DateTimeOffset.UtcNow);
        if (mode == "active") Require(s.LoginSessions.TryBeginStream("receiver", "away", e.OldSession, DateTimeOffset.UtcNow), "Cannot start original stream.");
    }
    private static AuthoritativeEvent Raid() { var now = DateTimeOffset.UtcNow; return AuthoritativeEventFactory.Create(ServerEventType.Raid, new("attacker", "home"), new("receiver", "away"), "raid", false, new RaidEventPayload("defender", null, now, null, null, new RaidAttackForceRecord("departed", ["pawn"], [])), now); }
    private static CompatibilityManifest Manifest(string id = "base", string version = "1.7") => new() { ManifestId = id, ProtocolVersion = ProtocolApiVersion.Current, RimWorldVersion = version };
    private static int TokenCount(ClashOfRimNetworkState state) => ((IDictionary)typeof(AuthTokenRegistry).GetField("tokens", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state.AuthTokens)!).Count;
    private static LoginResponse Body(HttpResponseMessage response) { response.EnsureSuccessStatusCode(); return JsonSerializer.Deserialize<LoginResponse>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!; }
    private static LoginResponse Denied(HttpResponseMessage response) { var b = Body(response); Require(!b.Result.Accepted && b.SessionId is null && b.AuthToken is null && b.EventQueue is null && b.WorldMapMarkers is null, "Denied login disclosed a session."); return b; }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Slot : IJsonPersistenceSlot
    { private string? value; public bool Fail; public Action<string>? Before; public string? Read() => value; public void Write(string json) { Before?.Invoke(json); if (Fail) throw new IOException("Injected login persistence failure."); value = json; } }
    private sealed class Env : IDisposable
    {
        public readonly HttpSnapshotRecoveryTests.Fixture F; public readonly Slot Baseline = new(), Players = new(), Bank = new(); public Action? Publication;
        public string OldSession = "";
        public Env(Func<string, SaveSnapshotPackage> factory, bool fresh = false, SteamValidator? steam = null)
        {
            var plugins = new ServerPluginRegistry().WithBuiltInPlugins([new("test.login", "test", "1", "test", "", []) { SnapshotPostUploadProcessors = [new PublicationProbe(() => Publication?.Invoke())] }]);
            F = new(factory, plugins, compatibility: new CompatibilityBaselineRegistry(Baseline), steam: steam, players: new PlayerRegistry(Players), bank: new BankLoanRegistry(Bank)); F.Start();
            F.State.OfflineAccounts.Authenticate("receiver", "pw", DateTimeOffset.UtcNow, true);
            if (!fresh) { F.State.CompatibilityBaseline.EnsureBaseline(Manifest(), "admin", DateTimeOffset.UtcNow); F.State.WorldConfiguration.Prepare("admin"); }
            CaptureOld(); F.Seed();
        }
        private void CaptureOld() { Require(F.State.AuthTokens.TryGetPrincipal(F.Token, DateTimeOffset.UtcNow, out var principal), "No fixture token."); OldSession = principal!.SessionId; }
        public bool OldValid() => F.State.LoginSessions.IsValid("receiver", "away", OldSession, DateTimeOffset.UtcNow) && F.State.AuthTokens.IsValid(F.Token, "receiver", "away", DateTimeOffset.UtcNow);
        public string Domain() => JsonSerializer.Serialize(new { baseline = Baseline.Read(), admins = F.State.WorldConfiguration.ListAdministrators(), player = F.State.Players.FindByUserId("receiver"), tokens = TokenCount(F.State) });
        public LoginResponse Accepted(HttpResponseMessage response)
        {
            var b = Body(response); Require(b.Result.Accepted, "Login rejected: " + b.Result.Message);
            Require(b.AuthenticatedUserId == "receiver" && F.State.AuthTokens.TryGetPrincipal(b.AuthToken, DateTimeOffset.UtcNow, out var p) && p!.SessionId == b.SessionId && p.UserId == "receiver" && p.ColonyId == "away"
                && F.State.LoginSessions.IsValid("receiver", "away", b.SessionId, DateTimeOffset.UtcNow) && !F.State.LoginSessions.IsValid("receiver", "away", OldSession, DateTimeOffset.UtcNow), "Login returned an unbound session/token or retained its predecessor."); return b;
        }
        public void Restore()
        {
            F.Store.FailPreparation = F.Store.FailPublication = false; F.State.UpdateServerConfiguration(new()); F.State.AdminControl.Unban("receiver"); F.State.AdminControl.SetMaintenanceLoginLocked(false, null);
            F.State.OfflineAccounts.ResetPassword("receiver", "pw", DateTimeOffset.UtcNow, out _); F.State.OfflineAccounts.Authenticate("receiver", "pw", DateTimeOffset.UtcNow.AddHours(1));
            F.State.CompatibilityBaseline.ReplaceBaseline(Manifest(), "admin", DateTimeOffset.UtcNow); F.RenewToken(); CaptureOld();
        }
        public HttpResponseMessage Call(string mode = "")
        {
            if (mode is "malformed" or "null" or "empty") return F.Client.PostAsync(ProtocolContractManifest.Find(ProtocolMessageKind.Login).Route, new StringContent(mode == "malformed" ? "{\"" : mode == "null" ? "null" : "", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            return F.Post(ProtocolMessageKind.Login, new LoginRequest(mode == "protocol" ? "wrong" : ProtocolApiVersion.Current,
                mode == "user" ? "" : mode == "account" ? "unknown" : mode == "trimmed" ? " receiver " : "receiver", mode == "colony" ? "" : "away", null, "",
                password: mode == "password" ? "wrong" : "pw", compatibilityManifestJson: mode == "manifest" ? "{" : mode == "manifest-null" ? "null" : mode == "shape" ? "{\"Mods\":null}" : mode is "manifest-missing" or "id" or "summary" ? null : JsonSerializer.Serialize(Manifest()),
                compatibilityManifestId: mode == "id" ? "base" : null, compatibilityManifestSummaryJson: mode == "summary" ? JsonSerializer.Serialize(new CompatibilityManifestSummary { ManifestId = "base", ProtocolVersion = ProtocolApiVersion.Current, RimWorldVersion = "1.7" }) : null));
        }
        public void Dispose() => F.Dispose();
    }
    private sealed class SteamValidator : ISteamAuthTicketValidator
    { public string Identity = "receiver"; public bool Deny; public int Calls; public Action? Before; public SteamAuthTicketValidationResult Validate(string userId, string? ticket) { Calls++; Before?.Invoke(); return Deny ? SteamAuthTicketValidationResult.Reject("denied") : SteamAuthTicketValidationResult.Accept(Identity); } }
    private sealed class PublicationProbe(Action callback) : IInlineSnapshotPostUploadProcessor
    {
        public string Id => "test.login"; public int Order => 350; public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.EventReconciliation;
        public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline; public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Inline;
        public bool Supports(SnapshotPostUploadKind kind) => true; public void Process(SnapshotPostUploadContext context) => callback();
    }
}
