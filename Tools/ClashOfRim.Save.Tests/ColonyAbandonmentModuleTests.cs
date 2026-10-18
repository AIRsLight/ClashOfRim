using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ColonyAbandonmentModuleTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var culture = CultureInfo.CurrentCulture; var package = factory("abandon-module"); int count = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string mode in new[] { "success", "protocol", "user", "colony", "key", "token", "missing-token", "wrong-user", "wrong-colony",
                    "revoked", "replaced", "deleted", "stale", "optional", "missing", "unconfigured", "prepare-failure", "prepare-revoked", "prepare-replaced",
                    "prepare-deleted", "prepare-stale", "replay-revoked", "replay-replaced", "replay-deleted", "replay-stale" })
                { Flow(package, mode); count++; }
                foreach (bool resume in new[] { false, true }) { RecoveryAndReplacement(package, resume); count++; }
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Http(factory); count += 2;
        Console.WriteLine($"Colony abandonment independent module scenarios passed: {count}");
    }

    private static void Http(Func<string, SaveSnapshotPackage> factory)
    {
        using var f = new HttpSnapshotRecoveryTests.Fixture(factory);
        ColonyDeletionStageTests.SeedWorld(f.State.WorldConfiguration, "receiver", "away");
        f.Start(); f.Seed(); string before = f.Evidence();
        using var response = f.Post(ProtocolMessageKind.AbandonPlayerColony,
            new AbandonPlayerColonyRequest(ProtocolApiVersion.Current, "receiver", "away", null, "delete", "invalid"));
        response.EnsureSuccessStatusCode();
        var rejected = response.Content.ReadFromJsonAsync<AbandonPlayerColonyResponse>().GetAwaiter().GetResult()!;
        Check(!rejected.Result.Accepted && rejected.WorldConfiguration is null, "Invalid token accepted abandonment or leaked world.");
        f.AssertPending(before, "Invalid HTTP abandonment");
        f.Store.BeforePrepare = () => Check(!Monitor.IsEntered(f.State.RaidSettlementSnapshotMutationGate), "HTTP abandonment prepared inside global gate.");
        using var valid = f.Post(ProtocolMessageKind.AbandonPlayerColony,
            new AbandonPlayerColonyRequest(ProtocolApiVersion.Current, "receiver", "away", null, "delete", f.Token));
        valid.EnsureSuccessStatusCode();
        var result = valid.Content.ReadFromJsonAsync<AbandonPlayerColonyResponse>().GetAwaiter().GetResult()!;
        Check(result.Result.Accepted && result.RemovedSites == 2 && result.RemovedEvents == 0
            && f.State.Players.IsDeleted("receiver", "away") && f.State.Ledger.Find(f.Source.EventId)!.Status == ServerEventStatus.Cancelled,
            "Authorized HTTP abandonment did not delete colony and close target event.");
        f.AssertRecovered();
    }

    private static void Flow(SaveSnapshotPackage package, string mode)
    {
        var f = new Fixture(package);
        void Change(string change)
        {
            if (change == "revoked") f.S.Tokens.RevokeForUser("user");
            if (change == "replaced") { f.S.Sessions.EndUser("user"); f.S.Issue(); }
            if (change == "deleted") f.S.Players.MarkDeleted("user", "colony", "before", DateTimeOffset.UtcNow);
            if (change == "stale") f.Store("newer");
            if (change == "unconfigured") f.World.Submit("admin", f.World.Current!);
        }
        Change(mode);
        if (mode == "missing") f.S.Snapshots.RemoveLatest("user", "colony");
        string before = f.Evidence();
        f.S.Recovery.NeedsPreparation = true;
        f.S.Recovery.OnPrepare = () =>
        {
            Check(!Monitor.IsEntered(f.S.Gate), "Recovery preparation held mutation gate.");
            if (mode.StartsWith("prepare-", StringComparison.Ordinal)) Change(mode[8..]);
            before = f.Evidence();
            if (mode == "prepare-failure") throw new IOException("Injected preparation failure.");
        };
        f.S.Recovery.OnReplay = () =>
        {
            Check(Monitor.IsEntered(f.S.Gate), "Replay escaped mutation gate.");
            if (mode.StartsWith("replay-", StringComparison.Ordinal)) Change(mode[7..]);
            before = f.Evidence();
        };
        f.WorldSlot.Before = _ => Check(Monitor.IsEntered(f.S.Gate), "Deletion published world outside mutation gate.");
        var request = new AbandonPlayerColonyRequest(mode == "protocol" ? "bad" : ProtocolApiVersion.Current,
            mode == "user" ? " " : mode == "wrong-user" ? "other" : "user", mode == "colony" ? "" : mode == "wrong-colony" ? "other" : "colony",
            mode == "optional" ? null : "before", mode == "key" ? "" : "delete", mode == "token" ? "bad" : mode == "missing-token" ? null : f.Token);
        AbandonPlayerColonyResponse? result = null; bool failed = false;
        try { result = f.Module.Abandon(request); } catch (IOException) { failed = true; }
        Check(failed == (mode == "prepare-failure"), "Unexpected exception: " + mode);
        bool accepted = mode is "success" or "optional" or "missing" or "unconfigured";
        if (!failed) Check(result!.Result.Accepted == accepted, "Wrong acceptance: " + mode);
        Check(!Monitor.IsEntered(f.S.Gate), "Leaked mutation gate.");
        if (accepted)
        {
            Check(result!.RemovedSnapshots == (mode == "missing" ? 0 : 1) && result.RemovedSites == 2
                && result.RemovedEvents == 0 && result.RemovedRuntimeMarkers == 1, "Deletion response counts changed.");
            f.AssertDeleted();
            string done = f.Evidence();
            Check(!f.Module.Abandon(request).Result.Accepted && done == f.Evidence(), "Old token repeated deletion after session retirement.");
        }
        else Check(before == f.Evidence() && !f.Journal.HasPending, "Rejected request wrote deletion state: " + mode);
        if (mode is "protocol" or "user" or "colony" or "key" or "token" or "missing-token" or "wrong-user" or "wrong-colony" or "revoked" or "replaced" or "deleted")
            Check(f.S.Recovery.Replays == 0 && result!.WorldConfiguration is null, "Unauthorized/malformed request recovered or leaked world.");
        if (failed) { f.S.Recovery.OnPrepare = null; Check(f.Module.Abandon(request).Result.Accepted, "Recovery failure could not retry."); f.AssertDeleted(); }
    }

    private static void RecoveryAndReplacement(SaveSnapshotPackage package, bool resume)
    {
        var f = new Fixture(package);
        if (resume)
        {
            f.WorldSlot.Fail = true; bool failed = false;
            try { f.Module.Abandon(new(ProtocolApiVersion.Current, "user", "colony", "before", "delete", f.Token)); }
            catch (IOException) { failed = true; }
            Check(failed && f.Journal.HasPending && f.S.Snapshots.GetLatest("user", "colony") is null && f.World.Current!.PlayerColonySites.Count == 4,
                "Interruption lost deletion intent or published failed world candidate.");
            f.WorldSlot.Fail = false;
        }
        else { f.Deletion.Delete("user", "colony", DateTimeOffset.UtcNow); f.AssertDeleted(); }
        f.S.Sessions.EndUser("user"); var now = DateTimeOffset.UtcNow;
        Check(f.S.Sessions.TryCreate("user", "rebuilt", now, out var session) && f.S.Sessions.TryBeginStream("user", "rebuilt", session, now), "Could not create replacement.");
        string token = f.S.Tokens.Issue("user", "user", "rebuilt", session, now);
        Check(f.S.Presence.TryConnectExclusive("user", session, out var lease), "Could not create replacement presence.");
        using var replacement = lease!;
        f.S.Players.Record("user", "rebuilt", null, now);
        if (resume) f.Deletion.Recover(); else f.Deletion.Delete("user", "colony", now);
        f.AssertDeleted();
        Check(f.S.Sessions.IsValid("user", "rebuilt", session, now) && f.S.Tokens.IsValid(token, "user", "rebuilt", now)
            && replacement.Touch() && !f.S.Players.IsDeleted("user", "rebuilt"), "Old deletion cleanup damaged replacement session.");
        string done = f.Evidence(); f.Deletion.Recover(); Check(done == f.Evidence(), "Completed recovery repeated effects.");
    }

    private sealed class Fixture
    {
        internal readonly SessionModuleTestContext S = new();
        internal readonly RecoveryTestSupport.Slot WorldSlot = new();
        internal readonly WorldConfigurationPersistenceTests.MemoryBinary Binary = new();
        internal readonly ColonyDeletionService Journal = new();
        internal readonly EventNotificationHub WorldNotifications = new();
        internal readonly RuntimeWorldObjectMarkerRegistry Runtime = new();
        internal readonly RaidProtectionActivationRegistry Protection = new();
        internal readonly MercenaryContractRegistry Mercenaries = new();
        internal readonly DiplomacyRelationRegistry Relations = new();
        internal readonly WorldConfigurationRegistry World;
        internal readonly ColonyDeletion Deletion;
        internal readonly ColonyAbandonment Module;
        internal readonly string Token;
        private readonly SaveSnapshotPackage package;
        internal Fixture(SaveSnapshotPackage package)
        {
            this.package = package; Token = S.Issue().Token; Store();
            var plugins = new ServerPluginRegistry(); var baseline = new CompatibilityBaselineRegistry();
            World = new(WorldSlot, Binary); ColonyDeletionStageTests.SeedWorld(World, "user", "colony");
            S.Players.Record("user", "colony", "before", DateTimeOffset.UtcNow);
            S.Players.Record("watcher", "other", null, DateTimeOffset.UtcNow);
            var delivery = new WorldConfigurationDelivery(plugins, baseline);
            var raids = new RaidMarkerProjection(S.Ledger, S.Snapshots);
            var markers = new WorldMarkerDelivery(S.Players, S.Presence, Relations, S.Snapshots, World, delivery, Runtime, raids,
                new RaidProtection(S.Ledger, Protection, new(), () => S.Configuration, NullLogger.Instance), new(S.Players, S.Snapshots), () => S.Configuration);
            Deletion = new(S.Snapshots, S.Ledger, Journal, new(), S.Mutations, World, plugins, baseline, Runtime,
                Relations, S.Loans, S.Players, new(), Protection, Mercenaries, S.Connections, S.Events, new(S.Players, WorldNotifications));
            Module = new(new(new(S.Tokens, S.Sessions, Relations, new(), S.Ledger, () => S.Configuration.PvpEnabled), S.Players), S.Mutations,
                S.Snapshots, Deletion, World, delivery, markers, () => NullLogger.Instance);
            var now = DateTimeOffset.UtcNow;
            foreach (string colony in new[] { "colony", "other" })
            {
                S.Loans.Create(colony, "user", colony, "before", 100, 10, 1, 0, now);
                Mercenaries.Create(colony, "user", colony, "before", "Shooting", 10, 1, 100, 10, 20, 0, now, colony);
                Protection.ActivateIfMissing(colony, "user", colony, now);
                Relations.SetRelationKind("user", colony, "watcher", "other", DiplomacyRelationRegistry.RelationAlly, null, now);
                Runtime.ReplaceForOwner("user", colony, "before", [new(colony, "Caravan", "Caravan", 9, "商队")], now);
            }
            var order = AuthoritativeEventFactory.Create(ServerEventType.Trade, new("user", "colony"), new("server"), "order", false,
                new TradeEventPayload("order", TradeStage.MarketOrder, [], [], 0), now);
            S.Ledger.Append(order);
            S.Ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.Trade, new("buyer", "home"), new("buyer", "home"), "memo", false,
                new TradeEventPayload(order.EventId, TradeStage.AcceptedMemo, [], [], 0), now));
        }
        internal void Store(string id = "before")
        {
            var identity = new SnapshotIdentity("user", "colony", id);
            S.Snapshots.StoreLatest(new(identity, package.Envelope with { Identity = identity }, package.Index, DateTimeOffset.UtcNow));
        }
        internal string Evidence() => JsonSerializer.Serialize(new { World = WorldSlot.Read(), Players = S.PlayerSlot.Read(), Snapshots = S.Snapshots.ListLatest(),
            Events = S.Ledger.ListAll(), Notify = S.Events.GetVersion("buyer"), WorldNotify = WorldNotifications.GetVersion("watcher"), Markers = Runtime.ListVisibleMarkers("observer", DateTimeOffset.UtcNow) });
        internal void AssertDeleted()
        {
            Check(!Journal.HasPending && S.Snapshots.GetLatest("user", "colony") is null && S.Players.IsDeleted("user", "colony")
                && !S.Tokens.IsValid(Token, "user", "colony", DateTimeOffset.UtcNow), "Deletion left snapshot, identity or token.");
            var current = new WorldConfigurationRegistry(WorldSlot, Binary).Current;
            Check(current is null || current.PlayerColonySites.Count == 2 && current.PlayerColonySites.All(x => x.ColonyId != "colony"), "Deletion changed foreign world sites.");
            Check(S.Loans.FindByIdempotencyKey("colony") is null && S.Loans.FindByIdempotencyKey("other") is not null
                && Mercenaries.FindByIdempotencyKey("colony")?.Status == MercenaryContractRegistry.StatusCompleted
                && Mercenaries.FindByIdempotencyKey("other")?.Status == MercenaryContractRegistry.StatusPendingActivation
                && Protection.FindActivatedAt("colony", "user", "colony") is null && Protection.FindActivatedAt("other", "user", "other") is not null,
                "Deletion lost registry ownership.");
            Check(Relations.GetRelationKind("user", "other", "watcher", "other") == DiplomacyRelationRegistry.RelationAlly
                && Relations.GetRelationKind("user", "colony", "watcher", "other") != DiplomacyRelationRegistry.RelationAlly
                && Runtime.ListVisibleMarkers("observer", DateTimeOffset.UtcNow).Single().ColonyId == "other", "Deletion affected foreign relations/markers.");
            Check(S.Ledger.ListAll().Count == 3 && S.Ledger.FindByIdempotencyKey("order")!.Status == ServerEventStatus.Cancelled
                && S.Ledger.FindByIdempotencyKey("memo")!.Status == ServerEventStatus.Cancelled && S.Events.GetVersion("buyer") > 0
                && WorldNotifications.GetVersion("watcher") > 0, "Deletion lost event graph or notification recipients.");
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Colony abandonment module: " + message); }
}
