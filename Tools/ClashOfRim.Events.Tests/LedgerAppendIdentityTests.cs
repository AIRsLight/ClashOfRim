using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Data.Sqlite;

internal static class LedgerAppendIdentityTests
{
    private static readonly DateTimeOffset Created = DateTimeOffset.UnixEpoch.AddDays(1);

    public static void Run()
    {
        foreach (string storage in new[] { "memory", "file", "sqlite" })
        {
            IdentityConflicts(storage);
            ReplayPreservesLifecycle(storage);
            ConcurrentIdentityClaims(storage);
            OptionalPartyIdentity(storage);
        }
        SqlRollback();
        if (OperatingSystem.IsWindows())
            foreach (string failure in new[] { "write", "replace" }) FileRollback(failure);
    }

    private static void IdentityConflicts(string storage) => WithDirectory(root =>
    {
        var ledger = Open(storage, root); var source = Event(); ledger.Append(source);
        var candidates = new[]
        {
            source with { EventId = "changed-id" },
            source with { Type = ServerEventType.SupportPawn },
            source with { Actor = source.Actor with { UserId = "other" } },
            source with { Actor = source.Actor with { ColonyId = "other" } },
            source with { Actor = source.Actor with { FactionId = "other" } },
            source with { Target = source.Target with { UserId = "other" } },
            source with { Target = source.Target with { ColonyId = "other" } },
            source with { Target = source.Target with { FactionId = "other" } },
            source with { IdempotencyKey = "other-key" }
        };
        foreach (var candidate in candidates)
        {
            Expect<InvalidOperationException>(() => ledger.Append(candidate), "append accepted a different identity");
            Expect<InvalidOperationException>(() => ledger.ApplyBatch(new LedgerBatch([candidate], [])), "batch accepted a different identity");
            Require(ledger.ListAll().Count == 1 && Same(source, ledger.Find(source.EventId)!)
                && Same(source, ledger.FindByIdempotencyKey(source.IdempotencyKey)!), "conflicting append changed an index");
            if (storage != "memory")
            {
                var reopened = Open(storage, root);
                Require(reopened.ListAll().Count == 1 && Same(source, reopened.Find(source.EventId)!), "conflict changed persisted event");
            }
        }
        // Catch the old normalization collision without treating a different key as a replay.
        var punctuation = Event("a:b"); var other = Event("a-b");
        Require(punctuation.EventId == other.EventId, "fixture no longer exercises the legacy ID collision");
        ledger.Append(punctuation);
        Expect<InvalidOperationException>(() => ledger.Append(other), "different keys sharing a legacy ID were conflated");
        Require(ledger.FindByIdempotencyKey("a-b") is null, "colliding key acquired an alias");
    });

    private static void ReplayPreservesLifecycle(string storage) => WithDirectory(root =>
    {
        var ledger = Open(storage, root); var source = Event(); ledger.Append(source);
        ledger.MarkDelivered(source.EventId, "delivery", Created.AddMinutes(1));
        ledger.MarkApplied(source.EventId, "applied", Created.AddMinutes(2));
        var current = ledger.Find(source.EventId)!;
        var evolvedPayload = ((ItemDeliveryEventPayload)current.Payload) with { Message = "persisted update" };
        ledger.ApplyBatch(new LedgerBatch([], [new(source.EventId, current.Status, current.Status, current.Payload, evolvedPayload)]));
        var evolved = ledger.Find(source.EventId)!;
        if (storage != "memory") ledger = Open(storage, root);
        var result = ledger.Append(source with { CreatedAtUtc = Created.AddDays(1), DeliveryMode = ServerEventDeliveryMode.OnlineImmediate });
        Require(!result.Created && Same(evolved, result.Event) && Same(evolved, ledger.Find(source.EventId)!),
            "identity replay reset the persisted payload or consumption state");
        // Batch recovery intentionally remains stricter than ordinary append about the prepared payload.
        Expect<InvalidOperationException>(() => ledger.ApplyBatch(new LedgerBatch([source], [])), "batch replay lost payload validation");
    });

