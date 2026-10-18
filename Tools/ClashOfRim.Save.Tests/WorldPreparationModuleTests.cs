using System.Globalization;
using System.Text.Json;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Network.Plugins.CoreCompatibility;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging.Abstractions;

internal static class WorldPreparationModuleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var culture = CultureInfo.CurrentCulture;
        int count = 0; void Case(Action action) { action(); count++; }
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string mode in new[] { "success", "recovery-failure", "recovery-ban", "player-failure", "final-ban", "final-password",
                    "final-maintenance", "final-mismatch", "final-defense", "final-admin" }) Case(() => Publication(mode));
                foreach (string mode in new[] { "success", "account-failure", "baseline-failure", "world-failure" }) Case(() => Bootstrap(mode));
                foreach (string mode in new[] { "requested", "active", "latest", "tie", "deleted", "site", "ordinal", "foreign", "historical", "ordinary", "foreign-tombstone", "default" })
                    Case(() => Selection(factory, mode));
                Case(ReadConfiguration);
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Console.WriteLine($"World preparation independent module scenarios passed: {count}");
    }

    private static void Publication(string mode)
    {
        var f = new Fixture(); var old = f.S.Issue();
        Check(f.S.Sessions.TryBeginStream("user", "colony", old.Session, Now), "Cannot seed active connection.");
        ColonyDeletionStageTests.SeedWorld(f.World, "user", "colony");
        f.S.Loans.Create("pending", "user", "colony", "base", 100, 1, 7, 1000, Now);
        f.S.Recovery.NeedsPreparation = true;
        f.S.Recovery.OnPrepare = () =>
        {
            Check(!Monitor.IsEntered(f.S.Gate) && f.S.PlayerSlot.Writes == 0, "World recovery prepared inside the gate or after activity publication.");
            if (mode == "recovery-failure") throw new IOException("Recovery unavailable.");
            if (mode == "recovery-ban") f.Admin.Ban("user");
        };
        f.S.PlayerSlot.Fail = mode == "player-failure";
        f.S.PlayerSlot.Before = _ =>
        {
            if (mode == "final-ban") f.Admin.Ban("user");
            if (mode == "final-password") f.Accounts.ResetPassword("user", "pw", Now, out _);
            if (mode == "final-maintenance") f.Admin.SetMaintenanceLoginLocked(true, "maintenance");
            if (mode == "final-mismatch") f.Baseline.ReplaceBaseline(EntryTestSupport.Manifest("changed", "different"), "admin", Now);
            if (mode == "final-admin") f.World.PromoteAdministrator("user");
            if (mode == "final-defense") f.S.Ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.Raid,
                new("attacker", "other"), new("user", "colony"), "defense", false,
                new RaidEventPayload("base", null, Now, null, null, new("attacker-base", ["pawn"], [])), Now));
        };
        PrepareWorldSessionResponse? result = null; bool threw = false;
        try { result = f.Prepare.Prepare(f.Request()); } catch (IOException) { threw = true; }
        bool failure = mode is "recovery-failure" or "player-failure";
        Check(threw == failure, "World preparation persistence/recovery exception changed: " + mode);
        Check(f.S.Valid(old) && LoginPreparationTests.TokenCount(f.S.Tokens) == 1 && !Monitor.IsEntered(f.S.Gate),
            "World preparation changed the active session/token or leaked the gate: " + mode);
        if (!failure)
        {
            Check(result!.Result.Accepted == (mode is "success" or "final-admin"), "World preparation accepted revoked policy: " + mode);
            if (result.Result.Accepted)
                Check(result.WorldConfiguration is null && result.WorldConfigured && result.HasExistingColony && result.AssignedColonyId == "colony"
                    && result.IsAdministrator == (mode == "final-admin") && f.S.Loans.FindByIdempotencyKey("pending") is null,
                    "World preparation lost lifecycle effects or returned stale session metadata.");
            if (mode == "final-defense") Check(result.HasExistingColony && result.AssignedColonyId == "colony", "Defense denial lost the resumable colony.");
        }
        if (mode is "recovery-failure" or "recovery-ban")
            Check(f.S.PlayerSlot.Writes == 0 && f.S.Loans.FindByIdempotencyKey("pending") is not null, "Failed authorization continued world entry.");
        f.S.Recovery.OnPrepare = null; f.S.PlayerSlot.Before = null; f.S.PlayerSlot.Fail = false;
        f.Admin.Unban("user"); f.Admin.SetMaintenanceLoginLocked(false, null);
        f.Baseline.ReplaceBaseline(EntryTestSupport.Manifest(), "admin", Now);
        foreach (var raid in f.S.Ledger.ListByType(ServerEventType.Raid)) f.S.Ledger.ChangeStatus(raid.EventId, ServerEventStatus.Cancelled);
        Check(f.Prepare.Prepare(f.Request()).Result.Accepted && f.S.Valid(old), "World entry did not recover on retry: " + mode);
    }

    private static void Bootstrap(string mode)
    {
        var f = new Fixture(fresh: true);
        f.Admin.SetMaintenanceLoginLocked(true, "maintenance");
        f.AccountSlot.Fail = mode == "account-failure"; f.BaselineSlot.Fail = mode == "baseline-failure"; f.WorldSlot.Fail = mode == "world-failure";
        bool threw = false; PrepareWorldSessionResponse? result = null;
        try { result = f.Prepare.Prepare(f.Request(signup: true)); } catch (IOException) { threw = true; }
        Check(threw == (mode != "success") && LoginPreparationTests.TokenCount(f.S.Tokens) == 0, "Bootstrap failure was swallowed or issued a login token.");
        if (!threw) Check(result!.Result.Accepted && result.IsAdministrator && result.RequiresInitialWorldConfiguration
            && !result.WorldConfigured && !result.HasExistingColony && result.AssignedColonyId == "colony", "First admin maintenance bootstrap changed.");
        else Check(f.S.PlayerSlot.Writes == 0 && f.World.ListAdministrators().Count == 0, "Failed bootstrap published admin/activity.");
        f.AccountSlot.Fail = f.BaselineSlot.Fail = f.WorldSlot.Fail = false;
        Check(f.Prepare.Prepare(f.Request(signup: true)).Result.Accepted && new OfflineAccountRegistry(f.AccountSlot).Authenticate("user", "pw", Now, false).Accepted
            && new CompatibilityBaselineRegistry(f.BaselineSlot).Current is not null && new WorldConfigurationRegistry(f.WorldSlot).IsAdministrator("user"),
            "Bootstrap retry did not persist credentials, baseline and administrator.");
    }

    private static void Selection(Func<string, SaveSnapshotPackage> factory, string mode)
    {
        var f = new Fixture(); var package = factory("world-selection");
        ColonyDeletionStageTests.SeedWorld(f.World, "user", "z-site");
        f.World.RegisterPlayerColonySites("user", "I-site", [new("user", "I-site", null, null, 0, "valid zero tile")]);
        f.World.RegisterPlayerColonySites("user", "invalid", [new("user", "invalid", null, null, -1, "invalid")]);
        string request = "missing", expected = "I-site"; bool existing = true;
        void Store(string owner, string colony, string id, DateTimeOffset at)
        { var envelope = package.Envelope with { Identity = new(owner, colony, id) }; f.S.Snapshots.StoreLatest(new(envelope.Identity, envelope, package.Index, at)); }
        if (mode is "requested" or "active" or "latest" or "tie" or "deleted")
        {
            Store("user", "first", "first-id", Now); Store("user", "second", "second-id", mode == "tie" ? Now : Now.AddMinutes(1));
            Store("other", "foreign", "foreign-id", Now.AddDays(1)); expected = mode == "tie" ? "first" : "second";
            if (mode == "requested") { request = "  first  "; expected = "first"; }
            if (mode == "active") { f.S.Players.Record("user", "first", "first-id", Now); expected = "first"; }
            if (mode == "deleted") { f.S.Players.MarkDeleted("user", "second", "second-id", Now); expected = "first"; }
        }
        if (mode == "site") { request = "z-site"; expected = "z-site"; }
        if (mode is "historical" or "ordinary" or "foreign-tombstone" or "foreign" or "default")
        {
            f.World.RemovePlayerColonySites("user", "I-site"); f.World.RemovePlayerColonySites("user", "z-site");
            expected = "missing"; existing = false;
            if (mode == "foreign") Store("other", "foreign", "foreign-id", Now);
            if (mode == "default") { request = ""; expected = "colony-user"; }
            if (mode is "historical" or "ordinary" or "foreign-tombstone")
            {
                var actor = mode == "foreign-tombstone" ? new EventParty("other", "missing") : new EventParty("user", "missing");
                var payload = new ServerNotificationEventPayload(mode == "ordinary" ? "notice" : ColonyTombstones.NotificationId("user", "missing"),
                    "title", "message", ServerNotificationSeverity.Info, false);
                // Legacy tombstones may lack the modern idempotency key/status; preserve historical matching.
                f.S.Ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.ServerNotification, actor, new("server"), "legacy", false, payload, Now));
            }
        }
        var result = f.Colonies.Resolve("user", request, f.World.Current, Now);
        if (mode == "historical")
            Check(result.ColonyId != "missing" && f.S.Players.IsDeleted("user", "missing") && !result.HasExistingColony, "Historical tombstone reused the abandoned colony ID.");
        else Check(result.ColonyId == expected && result.HasExistingColony == existing, "Colony selection changed: " + mode + " => " + result.ColonyId);
        if (mode is "requested" or "active" or "latest" or "tie" or "deleted")
            Check(result.Snapshot?.Identity.ColonyId == expected && result.Snapshot.Identity.OwnerId == "user", "Selected colony and snapshot diverged.");
    }

    private static void ReadConfiguration()
    {
        var f = new Fixture();
        Check(!f.Read.Get(new(ProtocolApiVersion.Current, "user", "colony", password: "pw")).Result.Accepted, "Unconfigured world became readable.");
        ColonyDeletionStageTests.SeedWorld(f.World, "user", "colony", gameLanguage: "German",
            extensions: [CoreRaidDifficultyServerCompatibility.BuildBaselineExtension(new("Cassandra", "Custom", "2.35", "1.5", "100", []))!]);
        string before = JsonSerializer.Serialize(f.World.Current);
        foreach (bool baseline in new[] { false, true })
        foreach (bool sites in new[] { false, true })
        foreach (bool extensions in new[] { false, true })
        {
            var response = f.Read.Get(new(ProtocolApiVersion.Current, "user", "colony", password: "pw",
                includeGenerationBaseline: baseline, includePlayerColonySites: sites, includeWorldExtensions: extensions));
            var config = response.WorldConfiguration;
            Check(response.Result.Accepted && config is not null && (config.SeedString == "seed") == baseline
                && (config.PlayerColonySites.Count > 0) == sites && config.GameLanguage == "German" && config.TileGeometry is null
                && config.Extensions.Count == (baseline && extensions ? 1 : 0),
                "World read lost request flags, language, or disclosed geometry.");
        }
        foreach (string mode in new[] { "protocol", "identity", "password" })
        {
            var response = f.Read.Get(new(mode == "protocol" ? "bad" : ProtocolApiVersion.Current, mode == "identity" ? "" : "user", "colony",
                password: mode == "password" ? "wrong" : "pw"));
            Check(!response.Result.Accepted && response.WorldConfiguration is null, "Invalid world read disclosed configuration.");
        }
        Check(before == JsonSerializer.Serialize(f.World.Current) && f.S.Recovery.Replays == 0 && f.S.PlayerSlot.Writes == 0,
            "World read recovered or mutated world/player state.");
    }

    private sealed class Fixture
    {
        internal readonly SessionModuleTestContext S = new();
        internal readonly RecoveryTestSupport.Slot AccountSlot = new(), BaselineSlot = new(), WorldSlot = new();
        internal readonly OfflineAccountRegistry Accounts;
        internal readonly CompatibilityBaselineRegistry Baseline;
        internal readonly AdminControlRegistry Admin = new();
        internal readonly WorldConfigurationRegistry World;
        internal readonly WorldColonySelection Colonies;
        internal readonly WorldSessionPreparation Prepare;
        internal readonly WorldConfigurationRead Read;
        internal readonly string Manifest = CompatibilityManifestJsonWriter.Write(EntryTestSupport.Manifest());
        internal Fixture(bool fresh = false)
        {
            Accounts = new(AccountSlot); Baseline = new(BaselineSlot); World = fresh ? new(WorldSlot) : new();
            if (!fresh) { Check(Accounts.Authenticate("user", "pw", Now, true).Accepted, "Cannot seed account."); World.Prepare("admin"); }
            var credentials = new AccountService(Accounts, S.Tokens, new RecoveryTestSupport.SteamValidator(), () => S.Configuration);
            var handshakes = new EntryHandshakePolicy(Admin, World, S.Sessions, new(Baseline, World, () => new()));
            var protection = new RaidProtection(S.Ledger, new(), new(), () => S.Configuration, NullLogger.Instance);
            Colonies = new(S.Players, S.Snapshots, S.Ledger);
            Prepare = new(credentials, handshakes, S.Mutations, S.Timeouts, S.Queries, World, Colonies, S.Activity, new(protection, S.Pending));
            Read = new(credentials, World, new(new ServerPluginRegistry().WithBuiltInPlugins([CoreRaidDifficultyServerCompatibility.Descriptor]), Baseline));
        }
        internal PrepareWorldSessionRequest Request(bool signup = false) => new(ProtocolApiVersion.Current, "user", "colony", Manifest, password: "pw", createAccountIfMissing: signup);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
