using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Data.Sqlite;

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
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

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
