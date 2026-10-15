using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Data.Sqlite;

internal static class GiftRejectionAtomicityTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(3);

    public static void Run()
    {
        foreach (string phase in new[] { "insert", "update" })
        foreach (bool reopen in new[] { false, true }) SqlFailure(phase, reopen);
        foreach (string storage in new[] { "memory", "file", "sqlite" })
        {
            ReplayAndLegacyRepair(storage);
            ValidationRollback(storage);
            ConcurrentRejections(storage);
        }
        if (OperatingSystem.IsWindows())
            foreach (string phase in new[] { "write", "replace" }) FileFailure(phase);
    }

    private static void SqlFailure(string phase, bool reopen) => WithRoot(root =>
    {
        string path = Path.Combine(root, "events.sqlite");
        IAuthoritativeEventLedger ledger = Open("sqlite", root);
        var source = Gift(); ledger.Append(source);
        string original = Snapshot(ledger);
        Sql(path, $"create trigger fail_reject before {phase} on events begin select raise(abort, 'rejection failure'); end;");
        Fails<SqliteException>(() => Reject(ledger, source));
        Require(Snapshot(ledger) == original && Snapshot(Open("sqlite", root)) == original,
            "failed return/rejection write partially committed the gift closure");
        Sql(path, "drop trigger fail_reject;");
        if (reopen) ledger = Open("sqlite", root);
        var result = Reject(ledger, source);
        Require(result.ReturnEventCreated && ledger.ListAll().Count == 2
            && ledger.Find(source.EventId)!.Status == ServerEventStatus.RejectedByTarget, "SQL rejection retry did not complete");
    });

    private static void FileFailure(string phase) => WithRoot(root =>
    {
        var ledger = Open("file", root); var source = Gift(); ledger.Append(source);
        string original = Snapshot(ledger);
        string blocked = Path.Combine(root, "events.json") + (phase == "write" ? ".tmp" : "");
        if (!File.Exists(blocked)) File.WriteAllText(blocked, string.Empty);
        using (var held = new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool failed = false;
            try { Reject(ledger, source); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            Require(failed && Snapshot(ledger) == original && Snapshot(Open("file", root)) == original, "file rejection leaked a partial closure");
        }
        Require(Reject(ledger, source).ReturnEventCreated && Open("file", root).ListAll().Count == 2, "file rejection retry failed");
    });

    private static void ReplayAndLegacyRepair(string storage) => WithRoot(root =>
    {
        var ledger = Open(storage, root); var source = Gift(); ledger.Append(source);
        // Old versions could leave the rejected source without its return.
        ledger.MarkRejected(source.EventId, At, "first reason");
        if (storage != "memory") ledger = Open(storage, root);
        var repaired = ledger.RejectGiftAndCreateReturn(source.EventId, At.AddHours(1), "new reason", true);
        Require(repaired.ReturnEventCreated && repaired.RejectedGift.DecisionAtUtc == At
            && repaired.RejectedGift.DecisionReason == "first reason" && repaired.ReturnEvent.CreatedAtUtc == At,
            "legacy repair replaced the first rejection decision");
        ledger.MarkDelivered(repaired.ReturnEvent.EventId, "base", At);
        ledger.MarkApplied(repaired.ReturnEvent.EventId, "home", At);
        string original = Snapshot(ledger);
        if (storage != "memory") ledger = Open(storage, root);
        var replay = ledger.RejectGiftAndCreateReturn(source.EventId, At.AddDays(1), "retry", false);
        Require(!replay.ReturnEventCreated && replay.ReturnEvent.Status == ServerEventStatus.AppliedToSnapshot
            && replay.ReturnEvent.AppliedSnapshotId == "home" && Snapshot(ledger) == original, "rejection replay reset decision or return consumption");
    });

    private static void ValidationRollback(string storage)
    {
        foreach (string scenario in new[] { "payload", "actor", "not-gift", "applied", "cancelled", "failed", "bad-decision" })
        WithRoot(root =>
        {
            var ledger = Open(storage, root); var source = Gift();
            source = scenario switch
            {
                "not-gift" => source with { Payload = new ItemDeliveryEventPayload([], null, Purpose: ItemDeliveryPurpose.TradeCancelledOwnerReturn) },
                "applied" => source with { Status = ServerEventStatus.AppliedToSnapshot },
                "cancelled" => source with { Status = ServerEventStatus.Cancelled },
                "failed" => source with { Status = ServerEventStatus.Failed },
                "bad-decision" => source with { Status = ServerEventStatus.RejectedByTarget },
                _ => source
            };
            ledger.Append(source);
            if (scenario is "payload" or "actor")
            {
                var other = GiftReturnEventFactory.CreateReturnEvent(source, At, false);
                ledger.Append(scenario == "actor" ? other with { Actor = new("imposter") } : other with
                { Payload = ((ItemDeliveryEventPayload)other.Payload) with { Items = [] } });
            }
            string original = Snapshot(ledger);
            Fails<InvalidOperationException>(() => Reject(ledger, source));
            Require(Snapshot(ledger) == original, scenario + " rejection changed state before validation");
            if (storage != "memory") Require(Snapshot(Open(storage, root)) == original, "validation changed durable state");
        });
    }

    private static void ConcurrentRejections(string storage) => WithRoot(root =>
    {
        var ledger = Open(storage, root); var source = Gift(); ledger.Append(source);
        var results = new System.Collections.Concurrent.ConcurrentBag<GiftReturnResult>();
        Parallel.For(0, 8, index =>
        {
            // Independent SQL instances must coordinate through the database transaction.
            var current = storage == "sqlite" ? Open(storage, root) : ledger;
            results.Add(current.RejectGiftAndCreateReturn(source.EventId, At.AddSeconds(index), "reason-" + index, false));
        });
        var winner = ledger.Find(source.EventId)!;
        Require(results.Count(r => r.ReturnEventCreated) == 1 && ledger.ListAll().Count == 2
            && results.All(r => JsonSerializer.Serialize(r.RejectedGift) == JsonSerializer.Serialize(winner)),
            "concurrent rejection duplicated the return or rewrote the first decision");
    });

    private static AuthoritativeEvent Gift() => AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery,
        new("owner", "home"), new("receiver", "away"), "gift", false,
        new ItemDeliveryEventPayload([new("steel", "Steel", 10, "source")], "gift"), At);
    private static GiftReturnResult Reject(IAuthoritativeEventLedger ledger, AuthoritativeEvent source) =>
        ledger.RejectGiftAndCreateReturn(source.EventId, At, "declined", false);
    private static IAuthoritativeEventLedger Open(string storage, string root) => storage switch
    {
        "sqlite" => new SqliteAuthoritativeEventLedger(Path.Combine(root, "events.sqlite")),
        "file" => new FileAuthoritativeEventLedger(Path.Combine(root, "events.json")),
        _ => new InMemoryAuthoritativeEventLedger()
    };
    private static string Snapshot(IAuthoritativeEventLedger ledger) => JsonSerializer.Serialize(ledger.ListAll().OrderBy(e => e.EventId, StringComparer.Ordinal));
    private static void Sql(string path, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + path); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static void Fails<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void WithRoot(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-gift-rejection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
