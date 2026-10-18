using System.Text.Json;
using System.Globalization;
using System.Reflection;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Admin;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

internal static class AdministratorActionModuleTests
{
    private static readonly string[] Actions = ["Broadcast", "Kick", "Ban", "Unban", "PromoteAdmin", "RevokeAdmin", "DeletePlayerSave", "ResetOfflinePassword", "LockMaintenance", "UnlockMaintenance"];
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization(); var culture = CultureInfo.CurrentCulture; int count = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string action in Actions) { Success(action); count++; Denied(action); count++; }
                foreach (string action in new[] { "Kick", "Ban", "RevokeAdmin", "DeletePlayerSave" }) { SelfTarget(action); count++; }
                foreach (string action in new[] { "", "  ", "kick", "0", "999", "Broadcast, Kick" }) { InvalidAction(action); count++; }
                foreach (string action in new[] { "Kick", "Ban", "Unban", "PromoteAdmin", "RevokeAdmin", "DeletePlayerSave", "ResetOfflinePassword" })
                { MissingTarget(action); count++; }
                foreach (bool replay in new[] { false, true }) foreach (string change in new[] { "none", "role", "replacement" })
                { Recovery(change, replay); count++; }
                foreach (bool persistent in new[] { false, true }) foreach (string severity in new[] { "Warning", "Critical", "Error", "unknown" })
                { Broadcast(persistent, severity); count++; }
                foreach (string mode in new[] { "Ban", "Unban", "LockMaintenance", "Kick", "ResetOfflinePassword" }) { Verify(mode); count++; }
                RebuiltDeletion(factory("administrator-delete")); count++;
                BroadcastFailure(); count++;
                RolesAndFailures(); count++;
                LegacyAudit(); count++;
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
        foreach (string store in new[] { "file", "sqlite" }) foreach (string action in new[] { "Ban", "Unban", "LockMaintenance", "UnlockMaintenance" })
        { PolicyPersistence(store, action); count++; }
        HttpDenied(factory); count++;
        HttpRecovery(factory); count++;
        Console.WriteLine($"PASS: administrator action module ({count} scenarios)");
    }
    private static void Verify(string mode)
    {
        var slot = new RecoveryTestSupport.Slot(); var control = new AdminControlRegistry(slot);
        var state = new ClashOfRimNetworkState(adminControl: control);
        state.WorldConfiguration.Prepare("admin");
        state.LoginSessions.TryCreate("admin", "colony", DateTimeOffset.UtcNow, out var adminSession);
        var token = state.AuthTokens.Issue("admin", "admin", "colony", adminSession, DateTimeOffset.UtcNow);
        state.LoginSessions.TryCreate("target", "target-colony", DateTimeOffset.UtcNow, out var targetSession);
        state.AuthTokens.Issue("target", "target", "target-colony", targetSession, DateTimeOffset.UtcNow);
        state.OnlinePresence.TryConnectExclusive("target", out var presence);
        using (presence)
        {
            var request = new AdminActionRequest("admin", "colony", token, mode, "target", "target-colony", "test-new-password");
            if (mode == "ResetOfflinePassword")
            {
                state.OfflineAccounts.Authenticate("target", "old", DateTimeOffset.UtcNow, true);
                var result = Call(state, request);
                Require(result.Result.Accepted && !JsonSerializer.Serialize(result.AuditRecord).Contains("test-new-password", StringComparison.Ordinal)
                    && !slot.Read()!.Contains("test-new-password", StringComparison.Ordinal), "Password reset persisted the new password in its audit record.");
                return;
            }
            if (mode == "Kick")
            {
                var result = Call(state, request);
                Require(result.AffectedOnlineUsers == 1, "One kicked player was counted twice."); return;
            }
            if (mode == "Unban") control.Ban("target");
            slot.Fail = true;
            try { Call(state, request); throw new Exception("Persistence failure missing."); } catch (IOException) { }
            Require(control.IsBanned("target") == (mode == "Unban") && !control.MaintenanceLoginLocked
                && state.LoginSessions.IsValid("target", "target-colony", targetSession, DateTimeOffset.UtcNow),
                "Failed policy save changed live policy or disconnected its target.");
        }
    }
    private static void HttpDenied(Func<string, SaveSnapshotPackage> factory)
    {
        using var f = new HttpSnapshotRecoveryTests.Fixture(factory); f.Start(); f.Seed(); string before = f.Evidence();
        using var response = f.Post(ProtocolMessageKind.AdminAction, new AdminActionRequest("receiver", "away", "bad", "Kick", "foreign", "foreign-colony", null));
        response.EnsureSuccessStatusCode(); f.AssertPending(before, "unauthorized administrator action");
    }
    private static AdminActionResponse Call(ClashOfRimNetworkState state, AdminActionRequest request)
        => state.AdministratorActions.Execute(request);

    private static void Success(string action)
    {
        using var f = new Env();
        if (action == "Unban") f.Control.Ban("target");
        if (action == "RevokeAdmin") f.World.PromoteAdministrator("target");
        if (action == "UnlockMaintenance") f.Control.SetMaintenanceLoginLocked(true, "before");
        if (action == "ResetOfflinePassword") f.Accounts.Authenticate("target", "old", DateTimeOffset.UtcNow, true);
        var result = f.Module.Execute(f.Request(" " + action + " ", " target "));
        Require(result.Result.Accepted && result.ActionKind == action && result.AuditRecord is not null
            && f.Control.ListAudit().Count == 1 && f.Control.ListAudit()[0].TargetUserId == "target", "Valid administrator action failed: " + action);
        Require(f.S.Recovery.Replays == 1, "Action ran duplicate recovery: " + action);
        if (action is "Kick" or "Ban" or "DeletePlayerSave")
            Require(result.AffectedOnlineUsers == 1 && !f.TargetActive && !f.S.Tokens.IsValid(f.TargetToken, "target", "target-colony", DateTimeOffset.UtcNow), "Forced account retirement did not revoke current access.");
        if (action == "Ban") Require(f.Control.IsBanned("target"), "Ban was not installed.");
        if (action == "Unban") Require(!f.Control.IsBanned("target"), "Unban was not installed.");
        if (action == "PromoteAdmin") Require(f.World.IsAdministrator("target") && f.WorldNotifications.GetVersion("target") > 0, "Promotion was not visible.");
        if (action == "RevokeAdmin") Require(!f.World.IsAdministrator("target") && f.WorldNotifications.GetVersion("target") > 0, "Revocation was not visible.");
        if (action == "DeletePlayerSave") Require(f.S.Players.IsDeleted("target", "target-colony") && f.World.Current!.PlayerColonySites.Count == 2, "Deletion did not use the colony cleanup flow.");
        if (action == "ResetOfflinePassword") Require(f.Accounts.Authenticate("target", "message", DateTimeOffset.UtcNow).Accepted
            && result.AuditRecord!.Message is null, "Password reset failed or disclosed its password.");
        if (action is "LockMaintenance" or "UnlockMaintenance") Require(f.Control.MaintenanceLoginLocked == (action == "LockMaintenance")
            && f.Control.MaintenanceReason == (action == "LockMaintenance" ? "message" : null), "Maintenance policy changed semantics.");
        if (action == "Broadcast") Require(f.S.Ledger.ListByType(ServerEventType.ServerNotification).Count == 1, "Targeted broadcast failed.");
    }

    private static void Denied(string action)
    {
        using var f = new Env(); string before = f.Evidence();
        f.S.Recovery.OnPrerequisites = () => throw new Exception("Denied administrator action started recovery.");
        var result = f.Module.Execute(new("user", "colony", "bad", action, "target", "target-colony", "message"));
        Require(!result.Result.Accepted && result.AuditRecord is null && result.AffectedOnlineUsers == 0 && before == f.Evidence(), "Denied action changed state: " + action);
    }

    private static void SelfTarget(string action)
    {
        using var f = new Env(); f.S.Recovery.OnPrerequisites = () => throw new Exception("Self-target request recovered.");
        Require(!f.Module.Execute(f.Request(action, " user ")).Result.Accepted && f.Control.ListAudit().Count == 0, "Self-target action accepted: " + action);
    }

    private static void InvalidAction(string action)
    {
        using var f = new Env(); f.S.Recovery.OnPrerequisites = () => throw new Exception("Invalid action recovered.");
        Require(!f.Module.Execute(f.Request(action)).Result.Accepted && f.Control.ListAudit().Count == 0, "Unknown action accepted: " + action);
    }

    private static void MissingTarget(string action)
    {
        using var f = new Env(); f.S.Recovery.OnPrerequisites = () => throw new Exception("Missing target recovered.");
        Require(!f.Module.Execute(f.Request(action, " ")).Result.Accepted, "Missing target accepted: " + action);
    }

    private static void Recovery(string change, bool replay)
    {
        using var f = new Env(); f.S.Recovery.NeedsPreparation = true;
        void Change()
        {
            if (change == "role") f.World.RevokeAdministrator("user");
            if (change == "replacement") { f.S.Sessions.EndUser("user"); f.S.Issue(); }
        }
        f.S.Recovery.OnPrepare = () => { Require(!Monitor.IsEntered(f.S.Gate), "Admin action recovery prepared under the global gate."); if (!replay) Change(); };
        f.S.Recovery.OnReplay = () => { Require(Monitor.IsEntered(f.S.Gate), "Admin action replay escaped the gate."); if (replay) Change(); };
        var result = f.Module.Execute(f.Request("Ban"));
        Require(result.Result.Accepted == (change == "none") && f.Control.IsBanned("target") == (change == "none")
            && f.TargetActive == (change != "none"), "Recovery did not revalidate administrator access: " + change);
    }

    private static void Broadcast(bool persistent, string severity)
    {
        using var f = new Env(); f.S.Players.Record("offline", "elsewhere", null, DateTimeOffset.UtcNow);
        var result = f.Module.Execute(new("user", "colony", f.Token, "Broadcast", null, null, "  本地化通知  ", severity, persistent));
        var events = f.S.Ledger.ListByType(ServerEventType.ServerNotification);
        Require(result.AffectedOnlineUsers == (persistent ? 2 : 1) && events.Count == (persistent ? 2 : 1), "Broadcast recipient selection changed.");
        var expected = severity == "Warning" ? ServerNotificationSeverity.Warning : severity is "Error" or "Critical" ? ServerNotificationSeverity.Critical : ServerNotificationSeverity.Info;
        foreach (var item in events)
        {
            var payload = (ServerNotificationEventPayload)item.Payload;
            Require(payload.Message == "本地化通知" && payload.Severity == expected && payload.FromAdministrator
                && payload.AdministratorUserId == "user" && payload.OnlineOnly == !persistent && f.S.Events.GetVersion(item.Target.UserId) > 0,
                "Broadcast payload or wakeup lost administrator semantics.");
        }
        var none = f.Module.Execute(f.Request("Broadcast", "missing"));
        Require(none.Result.Accepted && none.AffectedOnlineUsers == 0 && f.S.Ledger.ListByType(ServerEventType.ServerNotification).Count == events.Count, "Unknown broadcast target added notifications.");
    }

    private static void BroadcastFailure()
    {
        using var f = new Env(); f.S.Players.Record("offline", "elsewhere", null, DateTimeOffset.UtcNow);
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, SnapshotSupportDeathTests.InterruptedLedger>();
        var fault = (SnapshotSupportDeathTests.InterruptedLedger)(object)proxy; fault.Inner = f.S.Ledger;
        int batches = 0; fault.BeforeAppend = () => throw new Exception("Broadcast still appends recipients individually.");
        fault.BeforeBatch = () => { batches++; throw new IOException("Broadcast batch interrupted."); };
        var broadcasts = new AdministratorBroadcasts(proxy, f.S.Players, f.S.Presence, f.S.Events);
        ExpectFailure(() => broadcasts.Publish("user", null, null, null, true, DateTimeOffset.UnixEpoch));
        Require(f.S.Ledger.ListAll().Count == 0 && f.S.Events.GetVersion("target") == 0, "Failed broadcast partially published or woke a player.");
        fault.BeforeBatch = () => batches++;
        Require(broadcasts.Publish("user", null, null, null, true, DateTimeOffset.UnixEpoch) == 2 && batches == 2
            && f.S.Ledger.ListAll().Count == 2, "Broadcast retry was not a single batch.");
        broadcasts.Publish("user", null, null, null, true, DateTimeOffset.UnixEpoch);
        Require(f.S.Ledger.ListAll().Count == 2, "The same broadcast publication duplicated notifications.");
    }

    private static void RebuiltDeletion(SaveSnapshotPackage package)
    {
        using var f = new Env();
        var identity = new SnapshotIdentity("target", "target-colony", "old");
        f.S.Snapshots.StoreLatest(new(identity, package.Envelope with { Identity = identity }, package.Index, DateTimeOffset.UtcNow));
        f.S.Sessions.EndUser("target"); f.S.Presence.ForceDisconnect("target");
        f.S.Sessions.TryCreate("target", "rebuilt", DateTimeOffset.UtcNow, out var next);
        var newToken = f.S.Tokens.Issue("target", "target", "rebuilt", next, DateTimeOffset.UtcNow);
        f.S.Presence.TryConnectExclusive("target", next, out var replacement); using var lease = replacement!;
        f.S.Players.Record("target", "rebuilt", null, DateTimeOffset.UtcNow);
        var result = f.Module.Execute(f.Request("DeletePlayerSave"));
        Require(result.Result.Accepted && result.AffectedOnlineUsers == 1 && !lease.IsActive()
            && !f.S.Tokens.IsValid(newToken, "target", "rebuilt", DateTimeOffset.UtcNow)
            && f.S.Players.IsDeleted("target", "target-colony") && !f.S.Players.IsDeleted("target", "rebuilt"),
            "Explicit administrator deletion did not retire the account or deleted the replacement colony.");
        f.S.Sessions.TryCreate("target", "rebuilt", DateTimeOffset.UtcNow, out var again);
        string againToken = f.S.Tokens.Issue("target", "target", "rebuilt", again, DateTimeOffset.UtcNow);
        f.Deletion.Recover();
        Require(f.S.Tokens.IsValid(againToken, "target", "rebuilt", DateTimeOffset.UtcNow), "Completed deletion recovery retired a later login.");
    }

    private static void RolesAndFailures()
    {
        using var f = new Env();
        Require(!f.Module.Execute(f.Request("RevokeAdmin", "missing")).Result.Accepted, "Unknown administrator revocation was accepted.");
        f.WorldSlot.Fail = true; ExpectFailure(() => f.Module.Execute(f.Request("PromoteAdmin")));
        Require(!f.World.IsAdministrator("target") && f.Control.ListAudit().Count == 0 && f.WorldNotifications.GetVersion("target") == 0,
            "Failed role save changed role, audit or notifications.");
        f.WorldSlot.Fail = false;
        Require(f.Module.Execute(f.Request("PromoteAdmin", "user")).Result.Accepted, "Idempotent self promotion changed compatibility.");
        Require(f.Module.Execute(f.Request("ResetOfflinePassword", "missing")).Result.Accepted
            && f.Accounts.Authenticate("missing", "message", DateTimeOffset.UtcNow).Accepted, "Administrative password reset lost account provisioning support.");
        Require(!f.Module.Execute(new("user", "colony", f.Token, "DeletePlayerSave", "missing", null, null)).Result.Accepted,
            "Deletion with no colony was accepted.");
    }

    private static void LegacyAudit()
    {
        var slot = new RecoveryTestSupport.Slot();
        var old = new AdminAuditRecord("ResetOfflinePassword", "admin", "target", "old-plaintext", DateTimeOffset.UnixEpoch);
        slot.Write(JsonSerializer.Serialize(new { MaintenanceLoginLocked = false, BannedUsers = Array.Empty<string>(), AuditRecords = new[] { old } }));
        var registry = new AdminControlRegistry(slot);
        Require(registry.ListAudit().Single().Message is null && old.ToDto().Message is null, "Historical password audits were returned to clients.");
        registry.AddAudit("ResetOfflinePassword", "admin", "target", "new-plaintext", DateTimeOffset.UtcNow);
        Require(!slot.Read()!.Contains("plaintext", StringComparison.Ordinal), "Audit persistence bypassed shared credential redaction.");
    }

    private static void PolicyPersistence(string store, string action)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cor-admin-policy-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            string db = Path.Combine(root, "server.sqlite"); ServerDatabaseMigrator.Migrate(db);
            var slot = new RecoveryTestSupport.Slot(new FileJsonPersistenceSlot(Path.Combine(root, "control.json")));
            AdminControlRegistry Open() => store == "sqlite" ? new(new SqliteKeyedJsonRecordStore(db, "admin-control"), null) : new(slot);
            var control = Open();
            if (action == "Unban") control.Ban("target");
            if (action == "UnlockMaintenance") control.SetMaintenanceLoginLocked(true, "before");
            control.AddAudit("seed", "user", null, null, DateTimeOffset.UnixEpoch);
            string before = Policy(control);
            void Sql(string sql) { using var c = new SqliteConnection("Data Source=" + db); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }
            if (store == "sqlite") Sql("create trigger fail_admin_policy before " + (action == "Unban" ? "delete" : "insert") + " on server_keyed_json_records begin select raise(abort, 'policy failure'); end;");
            else slot.Fail = true;
            void Change() { if (action == "Ban") control.Ban("target"); else if (action == "Unban") control.Unban("target"); else control.SetMaintenanceLoginLocked(action == "LockMaintenance", action == "LockMaintenance" ? "new" : null); }
            bool failed = false; try { Change(); } catch (Exception ex) when (ex is IOException or SqliteException) { failed = true; }
            Require(failed && Policy(control) == before && Policy(Open()) == before, "Failed policy write changed cache or storage: " + store + "/" + action);
            if (store == "sqlite") Sql("drop trigger fail_admin_policy;"); else slot.Fail = false;
            Change(); Require(Policy(control) == Policy(Open()) && Policy(control) != before, "Policy retry did not persist.");
            string after = Policy(control); Change(); Require(Policy(control) == after, "Repeated policy request changed its value.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Require(Path.GetDirectoryName(root)!.TrimEnd(Path.DirectorySeparatorChar) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                && Path.GetFileName(root).StartsWith("cor-admin-policy-", StringComparison.Ordinal), "Unexpected policy test cleanup path.");
            Directory.Delete(root, true);
        }
    }

    private static void HttpRecovery(Func<string, SaveSnapshotPackage> factory)
    {
        using var f = new HttpSnapshotRecoveryTests.Fixture(factory); f.Start(); f.State.WorldConfiguration.Prepare("receiver"); f.Seed();
        f.Store.BeforePrepare = () => Require(!Monitor.IsEntered(f.State.RaidSettlementSnapshotMutationGate), "HTTP admin action prepared recovery inside the gate.");
        using var response = f.Post(ProtocolMessageKind.AdminAction, new AdminActionRequest("receiver", "away", f.Token, "LockMaintenance", null, null, "maintenance"));
        response.EnsureSuccessStatusCode();
        Require(f.State.AdminControl.MaintenanceLoginLocked && f.State.AdminControl.ListAudit().Count == 1, "Authenticated HTTP action failed.");
        f.AssertRecovered();
    }

    private sealed class Env : IDisposable
    {
        internal readonly SessionModuleTestContext S = new();
        internal readonly RecoveryTestSupport.Slot ControlSlot = new(), WorldSlot = new();
        internal readonly AdminControlRegistry Control;
        internal readonly WorldConfigurationRegistry World;
        internal readonly EventNotificationHub WorldNotifications = new();
        internal readonly OfflineAccountRegistry Accounts = new();
        internal readonly ColonyDeletion Deletion;
        internal readonly AdministratorActions Module;
        internal readonly string Token, TargetSession, TargetToken;
        private readonly OnlinePresenceLease targetLease;
        internal Env()
        {
            Control = new(ControlSlot); World = new(WorldSlot, new WorldConfigurationPersistenceTests.MemoryBinary());
            ColonyDeletionStageTests.SeedWorld(World, "target", "target-colony"); World.PromoteAdministrator("user"); Token = S.Issue().Token;
            S.Players.Record("target", "target-colony", null, DateTimeOffset.UtcNow);
            S.Sessions.TryCreate("target", "target-colony", DateTimeOffset.UtcNow, out TargetSession);
            TargetToken = S.Tokens.Issue("target", "target", "target-colony", TargetSession, DateTimeOffset.UtcNow);
            S.Presence.TryConnectExclusive("target", TargetSession, out var lease); targetLease = lease!;
            var changes = new WorldConfigurationChanges(S.Players, WorldNotifications);
            Deletion = new(S.Snapshots, S.Ledger, new(), new(), S.Mutations, World, new ServerPluginRegistry(), new(), new(), new(), S.Loans,
                S.Players, new(), new(), new(), S.Connections, S.Events, changes);
            S.Recovery.OnPrerequisites = Deletion.Recover;
            Module = new(new AdministratorAuthorization(S.Tokens, S.Sessions, World), S.Mutations, Control,
                new AdministratorPlayers(Control, S.Connections, S.Events, S.Players, S.Snapshots, Deletion, Accounts),
                new AdministratorBroadcasts(S.Ledger, S.Players, S.Presence, S.Events), World, changes, () => NullLogger.Instance);
        }
        internal bool TargetActive => S.Sessions.IsValid("target", "target-colony", TargetSession, DateTimeOffset.UtcNow) && targetLease.IsActive();
        internal AdminActionRequest Request(string action, string? target = "target") => new("user", "colony", Token, action, target, "target-colony", "message");
        internal string Evidence() => JsonSerializer.Serialize(new { Policy = Policy(Control), World = WorldSlot.Read(), Players = S.PlayerSlot.Read(), Events = S.Ledger.ListAll(), Target = TargetActive });
        public void Dispose() => targetLease.Dispose();
    }
    private static string Policy(AdminControlRegistry control) => JsonSerializer.Serialize(new { control.MaintenanceLoginLocked, control.MaintenanceReason, Bans = control.ListBannedUsers(), Audit = control.ListAudit() });
    private static void ExpectFailure(Action run) { try { run(); } catch (IOException) { return; } throw new Exception("Expected persistence failure missing."); }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
