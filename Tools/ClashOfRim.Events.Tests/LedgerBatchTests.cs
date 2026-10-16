using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Data.Sqlite;
using System.Text.Json;

internal static class LedgerBatchTests
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-ledger-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string database = Path.Combine(root, "ledger.sqlite");
            string json = Path.Combine(root, "ledger.json");
            foreach (IAuthoritativeEventLedger ledger in new IAuthoritativeEventLedger[]
                { new InMemoryAuthoritativeEventLedger(), new FileAuthoritativeEventLedger(json), new SqliteAuthoritativeEventLedger(database) })
            {
                AuthoritativeEvent order = Event("order");
                ledger.Append(order);
                var change = new LedgerStatusChange(order.EventId, order.Status, ServerEventStatus.AppliedToSnapshot);
                var first = new LedgerBatch(new[] { Event("delivery-a"), Event("delivery-b") }, new[] { change });
                var conflict = new LedgerBatch(new[] { Event("early"), Event("order") with { EventId = "conflict" } }, new[] { change });
                ExpectFailure(() => ledger.ApplyBatch(conflict));
                Require(ledger.ListAll().Count == 1 && ledger.Find("order")!.Status == order.Status, "Validation leaked a partial batch.");

                if (ledger is SqliteAuthoritativeEventLedger)
                {
                    Execute(database, "create trigger fail_batch before update on events begin select raise(abort, 'injected batch failure'); end;");
                    ExpectFailure(() => ledger.ApplyBatch(first));
                    Require(ledger.ListAll().Count == 1 && ledger.Find("order")!.Status == order.Status, "SQL failure did not roll back inserted deliveries.");
                    Execute(database, "drop trigger fail_batch;");
                }
                if (ledger is FileAuthoritativeEventLedger)
                {
                    Directory.CreateDirectory(json + ".tmp");
                    ExpectFailure(() => ledger.ApplyBatch(first));
                    Require(ledger.ListAll().Count == 1 && new FileAuthoritativeEventLedger(json).ListAll().Count == 1,
                        "File failure published the cache or changed disk state.");
                    Directory.Delete(json + ".tmp");
                }

                ledger.ApplyBatch(first);
                ledger.MarkDelivered("delivery-a", "before", DateTimeOffset.UtcNow);
                ledger.MarkApplied("delivery-a", "consumed", DateTimeOffset.UtcNow);
                ledger.ApplyBatch(first);
                Require(ledger.ListAll().Count == 3 && ledger.Find("delivery-a")!.AppliedSnapshotId == "consumed",
                    "Replay duplicated deliveries or reset their lifecycle.");
                var contender = new LedgerBatch(new[] { Event("other-buyer") }, new[] { change });
                ExpectFailure(() => ledger.ApplyBatch(contender));
                Require(ledger.Find("other-buyer") is null, "Another buyer settled an already closed order.");
                if (ledger is FileAuthoritativeEventLedger) Require(new FileAuthoritativeEventLedger(json).ListAll().Count == 3, "File batch was not durable.");
                if (ledger is SqliteAuthoritativeEventLedger) Require(new SqliteAuthoritativeEventLedger(database).ListAll().Count == 3, "SQL batch was not durable.");

                ledger.Append(Event("race-order"));
                int accepted = 0;
                Parallel.For(0, 2, i =>
                {
                    try
                    {
                        ledger.ApplyBatch(new LedgerBatch(new[] { Event("race-delivery-" + i) },
                            new[] { new LedgerStatusChange("race-order", order.Status, ServerEventStatus.AppliedToSnapshot) }));
                        Interlocked.Increment(ref accepted);
                    }
                    catch (InvalidOperationException) { }
                });
                Require(accepted == 1, "Concurrent settlement must have exactly one winner.");
                VerifyMixedBatch(ledger, database, json);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyMixedBatch(IAuthoritativeEventLedger ledger, string database, string json)
    {
        var at = DateTimeOffset.UnixEpoch.AddDays(2);
        var status = Event("mixed-status") with { LastFailureReason = "preserve", NextRetryAtUtc = at };
        var applied = Event("mixed-applied") with { Status = ServerEventStatus.DeliveredToClient,
            DeliveredToSnapshotId = "existing-delivery", DeliveredAtUtc = at, LastFailureReason = "old", NextRetryAtUtc = at,
            TargetDecision = TargetEventDecision.Accepted, DecisionAtUtc = at, DecisionReason = "keep decision" };
        var confirmation = Event("mixed-confirmation");
        var rejected = Event("mixed-rejection") with { RejectionPolicy = EventRejectionPolicy.RejectableByTarget };
        foreach (var source in new[] { status, applied, confirmation, rejected }) ledger.Append(source);
        var added = Event("mixed-added");
        var payload = new ItemDeliveryEventPayload([new("gold", "Gold", 4, "changed-source")], "changed payload");
        var confirmed = confirmation with { Status = ServerEventStatus.Conflict, DeliveredToSnapshotId = "base",
            DeliveredAtUtc = at, LastApplicationResult = EventApplicationResultKind.NeedsManualReview,
            LastFailureReason = "review", NextRetryAtUtc = at.AddHours(1) };
        var batch = new LedgerBatch([added], [new(status.EventId, status.Status, ServerEventStatus.Cancelled, status.Payload, payload, status)],
            [new(applied, "ignored-delivery", "applied-snapshot", at.AddMinutes(1))],
            [new(confirmation, confirmed)], [new(rejected, at.AddMinutes(2), "declined")]);
        string before = Snapshot(ledger);
        if (ledger is SqliteAuthoritativeEventLedger)
        {
            Execute(database, "create trigger fail_mixed before update on events when new.event_id = 'mixed-confirmation' begin select raise(abort, 'mixed batch failure'); end;");
            ExpectFailure(() => ledger.ApplyBatch(batch));
            Require(before == Snapshot(ledger) && before == Snapshot(new SqliteAuthoritativeEventLedger(database)), "Mixed SQL failure committed earlier updates.");
            Execute(database, "drop trigger fail_mixed;");
        }
        if (ledger is FileAuthoritativeEventLedger)
        {
            Directory.CreateDirectory(json + ".tmp");
            ExpectFailure(() => ledger.ApplyBatch(batch));
            Require(before == Snapshot(ledger) && before == Snapshot(new FileAuthoritativeEventLedger(json)), "Mixed file failure published candidate state.");
            Directory.Delete(json + ".tmp");
        }
        ledger.ApplyBatch(batch);
        var expected = new[] { added, status with { Status = ServerEventStatus.Cancelled, Payload = payload },
            applied with { Status = ServerEventStatus.AppliedToSnapshot, AppliedSnapshotId = "applied-snapshot", AppliedAtUtc = at.AddMinutes(1),
                LastApplicationResult = EventApplicationResultKind.Applied, LastFailureReason = null, NextRetryAtUtc = null }, confirmed,
            rejected with { Status = ServerEventStatus.RejectedByTarget, TargetDecision = TargetEventDecision.Rejected,
                DecisionAtUtc = at.AddMinutes(2), DecisionReason = "declined", LastApplicationResult = EventApplicationResultKind.Rejected } };
        foreach (var item in expected)
            Require(JsonSerializer.Serialize(ledger.Find(item.EventId)) == JsonSerializer.Serialize(item)
                && JsonSerializer.Serialize(ledger.FindByIdempotencyKey(item.IdempotencyKey)) == JsonSerializer.Serialize(item),
                "Mixed batch changed fields or index differently: " + ledger.GetType().Name + "/" + item.EventId);
        ledger.MarkDelivered(added.EventId, "delivered", at.AddMinutes(3));
        ledger.MarkApplied(added.EventId, "consumed", at.AddMinutes(4));
        string committed = Snapshot(ledger);
        ledger.ApplyBatch(batch);
        Require(committed == Snapshot(ledger), "Mixed replay reset an existing event's lifecycle.");
        IAuthoritativeEventLedger reopened = ledger is FileAuthoritativeEventLedger ? new FileAuthoritativeEventLedger(json)
            : ledger is SqliteAuthoritativeEventLedger ? new SqliteAuthoritativeEventLedger(database) : ledger;
        Require(committed == Snapshot(reopened), "Mixed batch was not durable.");
    }

    private static string Snapshot(IAuthoritativeEventLedger ledger) => JsonSerializer.Serialize(ledger.ListAll().OrderBy(item => item.EventId, StringComparer.Ordinal));

    private static AuthoritativeEvent Event(string key) => AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery,
        new EventParty("sender", "colony"), new EventParty("recipient", "target"), key, false,
        new ItemDeliveryEventPayload(new[] { new EventThingReference("silver", "Silver", 1, "snapshot") }, null),
        DateTimeOffset.UnixEpoch) with { EventId = key };

    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidOperationException or SqliteException or IOException or UnauthorizedAccessException) { return; }
        throw new Exception("Expected batch rejection.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
