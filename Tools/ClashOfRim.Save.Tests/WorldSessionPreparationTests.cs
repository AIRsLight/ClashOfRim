using static EntryTestSupport;
using static RecoveryTestSupport;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class WorldSessionPreparationTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        int count = 0; void Case(Action run) { run(); count++; }
        foreach (string invalid in new[] { "protocol", "user", "password", "account", "manifest", "manifest-null", "manifest-missing", "mismatch", "ban", "maintenance", "lockout", "malformed", "null", "empty" }) Case(() => Invalid(factory, invalid));
        foreach (string change in new[] { "none", "password", "same-password", "lockout", "auth-mode", "steam-key", "app-id", "ban", "maintenance", "role", "mismatch", "compatible", "failure", "publication-failure", "compete" }) Case(() => Preparing(factory, change));
        foreach (string change in new[] { "password", "same-password", "lockout", "auth-mode", "ban", "maintenance", "role", "mismatch" }) Case(() => Published(factory, change));
        foreach (string mode in new[] { "normal", "missing-manifest", "invalid-manifest", "preparation-failure", "account-failure", "baseline-failure", "competing-account", "competing-baseline" }) Case(() => Signup(factory, mode));
        foreach (string mode in new[] { "valid", "identity", "denied", "config", "recovery-config", "publication-ban" }) Case(() => Steam(factory, mode));
        foreach (string mode in new[] { "id", "summary", "trimmed", "default", "snapshot", "changed-snapshot", "deleted-colony" }) Case(() => ExistingColony(factory, mode));
        Case(() => ConcurrentSignup(factory));
        foreach (bool fresh in new[] { false, true })
        foreach (string shape in new[] { "schema", "id", "protocol", "version", "mods-null", "mods-item", "mods-duplicate", "dlc-null", "dlc-duplicate", "defs-null", "defs-duplicate", "files-null", "files-duplicate", "configs-null", "configs-duplicate", "rules-null" }) Case(() => InvalidShape(factory, fresh, shape));
        foreach (string mode in new[] { "summary", "summary-id-cache" }) Case(() => InvalidShape(factory, false, mode));
        foreach (string mode in new[] { "existing", "arrives", "closes", "administrator" }) Case(() => Defense(factory, mode));
        foreach (string mode in new[] { "uncommitted", "reset", "same-password", "compete", "lockout", "failure", "repeat" }) Case(() => CredentialProof(mode));
        foreach (string mode in new[] { "bootstrap-maintenance", "active-session" }) Case(() => WorldEntryPolicy(factory, mode));
        Console.WriteLine($"World session preparation scenarios passed: {count}");
    }
    private static void WorldEntryPolicy(Func<string, SaveSnapshotPackage> factory, string mode)
    {
        using var e = new Env(factory, fresh: mode == "bootstrap-maintenance");
        e.F.State.AuthTokens.TryGetPrincipal(e.F.Token, DateTimeOffset.UtcNow, out var principal);
        if (mode == "bootstrap-maintenance") e.F.State.AdminControl.SetMaintenanceLoginLocked(true, "maintenance");
        else Require(e.F.State.LoginSessions.TryBeginStream("receiver", "away", principal!.SessionId, DateTimeOffset.UtcNow), "Cannot establish existing stream.");
        using var response = e.Call(); var body = Accepted(response);
        Require(body.IsAdministrator == (mode == "bootstrap-maintenance")
            && e.F.State.LoginSessions.IsValid("receiver", "away", principal!.SessionId, DateTimeOffset.UtcNow), "World preparation inherited login-only session policy or lost bootstrap exception.");
        e.F.AssertRecovered();
    }
    private static void Invalid(Func<string, SaveSnapshotPackage> factory, string invalid)
    {
        using var e = new Env(factory); Change(e, invalid); string before = e.F.Evidence(), domain = e.Domain();
        using var response = e.Call(invalid);
        if (invalid is "malformed" or "null" or "empty") Require(response.StatusCode == HttpStatusCode.BadRequest, "Malformed world session was not rejected.");
        else Denied(response);
        e.F.AssertPending(before, "world-session/" + invalid); Require(e.Domain() == domain, "Rejected world preparation changed domain state.");
    }
    private static void Preparing(Func<string, SaveSnapshotPackage> factory, string change)
    {
        using var e = new Env(factory); if (change == "role") e.F.State.WorldConfiguration.PromoteAdministrator("receiver");
        string? before = null, domain = null; HttpResponseMessage? response = null;
        SnapshotPreparationGateProbe.Run(e.F.State.RaidSettlementSnapshotMutationGate, hook => e.F.Store.BeforePrepare = hook, () => response = e.Call(), () =>
        {
            Change(e, change); if (change == "failure") e.F.Store.FailPreparation = true;
            if (change == "publication-failure") e.F.Store.FailPublication = true;
            if (change == "compete") e.F.State.SnapshotMutations.Recover();
            before = e.F.Evidence(); domain = e.Domain();
        });
        using (response)
        {
            if (change is "none" or "compatible" or "compete") { Accepted(response!); e.F.AssertRecovered(); }
            else
            {
                if (change is "failure" or "publication-failure") Require(response!.StatusCode == HttpStatusCode.InternalServerError, "Recovery failure was swallowed."); else Denied(response!);
                e.F.AssertPending(before!, "world-session/preparing/" + change); Require(e.Domain() == domain, "Rejected or failed preparation changed session state.");
                e.Restore(); using var retry = e.Call(); Accepted(retry); e.F.AssertRecovered();
            }
        }
    }
    private static void Published(Func<string, SaveSnapshotPackage> factory, string change)
    {
        using var e = new Env(factory); if (change == "role") e.F.State.WorldConfiguration.PromoteAdministrator("receiver");
        string? domain = null; e.Publication = () => { Change(e, change); domain = e.Domain(); };
        using var response = e.Call(); Denied(response); e.F.AssertRecovered(); Require(e.Domain() == domain, "Publication bypassed final authorization.");
        e.Publication = null; e.Restore(); using var retry = e.Call(); Accepted(retry);
    }
    private static void Signup(Func<string, SaveSnapshotPackage> factory, string mode)
    {
        using var e = new Env(factory, fresh: true); string before = e.F.Evidence(); HttpResponseMessage? response = null;
        if (mode is "missing-manifest" or "invalid-manifest")
        {
            using var invalid = e.Call(mode == "missing-manifest" ? "manifest-missing" : "manifest"); Denied(invalid);
            e.F.AssertPending(before, "signup/" + mode); Require(e.Account.Read() is null && e.Baseline.Read() is null && e.F.State.WorldConfiguration.ListAdministrators().Count == 0, "Incompatible signup registered an account or baseline.");
        }
        else
        {
            SnapshotPreparationGateProbe.Run(e.F.State.RaidSettlementSnapshotMutationGate, hook => e.F.Store.BeforePrepare = hook, () => response = e.Call(), () =>
            {
                Require(e.Account.Read() is null && e.Baseline.Read() is null && e.F.State.WorldConfiguration.ListAdministrators().Count == 0, "Signup persisted before authorized recovery.");
                if (mode == "preparation-failure") e.F.Store.FailPreparation = true;
                if (mode == "account-failure") e.Account.Fail = true;
                if (mode == "baseline-failure") e.Baseline.Fail = true;
                if (mode == "competing-account") e.F.State.OfflineAccounts.Authenticate("receiver", "other", DateTimeOffset.UtcNow, true);
                if (mode == "competing-baseline") e.F.State.CompatibilityBaseline.EnsureBaseline(Manifest("other", "different"), "other", DateTimeOffset.UtcNow);
            });
            using (response)
            {
                if (mode == "normal") Accepted(response!);
                else if (mode.EndsWith("failure", StringComparison.Ordinal)) Require(response!.StatusCode == HttpStatusCode.InternalServerError, "Signup write failure was swallowed.");
                else Denied(response!);
            }
            if (mode is "account-failure" or "baseline-failure") e.F.AssertRecovered();
            if (mode != "normal") Require(e.F.State.WorldConfiguration.ListAdministrators().Count == 0 && e.F.State.Players.FindByUserId("receiver") is null, "Failed signup initialized a world session.");
            if (mode is "preparation-failure" or "account-failure" or "competing-baseline") Require(e.Account.Read() is null, "Failed signup published an account.");
            if (mode is "preparation-failure" or "account-failure" or "baseline-failure" or "competing-account") Require(e.Baseline.Read() is null, "Failed signup published a compatibility baseline.");
        }
        e.Account.Fail = e.Baseline.Fail = e.F.Store.FailPreparation = false;
        if (mode == "competing-account") e.F.State.OfflineAccounts.ResetPassword("receiver", "pw", DateTimeOffset.UtcNow, out _);
        if (mode == "competing-baseline") e.F.State.CompatibilityBaseline.ReplaceBaseline(Manifest(), "other", DateTimeOffset.UtcNow);
        using var success = e.Call(); var body = Accepted(success);
        Require(body.IsAdministrator && body.RequiresInitialWorldConfiguration && body.AssignedColonyId == "away"
            && e.F.State.OfflineAccounts.Authenticate("receiver", "pw", DateTimeOffset.UtcNow).Accepted
            && e.F.State.CompatibilityBaseline.Current?.RimWorldVersion == "1.7", "Signup retry lost account, baseline, colony or administrator initialization.");
        e.F.AssertRecovered();
    }
    private static void Steam(Func<string, SaveSnapshotPackage> factory, string mode)
    {
        var validator = new SteamValidator(); using var e = new Env(factory, steam: validator);
        e.F.State.UpdateServerConfiguration(new(authenticationDebugMode: true));
        validator.Before = () => { Require(!Monitor.IsEntered(e.F.State.RaidSettlementSnapshotMutationGate), "Steam validation ran under the global mutation gate."); if (mode == "config") e.F.State.UpdateServerConfiguration(new()); };
        validator.Identity = mode == "identity" ? "other-user" : "receiver"; validator.Deny = mode == "denied";
        if (mode == "publication-ban") e.Publication = () => e.F.State.AdminControl.Ban("receiver");
        string before = e.F.Evidence(); HttpResponseMessage? response = null;
        if (mode == "recovery-config") SnapshotPreparationGateProbe.Run(e.F.State.RaidSettlementSnapshotMutationGate, hook => e.F.Store.BeforePrepare = hook,
            () => response = e.Call(), () => e.F.State.UpdateServerConfiguration(new()));
        else response = e.Call();
        using (response) { if (mode == "valid") Accepted(response!); else Denied(response!); }
        Require(validator.Calls == 1, "Steam ticket was revalidated during recovery.");
        if (mode is "valid" or "publication-ban") e.F.AssertRecovered(); else e.F.AssertPending(before, "steam/" + mode);
        Require(!e.F.State.WorldConfiguration.IsAdministrator("other-user") && e.F.State.Players.FindByUserId("other-user") is null, "Steam identity mismatch initialized another player's state.");
    }
    private static void ExistingColony(Func<string, SaveSnapshotPackage> factory, string mode)
    {
        using var e = new Env(factory); HttpResponseMessage? response = null;
        if (mode is "snapshot" or "changed-snapshot") e.Snapshot("old");
        if (mode == "deleted-colony") e.F.State.Players.MarkDeleted("receiver", "away", null, DateTimeOffset.UtcNow);
        if (mode == "changed-snapshot") SnapshotPreparationGateProbe.Run(e.F.State.RaidSettlementSnapshotMutationGate, hook => e.F.Store.BeforePrepare = hook,
            () => response = e.Call(), () => e.Snapshot("new"));
        else response = e.Call(mode);
        using (response)
        {
            var body = Accepted(response!);
            Require(body.LatestSnapshotId == (mode == "snapshot" ? "old" : mode == "changed-snapshot" ? "new" : null), "Session returned a stale snapshot.");
            Require(!string.IsNullOrWhiteSpace(body.AssignedColonyId) && (mode != "deleted-colony" || body.AssignedColonyId != "away")
                && e.F.State.Players.FindByUserId("receiver")?.ColonyId == body.AssignedColonyId, "Session lost normalized or replacement colony identity.");
        }
        e.F.AssertRecovered();
    }
    private static void ConcurrentSignup(Func<string, SaveSnapshotPackage> factory)
    {
        using var e = new Env(factory, fresh: true);
        var tasks = new[] { ("receiver", "1.7"), ("other", "different") }.Select(item => Task.Run(() =>
        {
            using var r = e.F.Post(ProtocolMessageKind.PrepareWorldSession, new PrepareWorldSessionRequest(ProtocolApiVersion.Current, item.Item1, "away", JsonSerializer.Serialize(Manifest(item.Item1, item.Item2)), password: "pw", createAccountIfMissing: true));
            return (user: item.Item1, body: Body(r));
        })).ToArray();
        Task.WaitAll(tasks); var winner = tasks.Single(t => t.Result.body.Result.Accepted).Result;
        Require(e.F.State.WorldConfiguration.ListAdministrators().SequenceEqual(new[] { winner.user })
            && e.F.State.CompatibilityBaseline.Current?.ManifestId == winner.user, "Competing first worlds installed different administrators and baselines.");
        var loser = tasks.Single(t => !t.Result.body.Result.Accepted).Result;
        Require(!e.F.State.OfflineAccounts.Authenticate(loser.user, "pw", DateTimeOffset.UtcNow).Accepted, "Incompatible competing signup created an account."); e.F.AssertRecovered();
    }
    private static void CredentialProof(string mode)
    {
        var slot = new Slot(); var accounts = new OfflineAccountRegistry(slot); var now = DateTimeOffset.UtcNow;
        bool existing = mode is "reset" or "same-password" or "lockout";
        if (existing) accounts.Authenticate("u", "pw", now, true);
        var proof = accounts.PrepareAuthentication(" u ", "pw", now, true); string? before = slot.Read();
        Require(proof.Result.Accepted && proof.Validate(now).Accepted && before == slot.Read(), "Credential preparation wrote an account.");
        if (mode is "reset" or "same-password") accounts.ResetPassword("u", mode == "reset" ? "changed" : "pw", now, out _);
        if (mode == "compete") accounts.Authenticate("u", "different", now, true);
        if (mode == "lockout") for (int i = 0; i < 5; i++) accounts.Authenticate("u", "wrong", now);
        if (mode is "reset" or "same-password" or "compete" or "lockout") Require(!proof.Validate(now, true).Accepted, "Stale credential proof was committed.");
        else if (mode == "uncommitted") Require(!accounts.Authenticate("u", "pw", now).Accepted, "Uncommitted signup became an account.");
        else
        {
            if (mode == "failure") { slot.Fail = true; bool failed = false; try { proof.Validate(now, true); } catch (IOException) { failed = true; } Require(failed && slot.Read() == before && proof.Validate(now).Accepted, "Failed account commit poisoned proof or cache."); slot.Fail = false; }
            Require(proof.Validate(now, true).Accepted, "Valid credential proof could not commit."); int writes = slot.Writes;
            Require(proof.Validate(now, true).Accepted && writes == slot.Writes && new OfflineAccountRegistry(slot).Authenticate("u", "pw", now).Accepted, "Repeated credential commit duplicated writes or lost password.");
        }
    }
    private static void InvalidShape(Func<string, SaveSnapshotPackage> factory, bool fresh, string shape)
    {
        using var e = new Env(factory, fresh); var json = JsonSerializer.SerializeToNode(Manifest())!.AsObject();
        switch (shape)
        {
            case "schema": json["SchemaVersion"] = 0; break;
            case "id": json["ManifestId"] = ""; break;
            case "protocol": json["ProtocolVersion"] = "other"; break;
            case "version": json["RimWorldVersion"] = null; break;
            case "mods-null": case "summary": case "summary-id-cache": json["Mods"] = null; break;
            case "mods-item": json["Mods"] = JsonNode.Parse("[null]"); break;
            case "mods-duplicate": json["Mods"] = JsonNode.Parse("[{\"PackageId\":\"mod\"},{\"PackageId\":\"MOD\"}]"); break;
            case "dlc-null": json["DlcIds"] = null; break;
            case "dlc-duplicate": json["DlcIds"] = JsonNode.Parse("[\"core\",\"CORE\"]"); break;
            case "defs-null": json["DefSummaries"] = null; break;
            case "defs-duplicate": json["DefSummaries"] = JsonNode.Parse("[{\"Name\":\"defs\"},{\"Name\":\"Defs\"}]"); break;
            case "files-null": json["Mods"] = JsonNode.Parse("[{\"PackageId\":\"mod\",\"Files\":null}]"); break;
            case "files-duplicate": json["Mods"] = JsonNode.Parse("[{\"PackageId\":\"mod\",\"Files\":[{\"RelativePath\":\"Defs/a.xml\"},{\"RelativePath\":\"/defs/a.xml\"}]}]"); break;
            case "configs-null": json["Mods"] = JsonNode.Parse("[{\"PackageId\":\"mod\",\"Configs\":null}]"); break;
            case "configs-duplicate": json["Mods"] = JsonNode.Parse("[{\"PackageId\":\"mod\",\"Configs\":[{\"FileName\":\"a.xml\"},{\"FileName\":\"A.xml\"}]}]"); break;
            case "rules-null": json["ModConfigRules"] = null; break;
        }
        string before = e.F.Evidence(), domain = e.Domain(); bool summary = shape.StartsWith("summary", StringComparison.Ordinal);
        using var response = e.F.Post(ProtocolMessageKind.PrepareWorldSession, new PrepareWorldSessionRequest(ProtocolApiVersion.Current, "receiver", "away", summary ? null : json.ToJsonString(), password: "pw",
            compatibilityManifestId: shape == "summary-id-cache" ? "base" : null, compatibilityManifestSummaryJson: summary ? json.ToJsonString() : null, createAccountIfMissing: fresh));
        if (shape == "summary-id-cache") { Accepted(response); e.F.AssertRecovered(); }
        else { Denied(response); e.F.AssertPending(before, "shape/" + shape); Require(e.Domain() == domain, "Malformed manifest wrote an account, baseline or session."); }
    }
    private static void Defense(Func<string, SaveSnapshotPackage> factory, string mode)
    {
        using var e = new Env(factory); e.Snapshot("defender");
        if (mode == "administrator") e.F.State.WorldConfiguration.PromoteAdministrator("receiver");
        var now = DateTimeOffset.UtcNow;
        var raid = AuthoritativeEventFactory.Create(ServerEventType.Raid, new("attacker", "home"), new("receiver", "away"), "raid", false,
            new RaidEventPayload("defender", null, now, null, null, new RaidAttackForceRecord("departed", ["pawn"], [])), now);
        if (mode != "arrives") e.F.State.Ledger.Append(raid);
        HttpResponseMessage? response = null;
        SnapshotPreparationGateProbe.Run(e.F.State.RaidSettlementSnapshotMutationGate, hook => e.F.Store.BeforePrepare = hook, () => response = e.Call(), () =>
        { if (mode == "arrives") e.F.State.Ledger.Append(raid); if (mode == "closes") e.F.State.Ledger.ChangeStatus(raid.EventId, ServerEventStatus.Cancelled); });
        using (response)
        {
            var body = Body(response!); Require(body.Result.Accepted == (mode == "closes") && body.HasExistingColony && body.LatestSnapshotId == "defender", "World preparation ignored the current defender login restriction.");
            if (mode != "closes") Require(e.F.State.Players.FindByUserId("receiver") is null, "Blocked defender ran the successful lifecycle hook.");
        }
        e.F.AssertRecovered();
    }
    private static void Change(Env e, string change) => ChangeEntryState(e.F.State, change);
    private static PrepareWorldSessionResponse Body(HttpResponseMessage response)
    { response.EnsureSuccessStatusCode(); return JsonSerializer.Deserialize<PrepareWorldSessionResponse>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!; }
    private static PrepareWorldSessionResponse Accepted(HttpResponseMessage response) { var body = Body(response); Require(body.Result.Accepted, "Valid world preparation rejected: " + body.Result.Message); return body; }
    private static void Denied(HttpResponseMessage response)
    { var b = Body(response); Require(!b.Result.Accepted && !b.IsAdministrator && b.AssignedColonyId is null && b.LatestSnapshotId is null && b.WorldConfiguration is null, "Rejected preparation accepted or disclosed session state."); }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Env : IDisposable
    {
        public readonly HttpSnapshotRecoveryTests.Fixture F; public readonly Slot Account = new(), Baseline = new(); public Action? Publication; private readonly bool fresh;
        public Env(Func<string, SaveSnapshotPackage> factory, bool fresh = false, SteamValidator? steam = null)
        {
            this.fresh = fresh;
            var plugins = PublicationPlugins("test.world-prepare", () => Publication?.Invoke());
            F = new(factory, plugins, accounts: new OfflineAccountRegistry(Account), compatibility: new CompatibilityBaselineRegistry(Baseline), steam: steam); F.Start();
            if (!fresh) { F.State.OfflineAccounts.Authenticate("receiver", "pw", DateTimeOffset.UtcNow, true); F.State.CompatibilityBaseline.EnsureBaseline(Manifest(), "admin", DateTimeOffset.UtcNow); F.State.WorldConfiguration.Prepare("admin"); }
            F.Seed();
        }
        public void Snapshot(string id) { var envelope = F.Package.Envelope with { Identity = new("receiver", "away", id) }; F.Store.StoreLatest(new(envelope.Identity, envelope, F.Package.Index, DateTimeOffset.UtcNow)); }
        public string Domain() => JsonSerializer.Serialize(new { account = Account.Read(), baseline = Baseline.Read(), session = F.State.WorldConfiguration.GetSessionState("receiver"), player = F.State.Players.FindByUserId("receiver"), deleted = F.State.Players.ListDeleted().Where(x => x.UserId == "receiver") });
        public void Restore() => RestoreEntryState(F);
        public HttpResponseMessage Call(string mode = "")
        {
            if (mode is "malformed" or "null" or "empty") return PostInvalidBody(F.Client, ProtocolMessageKind.PrepareWorldSession, mode);
            return F.Post(ProtocolMessageKind.PrepareWorldSession, new PrepareWorldSessionRequest(mode == "protocol" ? "wrong" : ProtocolApiVersion.Current,
                mode == "user" ? "" : mode == "account" ? "unknown" : mode == "trimmed" ? " receiver " : "receiver", mode == "default" ? null : "away",
                mode == "manifest" ? "{" : mode == "manifest-null" ? "null" : mode is "manifest-missing" or "id" or "summary" ? null : JsonSerializer.Serialize(Manifest()),
                password: mode == "password" ? "wrong" : "pw", compatibilityManifestId: mode == "id" ? "base" : null,
                compatibilityManifestSummaryJson: mode == "summary" ? JsonSerializer.Serialize(new CompatibilityManifestSummary { ManifestId = "base", ProtocolVersion = ProtocolApiVersion.Current, RimWorldVersion = "1.7" }) : null,
                createAccountIfMissing: fresh));
        }
        public void Dispose() => F.Dispose();
    }
}
