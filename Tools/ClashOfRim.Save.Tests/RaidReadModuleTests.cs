using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using System.Globalization;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;

internal static class RaidReadModuleTests
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(10);
    internal static AuthoritativeEvent Raid(string id, string attacker = "attacker", string defender = "defender") => new(
        id, ServerEventType.Raid, new(attacker, "attack-colony"), new(defender, "defend-colony"), Now,
        ServerEventStatus.Recorded, ServerEventDeliveryMode.OnlineImmediate, "key:" + id,
        new RaidEventPayload("defense-base", null, Now, null, null, new("attack-base", ["pawn"], [])));
    internal static AuthoritativeEvent Loss(string id)
    {
        var raid = Raid(id); var payload = (RaidEventPayload)raid.Payload;
        return raid with { Actor = new("server"), Target = raid.Actor,
            Payload = payload with { FinishedAtUtc = Now.AddMinutes(10), AttackerLoss = RaidAttackerLossRecord.FromAttackForce("source", payload.AttackForce!, "timeout") } };
    }
    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            foreach (string storage in new[] { "memory", "file", "sqlite" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                WithLedger(storage, (ledger, reopen) => Queries(ledger, reopen));
                WithLedger(storage, (ledger, _) => Protection(ledger));
            }
            foreach (ServerEventStatus status in Enum.GetValues<ServerEventStatus>()) Status(status);
            Configuration(); Overrides(); InvalidOverride();
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static void Queries(IAuthoritativeEventLedger ledger, Func<IAuthoritativeEventLedger> reopen)
    {
        ledger.Append(Loss("loss") with { CreatedAtUtc = Now.AddMinutes(-1) });
        ledger.Append(Raid("active"));
        ledger.Append(Raid("other-colony") with { Actor = new("attacker", "rebuilt"), CreatedAtUtc = Now.AddDays(-1) });
        ledger.Append(Raid("npc") with { Payload = ((RaidEventPayload)Raid("npc").Payload) with { OpponentKind = RaidOpponentKind.VanillaNpc } });
        var queries = new RaidQueries(ledger, () => new());
        var activations = new RaidProtectionActivationRegistry();
        var module = new RaidProtection(ledger, activations, new(), () => new(), NullLogger.Instance);
        Check(queries.UnsettledAttacker("attacker", "attack-colony")?.EventId == "loss", "Pending attacker loss was omitted from new-raid admission.");
        Check(queries.UnsettledAttacker("attacker", "attack-colony", "key:loss")?.EventId == "active", "Same-key exclusion ignored the next active raid.");
        Check(queries.ActiveAttacker("attacker", "attack-colony")?.EventId == "active", "Loss receipt replaced active battle recovery.");
        Check(queries.BlockingDefender("defender", "defend-colony")?.EventId == "other-colony", "Defender did not choose earliest source raid.");
        Check(queries.BlockingDefender("attacker", "attack-colony") is null, "Incoming loss blocked attacker login as a defender.");
        Check(module.CurrentUntil("attacker", "attack-colony", Now) is null, "Loss receipt granted defender protection.");
        module.OnLifecycle(ClientLifecycleEvent.LoggedIn("attacker", "attack-colony", "session", "snapshot", Now));
        Check(activations.FindActivatedAt("loss", "attacker", "attack-colony") is null, "Loss receipt started a protection timer.");
        var recovery = queries.Recovery("attacker", "attack-colony", Now.AddMinutes(1));
        Check(recovery is not null && recovery.EventId == "active" && recovery.StartedAtUtc == Now && recovery.ServerNowUtc == Now.AddMinutes(1)
            && recovery.DefenderUserId == "defender" && recovery.DefenderColonyId == "defend-colony" && recovery.DefenderSnapshotId == "defense-base"
            && recovery.DeadlineUtc == Now + RaidDefenseLockPolicy.Default.MaxRaidDuration
            && recovery.FinalDeadlineUtc == Now + RaidDefenseLockPolicy.Default.ServerTimeoutDuration, "Recovery DTO lost its source, clock or deadlines.");
        Check(queries.UnsettledAttacker("", "attack-colony") is null && queries.BlockingDefender(null, "defend-colony") is null
            && queries.ActiveAttacker("attacker", "missing") is null && queries.Recovery("", "attack-colony", Now) is null,
            "Empty or unrelated identity leaked a raid.");
        ledger.MarkDelivered("loss", "base", Now); ledger.MarkApplied("loss", "consumed", Now); ledger.ChangeStatus("active", ServerEventStatus.Failed);
        queries = new RaidQueries(reopen(), () => new());
        Check(queries.UnsettledAttacker("attacker", "attack-colony") is null && queries.Recovery("attacker", "attack-colony", Now) is null,
            "Persisted consumed loss or failed source still blocks attacker.");
        Check(queries.BlockingDefender("defender", "defend-colony") is not null, "Failed source released defender before settlement.");
    }

    private static void Status(ServerEventStatus status)
    {
        var ledger = new InMemoryAuthoritativeEventLedger(); ledger.Append(Raid("source") with { Status = status });
        var queries = new RaidQueries(ledger, () => new());
        bool active = status is not (ServerEventStatus.AppliedToSnapshot or ServerEventStatus.Cancelled or ServerEventStatus.RejectedByTarget or ServerEventStatus.Failed);
        Check((queries.ActiveAttacker("attacker", "attack-colony") is not null) == active, "Source attacker classification changed: " + status);
        Check((queries.BlockingDefender("defender", "defend-colony") is not null) == (active || status == ServerEventStatus.Failed), "Defender source classification changed: " + status);
        ledger = new(); ledger.Append(Loss("loss") with { Status = status }); queries = new(ledger, () => new());
        Check((queries.UnsettledAttacker("attacker", "attack-colony") is not null) == active, "Loss receipt classification changed: " + status);
    }

    private static void Protection(IAuthoritativeEventLedger ledger)
    {
        foreach (var (id, status, ended, duration) in new[] {
            ("settled", ServerEventStatus.AppliedToSnapshot, true, RaidCooldownPolicy.Default.SettlementCooldown),
            ("failed", ServerEventStatus.Failed, false, RaidCooldownPolicy.Default.TimeoutCooldown),
            ("cancelled", ServerEventStatus.Cancelled, false, RaidCooldownPolicy.Default.CancelledCooldown) })
        {
            string user = "defender:" + id;
            var source = Raid(id, defender: user);
            source = source with { Status = status, Payload = ((RaidEventPayload)source.Payload) with { FinishedAtUtc = ended ? Now : null } };
            ledger.Append(source);
            var activations = new RaidProtectionActivationRegistry();
            var module = new RaidProtection(ledger, activations, new(), () => new(), NullLogger.Instance);
            Check(module.CurrentUntil(user, "defend-colony", Now) == DateTimeOffset.MaxValue, "Protection expired before first defender login: " + id);
            module.OnLifecycle(ClientLifecycleEvent.InitialWorldSessionPrepared(user, "defend-colony", "snapshot", Now));
            module.OnLifecycle(ClientLifecycleEvent.ColonyAbandoned(user, "defend-colony", "snapshot", Now));
            module.OnLifecycle(ClientLifecycleEvent.LoggedIn(user, "rebuilt", "session", "snapshot", Now));
            Check(activations.FindActivatedAt(id, user, "defend-colony") is null, "Unrelated lifecycle/colony activated protection.");
            DateTimeOffset login = Now.AddHours(2);
            module.OnLifecycle(ClientLifecycleEvent.LoggedIn(user, "defend-colony", "session", "snapshot", login));
            module.OnLifecycle(ClientLifecycleEvent.LoggedIn(user, "defend-colony", "second", "snapshot", login.AddHours(1)));
            Check(activations.FindActivatedAt(id, user, "defend-colony") == login && module.CurrentUntil(user, "defend-colony", login) == login + duration,
                "Login reset the protection timer or used the wrong policy: " + id);
            Check(module.CurrentUntil(user, "defend-colony", login + duration) is null && module.CurrentUntil(user, "", login) is null,
                "Expired or missing-colony protection remained active.");
        }
    }

    private static void Configuration()
    {
        var ledger = new InMemoryAuthoritativeEventLedger(); var source = Raid("settled");
        ledger.Append(source with { Payload = ((RaidEventPayload)source.Payload) with { FinishedAtUtc = Now } });
        var config = new ClashOfRimServerConfiguration(raidProtectionDuration: TimeSpan.Zero);
        var activations = new RaidProtectionActivationRegistry();
        var module = new RaidProtection(ledger, activations, new(), () => config, NullLogger.Instance);
        module.OnLifecycle(ClientLifecycleEvent.LoggedIn("defender", "defend-colony", "s", "base", Now));
        Check(activations.FindActivatedAt("settled", "defender", "defend-colony") is null && module.CurrentUntil("defender", "defend-colony", Now) is null,
            "Disabled protection still activated.");
        config = new(raidProtectionDuration: TimeSpan.FromHours(8));
        Check(module.CurrentUntil("defender", "defend-colony", Now) == DateTimeOffset.MaxValue, "Configuration update used stale policy.");
        module.OnLifecycle(ClientLifecycleEvent.LoggedIn("defender", "defend-colony", "s", "base", Now));
        config = new(raidProtectionDuration: TimeSpan.FromHours(2), raidMaxDuration: TimeSpan.FromMinutes(8), raidTimeoutGracePeriod: TimeSpan.FromMinutes(3));
        Check(module.CurrentUntil("defender", "defend-colony", Now) == Now.AddHours(2), "Protection did not use updated duration.");
        ledger.Append(Raid("active")); var queries = new RaidQueries(ledger, () => config);
        Check(queries.Recovery("attacker", "attack-colony", Now)?.FinalDeadlineUtc == Now.AddMinutes(11), "Recovery did not use current timeout policy.");
        // Old completion records without an attack-force field still protect the defender.
        var legacy = Raid("legacy", defender: "legacy-defender");
        ledger.Append(legacy with { Payload = ((RaidEventPayload)legacy.Payload) with { AttackForce = null, ReturnedSnapshotId = "returned" } });
        Check(module.CurrentUntil("legacy-defender", "defend-colony", Now) == DateTimeOffset.MaxValue, "Legacy completion lost protection.");
    }

    private static void Overrides() => WithDirectory(root =>
    {
        string database = Path.Combine(root, "state.sqlite");
        var ledger = new SqliteAuthoritativeEventLedger(database);
        var ended = Raid("settled"); ledger.Append(ended with { Payload = ((RaidEventPayload)ended.Payload) with { FinishedAtUtc = Now } });
        RaidProtectionActivationRegistry Activation() => new(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.RaidProtectionActivations), null);
        RaidProtection Open() => new(new SqliteAuthoritativeEventLedger(database), Activation(), new(new SqliteRaidCooldownOverrideStore(database)), () => new(), NullLogger.Instance);
        var module = Open(); module.OnLifecycle(ClientLifecycleEvent.LoggedIn("defender", "defend-colony", "s", "base", Now));
        Check(Activation().FindActivatedAt("settled", "defender", "defend-colony") == Now, "Activation was not persisted.");
        module.SetCurrent("defender", "defend-colony", 0, Now); module = Open();
        Check(module.CurrentUntil("defender", "defend-colony", Now) is null, "Clearing protection lost its suppression on restart.");
        module.SetCurrent("defender", "defend-colony", 1.5, Now); module = Open();
        Check(module.CurrentUntil("defender", "defend-colony", Now) == Now.AddMinutes(90), "Override failed to persist exact duration.");
        var next = Raid("new-raid"); ledger.Append(next with { Payload = ((RaidEventPayload)next.Payload) with { FinishedAtUtc = Now.AddHours(1) } });
        Check(module.CurrentUntil("defender", "defend-colony", Now) == DateTimeOffset.MaxValue, "Prior override suppressed a later raid.");
        module.OnLifecycle(ClientLifecycleEvent.LoggedIn("defender", "defend-colony", "new", "base", Now.AddHours(2)));
        Check(module.CurrentUntil("defender", "defend-colony", Now) == Now.AddHours(2) + RaidCooldownPolicy.Default.SettlementCooldown, "New raid did not start independent protection.");
    });

    private static void InvalidOverride()
    {
        var module = new RaidProtection(new InMemoryAuthoritativeEventLedger(), new(), new(), () => new(), NullLogger.Instance);
        foreach (double value in new[] { -1, double.NaN, double.PositiveInfinity, double.MaxValue })
        {
            bool rejected = false;
            try { module.SetCurrent("defender", "colony", value, Now); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && module.CurrentUntil("defender", "colony", Now) is null, "Invalid override changed registry: " + value);
        }
        bool overflow = false;
        try { module.SetCurrent("defender", "colony", 1, DateTimeOffset.MaxValue); } catch (ArgumentOutOfRangeException) { overflow = true; }
        Check(overflow, "Date overflow was accepted.");
    }

    private static void WithLedger(string storage, Action<IAuthoritativeEventLedger, Func<IAuthoritativeEventLedger>> run) => WithDirectory(root =>
    {
        var memory = new InMemoryAuthoritativeEventLedger();
        IAuthoritativeEventLedger Open() => storage switch {
            "file" => new FileAuthoritativeEventLedger(Path.Combine(root, "events.json")),
            "sqlite" => new SqliteAuthoritativeEventLedger(Path.Combine(root, "events.sqlite")), _ => memory };
        run(Open(), Open);
    });
    private static void WithDirectory(Action<string> run)
    {
        string root = Path.Combine(Path.GetTempPath(), "clash-raid-reads-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { run(root); } finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