    private static void ConcurrentIdentityClaims(string storage) => WithDirectory(root =>
    {
        var ledger = Open(storage, root); var source = Event();
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 12).Select(index => Task.Run(() =>
        {
            start.Wait();
            var item = source with { Actor = source.Actor with { UserId = index % 2 == 0 ? "one" : "two" } };
            try { var result = ledger.Append(item); return (Created: result.Created, Rejected: false, Owner: result.Event.Actor.UserId); }
            catch (InvalidOperationException) { return (Created: false, Rejected: true, Owner: item.Actor.UserId); }
        })).ToArray();
        start.Set(); Task.WaitAll(tasks);
        var winner = ledger.FindByIdempotencyKey(source.IdempotencyKey)!;
        Require(tasks.Count(task => task.Result.Created) == 1 && tasks.Count(task => task.Result.Rejected) == 6
            && tasks.Where(task => !task.Result.Rejected).All(task => task.Result.Owner == winner.Actor.UserId)
            && ledger.ListAll().Count == 1, "concurrent claims returned another party's event as a duplicate");
    });

    private static void OptionalPartyIdentity(string storage) => WithDirectory(root =>
    {
        var ledger = Open(storage, root);
        var source = Event() with { Actor = new("sender", "", "\t"), Target = new("receiver", null, " ") };
        ledger.Append(source);
        if (storage != "memory") ledger = Open(storage, root);
        var stored = ledger.Find(source.EventId)!;
        var normalized = source with { Actor = new("sender"), Target = new("receiver") };
        Require(!ledger.Append(normalized).Created && Same(stored, ledger.Find(source.EventId)!), "optional SQL identity normalization broke replay");
        ledger.ApplyBatch(new LedgerBatch([normalized], []));
        Require(Same(stored, ledger.Find(source.EventId)!), "normalized batch replay rewrote original party fields");
        Expect<InvalidOperationException>(() => ledger.Append(normalized with { Target = new("receiver", "actual-colony") }),
            "missing colony was treated as a wildcard identity");
    });

    private static void FileRollback(string failure) => WithDirectory(root =>
    {
        string path = Path.Combine(root, "events.json"); var ledger = new FileAuthoritativeEventLedger(path);
        var prior = Event("prior"); var pending = Event("pending"); ledger.Append(prior);
        string blocked = failure == "replace" ? path : path + ".tmp";
        if (!File.Exists(blocked)) File.WriteAllText(blocked, string.Empty);
        using (var held = new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool failed = false;
            try { ledger.Append(pending); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            Require(failed, "file append failure not reached");
            foreach (var current in new[] { ledger, new FileAuthoritativeEventLedger(path) })
                Require(current.ListAll().Count == 1 && Same(prior, current.Find(prior.EventId)!)
                    && current.Find(pending.EventId) is null && current.FindByIdempotencyKey(pending.IdempotencyKey) is null,
                    "failed file append published a phantom event");
        }
        Require(ledger.Append(pending).Created, "failed append poisoned retry as a duplicate");
        var reopened = new FileAuthoritativeEventLedger(path);
        Require(reopened.ListAll().Count == 2 && Same(pending, reopened.FindByIdempotencyKey(pending.IdempotencyKey)!), "retry was not durable");
    });

    private static void SqlRollback() => WithDirectory(root =>
    {
        string database = Path.Combine(root, "events.sqlite"); var ledger = new SqliteAuthoritativeEventLedger(database);
        var source = Event();
        Sql(database, "create trigger fail_append before insert on events begin select raise(abort, 'append'); end;");
        Expect<SqliteException>(() => ledger.Append(source), "SQL append failure not reached");
        Require(ledger.FindByIdempotencyKey(source.IdempotencyKey) is null && new SqliteAuthoritativeEventLedger(database).ListAll().Count == 0,
            "failed SQL append left an event");
        Sql(database, "drop trigger fail_append;");
        Require(ledger.Append(source).Created && !ledger.Append(source).Created, "SQL retry is not idempotent");
    });

    private static AuthoritativeEvent Event(string key = "append-identity") => AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery,
        new("sender", "home", "Player"), new("receiver", "colony", "Player"), key, false,
        new ItemDeliveryEventPayload([], "original"), Created);
    private static IAuthoritativeEventLedger Open(string storage, string root) => storage switch
    {
        "file" => new FileAuthoritativeEventLedger(Path.Combine(root, "events.json")),
        "sqlite" => new SqliteAuthoritativeEventLedger(Path.Combine(root, "events.sqlite")),
        _ => new InMemoryAuthoritativeEventLedger()
    };
    private static bool Same(AuthoritativeEvent left, AuthoritativeEvent right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right)
        && JsonSerializer.Serialize(left.Payload, left.Payload.GetType()) == JsonSerializer.Serialize(right.Payload, right.Payload.GetType());
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Expect<T>(Action action, string message) where T : Exception { try { action(); } catch (T) { return; } throw new Exception(message); }
    private static void Sql(string database, string sql)
    { using var connection = new SqliteConnection("Data Source=" + database); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static void WithDirectory(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-append-identity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); } finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
