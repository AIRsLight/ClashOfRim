using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Achievements;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

internal static class AchievementAwardModuleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    internal static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var culture = CultureInfo.CurrentCulture; int count = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string fault in new[] { "none", "metrics", "append-before", "append-after" })
                foreach (bool reopen in new[] { false, true }) { Relocation(fault, reopen); count++; }
                foreach (TradeStage stage in Enum.GetValues<TradeStage>())
                foreach (ServerEventStatus status in Enum.GetValues<ServerEventStatus>()) { Trade(stage, status); count++; }
                foreach (string source in new[] { "applied", "delivered", "event" }) { Source(source); count++; }
                Plugins(); count++; InvalidAndIdentity(); count++; Labels(); count++;
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Console.WriteLine($"Achievement award independent module scenarios passed: {count}");
    }

    private static void Relocation(string fault, bool reopen)
    {
        var slot = new RecoveryTestSupport.Slot();
        var inner = new InMemoryAuthoritativeEventLedger();
        var ledger = DispatchProxy.Create<IAuthoritativeEventLedger, SnapshotSupportDeathTests.InterruptedLedger>();
        var probe = (SnapshotSupportDeathTests.InterruptedLedger)(object)ledger; probe.Inner = inner;
        var registry = new AchievementRegistry(slot); var hub = new EventNotificationHub(); var plugins = new ServerPluginRegistry();
        AchievementAwards Open() => new(registry, plugins, new(), ledger, new(), hub, () => NullLogger.Instance);
        var awards = Open();
        awards.RecordRelocation("user", "colony", "one", Now); awards.RecordRelocation("user", "colony", "one", Now);
        awards.RecordRelocation("user", "colony", "two", Now);
        Check(registry.ListAggregates().Count == 0 && inner.ListAll().Count == 0 && hub.GetVersion("user") == 0, "Relocation threshold or snapshot dedup changed.");
        slot.Fail = fault == "metrics";
        if (fault == "append-before") probe.BeforeAppend = () => throw new IOException("append interrupted");
        if (fault == "append-after") probe.AfterAppend = () => throw new IOException("append interrupted");
        bool failed = false;
        try { awards.RecordRelocation("user", "colony", "three", Now); } catch (IOException) { failed = true; }
        Check(failed == (fault != "none") && hub.GetVersion("user") == (failed ? 0 : 1), "Failed awards signaled success or did not inject failure.");
        if (fault == "metrics") Check(registry.ListAggregates().Count == 0 && new AchievementRegistry(slot).ListAggregates().Count == 0, "Failed award leaked into cache/disk.");
        slot.Fail = false; probe.BeforeAppend = probe.AfterAppend = null;
        if (reopen) { registry = new(slot); awards = Open(); }
        awards.RecordRelocation("user", "colony", "three", Now);
        awards.RecordRelocation("user", "colony", "three", Now);
        var unlocked = registry.ListAggregates().Single();
        Check(unlocked is { AchievementId: "relocations_3", Value: 3, SourceSnapshotId: "three" }
            && inner.ListAll().Count == 1 && hub.GetVersion("user") >= 2, "Award/notification retry lost or duplicated durable effects.");
    }

    private static AchievementDefinition Definition(string id, string metric) => AchievementDefinition.NumericThreshold(id, metric, 1, "Event", "missing.label", null);

    private static AuthoritativeEvent TradeEvent(TradeStage stage, ServerEventStatus status) => AuthoritativeEventFactory.Create(
        ServerEventType.Trade, new("seller", "home"), new("buyer", "away"), "trade", false,
        new TradeEventPayload("trade", stage,
            [new("gold", "Gold", 2, MarketValue: 20.25f), new("silver", "Silver", 11), new("unknown", "WoodLog", 100), new("negative", "Silver", -2)],
            [new("priced", "Steel", 4, MarketValue: 10.5f)], 999), Now) with
        { Status = status, AppliedSnapshotId = " applied ", DeliveredToSnapshotId = "delivered" };

    private static void Trade(TradeStage stage, ServerEventStatus status)
    {
        var registry = new AchievementRegistry(); registry.RegisterDefinition(Definition("count", AchievementRegistry.MetricCompletedTradeCount));
        registry.RegisterDefinition(Definition("value", AchievementRegistry.MetricCompletedTradeValue));
        var ledger = new InMemoryAuthoritativeEventLedger(); var hub = new EventNotificationHub();
        var plugins = new ServerPluginRegistry(); var baseline = new CompatibilityBaselineRegistry();
        var awards = new AchievementAwards(registry, plugins, baseline, ledger, new(), hub, () => NullLogger.Instance);
        var recorder = new EventAchievementRecorder(awards, plugins, baseline);
        var item = TradeEvent(stage, status); string before = JsonSerializer.Serialize(item);
        recorder.Record(item, Now); recorder.Record(item, Now);
        bool qualifies = status == ServerEventStatus.AppliedToSnapshot && stage is TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange;
        var rows = registry.ListAggregates();
        Check(rows.Count == (qualifies ? 4 : 0) && ledger.ListAll().Count == (qualifies ? 4 : 0), "Wrong trade stage/status qualification.");
        if (qualifies)
        {
            foreach (var party in new[] { ("seller", "home"), ("buyer", "away") })
            {
                Check(rows.Single(x => x.UserId == party.Item1 && x.ColonyId == party.Item2 && x.AchievementId == "count").Value == 1
                    && rows.Single(x => x.UserId == party.Item1 && x.ColonyId == party.Item2 && x.AchievementId == "value").Value == 52,
                    "Trade count/value changed: per-item rounding, silver fallback, minimum stack or maximum side.");
                Check(hub.GetVersion(party.Item1) == 2, "Replay did not retry wakeup.");
            }
        }
        Check(JsonSerializer.Serialize(item) == before, "Event metrics mutated the ledger input.");
    }

    private static void Source(string source)
    {
        var registry = new AchievementRegistry(); registry.RegisterDefinition(Definition("count", AchievementRegistry.MetricCompletedTradeCount));
        var plugins = new ServerPluginRegistry(); var baseline = new CompatibilityBaselineRegistry();
        var recorder = new EventAchievementRecorder(new(registry, plugins, baseline, new InMemoryAuthoritativeEventLedger(), new(), new(), () => NullLogger.Instance), plugins, baseline);
        var item = TradeEvent(TradeStage.SelfDeliveryExchange, ServerEventStatus.AppliedToSnapshot) with
        { AppliedSnapshotId = source == "applied" ? " applied " : " ", DeliveredToSnapshotId = source == "delivered" ? " delivered " : null };
        recorder.Record(item, Now);
        Check(registry.ListAggregates().All(x => x.SourceSnapshotId == (source == "event" ? item.EventId : source)), "Achievement snapshot fallback/trim changed.");
    }

    private static void Plugins()
    {
        var provider = new Metrics(false); var failing = new Metrics(true); var registry = new AchievementRegistry();
        var plugins = new ServerPluginRegistry().WithBuiltInPlugins([new("test.metrics", "test", "1", "test", "", [],
            AchievementDefinitions: [Definition("plugin", "plugin-metric")], AuthoritativeEventAchievementMetricProviders: [failing, provider], RequiredPackageIds: ["test.metrics"])]);
        var baseline = new CompatibilityBaselineRegistry(); var ledger = new InMemoryAuthoritativeEventLedger();
        var recorder = new EventAchievementRecorder(new(registry, plugins, baseline, ledger, new(), new(), () => NullLogger.Instance), plugins, baseline);
        var item = TradeEvent(TradeStage.MarketOrder, ServerEventStatus.Recorded);
        recorder.Record(item, Now); Check(provider.Calls == 0 && registry.ListAggregates().Count == 0, "Inactive plugin ran.");
        baseline.ReplaceBaseline(EntryTestSupport.Manifest() with { Mods = [new ModManifestEntry { PackageId = "test.metrics" }] }, "admin", Now);
        recorder.Record(item, Now); recorder.Record(item, Now);
        Check(provider.Calls == 2 && failing.Calls == 2 && registry.ListAggregates().Single() is { UserId: "seller", AchievementId: "plugin", Value: 2 }
            && ledger.ListAll().Count == 1, "Plugin failure stopped remaining providers or duplicate event counted twice.");
        baseline.ReplaceBaseline(EntryTestSupport.Manifest(), "admin", Now); recorder.Record(item, Now);
        Check(provider.Calls == 2, "Removed plugin remained active.");
    }

    private static void InvalidAndIdentity()
    {
        var registry = new AchievementRegistry(); var ledger = new InMemoryAuthoritativeEventLedger();
        var awards = new AchievementAwards(registry, new(), new(), ledger, new(), new(), () => NullLogger.Instance);
        var delta = new Dictionary<string, long> { [AchievementRegistry.MetricColonyRelocations] = 3 };
        foreach (var (user, colony, key) in new[] { ("", "colony", "key"), ("user", " ", "key"), ("user", "colony", "") })
            awards.RecordCumulative(user, colony, key, "snapshot", delta, Now);
        awards.RecordCumulative("user", "colony", "key", "snapshot", new Dictionary<string, long>(), Now);
        Check(registry.ListAggregates().Count == 0 && ledger.ListAll().Count == 0, "Invalid cumulative input persisted an award.");
        foreach (var (user, colony) in new[] { ("I", "colony"), ("ı", "colony"), ("I", "rebuilt") })
            awards.RecordCumulative(user, colony, "shared-key", "shared-snapshot", delta, Now);
        Check(registry.ListAggregates().Count == 3 && ledger.ListAll().Count == 3, "Award identity crossed culture or rebuilt colony.");
    }

    private static void Labels()
    {
        var ledger = new InMemoryAuthoritativeEventLedger(); var presence = new OnlinePresenceRegistry(); var hub = new EventNotificationHub();
        presence.TryConnectExclusive("user", out var lease);
        using (lease)
        {
            var awards = new AchievementAwards(new(), new(), new(), ledger, presence, hub, () => NullLogger.Instance);
            var record = new AchievementEventRecord("user", "colony", "fallback-id", "key", 1234, "Event", "missing.label", null, "Max", "source", Now, null);
            awards.Notify("user", "colony", [record], Now); awards.Notify("user", "colony", [record], Now);
            var notification = ledger.ListAll().Single(); var payload = (ServerNotificationEventPayload)notification.Payload;
            Check(payload.Message.Contains("fallback-id", StringComparison.Ordinal)
                && notification.DeliveryMode == ServerEventDeliveryMode.OnlineImmediate && hub.GetVersion("user") == 2, "Notification fallback, online delivery or replay wakeup changed.");
        }
    }

    private sealed class Metrics(bool fail) : IAuthoritativeEventAchievementMetricProvider
    {
        internal int Calls;
        public void CollectAuthoritativeEventAchievementMetrics(AuthoritativeEventAchievementMetricContext context)
        { Calls++; if (fail) throw new IOException("injected plugin failure"); context.AddMetric("plugin-metric", 2); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Achievement awards: " + message); }
}
