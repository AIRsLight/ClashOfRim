using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class EventConfirmationStageTests
{
    private static readonly DateTimeOffset Applied = DateTimeOffset.UnixEpoch.AddDays(4);
    private static readonly string[] Kinds = ["gift", "loss", "support", "return", "trade", "drop-pod"];

    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (bool reopen in new[] { false, true })
        {
            foreach (string kind in Kinds) VerifyRollback(fixture, kind, reopen);
            foreach (string table in new[] { "server_achievement_metric_events", "server_achievement_events", "events" })
            foreach (string user in new[] { "userA", "other" }) VerifyAchievementRecovery(fixture, table, user, reopen);
        }
        foreach (string storage in new[] { "memory", "file", "sqlite" })
        {
            foreach (string kind in Kinds) VerifyLegacyAndOwnership(fixture, storage, kind);
            VerifyBatchReplay(storage);
        }
        VerifyBatchRollback();
        VerifyIncompleteLegacy(fixture);
    }

    private static void VerifyRollback(Func<string, SaveSnapshotPackage> fixture, string kind, bool reopen) => WithDirectory(root =>
    {
        string database = Path.Combine(root, "events.sqlite");
        IAuthoritativeEventLedger Open() => new SqliteAuthoritativeEventLedger(database);
        var ledger = Open();
        var source = Source(kind);
        ledger.Append(source);
        source = ledger.Find(source.EventId)!;
        var state = new ClashOfRimNetworkState(ledger: ledger);
        // The old path has already written MarkDelivered/MarkApplied when this final write fails.
        Sql(database, "create trigger fail_result before update on events when new.last_application_result = 'Applied' "
            + "begin select raise(abort, 'injected application result failure'); end;");
        Expect<SqliteException>(() => Confirm(state, fixture, kind), "application failure not reached");
        Require(Same(source, ledger.Find(source.EventId)!) && Same(source, Open().Find(source.EventId)!),
            kind + ": failed confirmation left a partial delivery/application/result");
        Sql(database, "drop trigger fail_result;");
        if (reopen) state = new ClashOfRimNetworkState(ledger: Open());
        Require(Confirm(state, fixture, kind).Result.Accepted, kind + ": retry rejected");
        VerifyApplied(state.Ledger.Find(source.EventId)!, "confirmed", Applied);
        var completed = state.Ledger.Find(source.EventId)!;
        var repeated = Confirm(state, fixture, kind, snapshot: "later", at: Applied.AddHours(1));
        Require(repeated.Result.Accepted && repeated.AppliedSnapshotId == "confirmed"
            && Same(completed, Open().Find(source.EventId)!), kind + ": repeat changed original attribution");
    });

    private static void VerifyLegacyAndOwnership(Func<string, SaveSnapshotPackage> fixture, string storage, string kind) => WithDirectory(root =>
    {
        var ledger = OpenLedger(storage, root);
        var original = Source(kind) with { Status = ServerEventStatus.AppliedToSnapshot,
            DeliveredToSnapshotId = "base", DeliveredAtUtc = Applied.AddMinutes(-1),
            AppliedSnapshotId = "original", AppliedAtUtc = Applied };
        ledger.Append(original);
        var state = new ClashOfRimNetworkState(ledger: ledger);
        Require(!Confirm(state, fixture, kind, user: "intruder").Result.Accepted, kind + ": wrong party got duplicate success");
        Require(Same(original, ledger.Find(original.EventId)!), kind + ": wrong party repaired or damaged an event");
        Require(state.Achievements.ListAggregates().Count == 0, "wrong party recorded achievements");
        if (kind is "loss" or "trade" or "drop-pod")
        {
            Require(!Confirm(state, fixture, kind, source: "wrong-source").Result.Accepted, "wrong source accepted as duplicate");
            Require(Same(original, ledger.Find(original.EventId)!), "wrong source changed an applied event");
        }
        var result = Confirm(state, fixture, kind, snapshot: "later", at: Applied.AddDays(1));
        Require(result.Result.Accepted && result.AppliedSnapshotId == "original", kind + ": legacy retry rejected");
        var repaired = ledger.Find(original.EventId)!;
        VerifyApplied(repaired, "original", Applied);
        Require(repaired.DeliveredAtUtc == original.DeliveredAtUtc, "legacy repair changed delivery time");
        Confirm(state, fixture, kind, at: Applied.AddDays(2));
        Require(Same(repaired, ledger.Find(original.EventId)!), "second repair rewrote the event");
        if (storage != "memory") Require(Same(repaired, OpenLedger(storage, root).Find(original.EventId)!), "repair was not durable");
    });

    private static void VerifyIncompleteLegacy(Func<string, SaveSnapshotPackage> fixture)
    {
        foreach (bool missingTime in new[] { false, true })
        {
            var state = new ClashOfRimNetworkState();
            var source = Source("trade") with { Status = ServerEventStatus.AppliedToSnapshot,
                AppliedSnapshotId = missingTime ? "original" : null, AppliedAtUtc = missingTime ? null : Applied };
            state.Ledger.Append(source);
            state.Achievements.RecordCumulativeMetricAchievements("userA", "colonyA", "seed", "prior",
                new Dictionary<string, long> { [AchievementRegistry.MetricCompletedTradeCount] = 9 }, [], Applied.AddDays(-1));
            Confirm(state, fixture, "trade", at: Applied.AddDays(1));
            Require(Same(source, state.Ledger.Find("trade")!) && state.Achievements.ListAggregates().Count == 0
                && state.Ledger.ListAll().Count == 1, "incomplete legacy attribution was guessed");
        }
    }

    private static void VerifyAchievementRecovery(Func<string, SaveSnapshotPackage> fixture, string table, string user, bool reopen) => WithDirectory(root =>
    {
        string database = Path.Combine(root, "server.sqlite");
        ServerDatabaseMigrator.Migrate(database);
        ClashOfRimNetworkState Open() => new(ledger: new SqliteAuthoritativeEventLedger(database),
            achievements: new AchievementRegistry(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.Achievements), null));
        var state = Open();
        state.Ledger.Append(Source("trade"));
        foreach (var party in new[] { new EventParty("userA", "colonyA"), new EventParty("other", "other-colony") })
            state.Achievements.RecordCumulativeMetricAchievements(party.UserId, party.ColonyId!, "seed", "prior",
                new Dictionary<string, long> { [AchievementRegistry.MetricCompletedTradeCount] = 9 }, [], Applied.AddDays(-1));
        string condition = table == "events" ? $"new.target_user_id = '{user}'"
            : $"json_extract(new.content_json, '$.UserId') = '{user}'";
        Sql(database, $"create trigger fail_achievement before insert on {table} when {condition} "
            + "begin select raise(abort, 'injected trade achievement failure'); end;");
        Expect<SqliteException>(() => Confirm(state, fixture, "trade"), "trade achievement failure not reached: " + table + "/" + user);
        VerifyApplied(state.Ledger.Find("trade")!, "confirmed", Applied);
        Sql(database, "drop trigger fail_achievement;");
        if (reopen) state = Open();
        Confirm(state, fixture, "trade", snapshot: "retry", at: Applied.AddDays(1));
        Confirm(state, fixture, "trade", snapshot: "retry", at: Applied.AddDays(2));
        state = Open();
        Confirm(state, fixture, "trade", snapshot: "retry", at: Applied.AddDays(3));
        VerifyApplied(state.Ledger.Find("trade")!, "confirmed", Applied);
        var unlocks = state.Achievements.ListAggregates().Where(item => item.AchievementId == "trades_10").ToArray();
        Require(unlocks.Length == 2 && unlocks.All(item => item.Value == 10), "trade retry lost or duplicated an unlock");
        Require(state.Ledger.ListAll().Count == 3, "trade retry lost or duplicated achievement notifications");
        Require(Scalar(database, "select count(*) from server_achievement_metric_events;") == 6,
            "trade retry counted the same metric twice");
        Require(Scalar(database, "select count(*) from server_achievement_metric_aggregates where json_extract(content_json, '$.Value') = 10;") == 2,
            "both parties did not retain exactly ten completed trades");
        foreach (var notification in state.Ledger.ListAll().Where(item => item.EventId != "trade"))
            Require(notification.CreatedAtUtc == Applied, "recovery attributed notification to retry time");
    });

    private static void VerifyBatchReplay(string storage) => WithDirectory(root =>
    {
        var ledger = OpenLedger(storage, root);
        var first = Source("support");
        var second = Source("return");
        ledger.Append(first); ledger.Append(second);
        first = ledger.Find(first.EventId)!; second = ledger.Find(second.EventId)!;
        var batch = new LedgerBatch([], [], [new(first, "base", "confirmed", Applied), new(second, "base", "confirmed", Applied)]);
        ledger.ApplyBatch(batch);
        if (storage != "memory") ledger = OpenLedger(storage, root);
        ledger.ApplyBatch(batch);
        VerifyApplied(ledger.Find(first.EventId)!, "confirmed", Applied);
        // A matching status alone is insufficient: a later report or another application must conflict.
        var completed = ledger.Find(first.EventId)!;
        Expect<InvalidOperationException>(() => ledger.ApplyBatch(new LedgerBatch([], [], [new(first, "base", "other", Applied)])),
            "different application accepted as replay");
        ledger.ReportApplicationResult(first.EventId, EventApplicationResultKind.NeedsManualReview, "later failure", null);
        Expect<InvalidOperationException>(() => ledger.ApplyBatch(batch), "replay overwrote changed result metadata");
        Require(ledger.Find(first.EventId)!.LastFailureReason == "later failure", "replay damaged newer metadata");
        // Status-only batches still require their expected state; application replay must not weaken them.
        Expect<InvalidOperationException>(() => ledger.ApplyBatch(new LedgerBatch([], [new(second.EventId, second.Status, ServerEventStatus.AppliedToSnapshot)])),
            "status-only stale batch was accepted");
        Require(Same(completed, batch.ApplicationChanges![0].Updated), "batch did not preserve exact original application");
    });

    private static void VerifyBatchRollback() => WithDirectory(root =>
    {
        string database = Path.Combine(root, "events.sqlite");
        var ledger = new SqliteAuthoritativeEventLedger(database);
        var first = Source("support"); var second = Source("return");
        ledger.Append(first); ledger.Append(second);
        first = ledger.Find(first.EventId)!; second = ledger.Find(second.EventId)!;
        Sql(database, "create trigger fail_second before update on events when new.event_id = 'return' begin select raise(abort, 'second application'); end;");
        Expect<SqliteException>(() => ledger.ApplyBatch(new LedgerBatch([], [],
            [new(first, "base", "confirmed", Applied), new(second, "base", "confirmed", Applied)])), "second application failure not reached");
        foreach (var current in new IAuthoritativeEventLedger[] { ledger, new SqliteAuthoritativeEventLedger(database) })
            Require(Same(first, current.Find(first.EventId)!) && Same(second, current.Find(second.EventId)!), "application batch partially committed");
    });

    private static AuthoritativeEvent Source(string kind)
    {
        var owner = new EventParty("userA", "colonyA");
        var other = new EventParty("other", "other-colony");
        bool trade = kind is "trade" or "drop-pod";
        LedgerEventPayload payload = kind switch
        {
            "gift" => new ItemDeliveryEventPayload([], "gift"),
            "loss" => new RaidEventPayload("defender", null, null, null, null,
                AttackerLoss: new RaidAttackerLossRecord("source", "base", ["pawn:absent"], [], "lost")),
            "trade" or "drop-pod" => new TradeEventPayload("source", kind == "trade" ? TradeStage.SelfDeliveryExchange : TradeStage.ServerDropPodExchange,
                [], [new EventThingReference("thing:payment", "Silver", 1, "base")], 0),
            _ => new SupportPawnEventPayload("pawn:support", "source", "Ally", true, null, ReturnToSender: kind == "return")
        };
        var type = trade ? ServerEventType.Trade : kind == "gift" ? ServerEventType.ItemDelivery
            : kind == "loss" ? ServerEventType.Raid : ServerEventType.SupportPawn;
        return AuthoritativeEventFactory.Create(type, trade ? owner : other, trade ? other : owner, kind, false, payload, Applied.AddDays(-1)) with
        {
            EventId = kind, LastApplicationResult = EventApplicationResultKind.NeedsManualReview,
            LastFailureReason = "old failure", NextRetryAtUtc = Applied.AddMinutes(-1),
            DeliveredToSnapshotId = kind is "gift" or "loss" ? "base" : kind == "return" ? "" : null,
            DeliveredAtUtc = kind is "gift" or "loss" ? Applied.AddHours(-1) : null
        };
    }

    private static ConfirmEventApplicationResultDto Confirm(ClashOfRimNetworkState state, Func<string, SaveSnapshotPackage> fixture,
        string kind, string user = "userA", string snapshot = "confirmed", DateTimeOffset? at = null, string source = "source")
    {
        var package = fixture(snapshot);
        var accepted = new LatestSnapshotRecord(package.Envelope.Identity, package.Envelope with { PreviousSnapshotId = "base" }, package.Index, at ?? Applied);
        var application = new ConfirmEventApplicationEntry(kind, source, kind == "gift" ? "ItemDeliveryAnchored"
            : kind == "return" ? "SupportPawnReturnCaravanCreated" : "SupportPawnArrived");
        try { return (ConfirmEventApplicationResultDto)typeof(ClashOfRimNetworkServer)
            .GetMethod("ConfirmEventApplicationAfterSnapshot", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { application, user, "colonyA", "base", accepted, state, at ?? Applied })!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }

    private static IAuthoritativeEventLedger OpenLedger(string storage, string root) => storage switch
    {
        "memory" => new InMemoryAuthoritativeEventLedger(),
        "file" => new FileAuthoritativeEventLedger(Path.Combine(root, "events.json")),
        _ => new SqliteAuthoritativeEventLedger(Path.Combine(root, "events.sqlite"))
    };
    private static void VerifyApplied(AuthoritativeEvent item, string snapshot, DateTimeOffset at) => Require(
        item.Status == ServerEventStatus.AppliedToSnapshot && item.AppliedSnapshotId == snapshot && item.AppliedAtUtc == at
        && item.DeliveredToSnapshotId == "base" && item.DeliveredAtUtc is not null
        && item.LastApplicationResult == EventApplicationResultKind.Applied && item.LastFailureReason is null && item.NextRetryAtUtc is null,
        "application metadata is incomplete or changed attribution");
    private static bool Same(AuthoritativeEvent left, AuthoritativeEvent right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right)
        && JsonSerializer.Serialize(left.Payload, left.Payload.GetType()) == JsonSerializer.Serialize(right.Payload, right.Payload.GetType());
    private static void Require(bool condition, string message) { if (!condition) throw new Exception("Confirmation stage: " + message); }
    private static void Expect<T>(Action action, string message) where T : Exception { try { action(); } catch (T) { return; } throw new Exception(message); }
    private static long Scalar(string database, string sql)
    { using var connection = new SqliteConnection("Data Source=" + database); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
    private static void Sql(string database, string sql)
    { using var connection = new SqliteConnection("Data Source=" + database); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static void WithDirectory(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-confirmation-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
