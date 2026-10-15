using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

internal static class FileLedgerPublicationTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(2);

    public static void Run()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows sharing modes inject real write/replace failures.
        foreach (string failure in new[] { "write", "replace" })
        foreach (string operation in new[] { "status", "delivered", "applied", "accepted", "rejected", "result", "delete" })
            VerifyRollback(operation, failure);
    }

    private static void VerifyRollback(string operation, string failure)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-file-ledger-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "events.json");
            var ledger = new FileAuthoritativeEventLedger(path);
            var source = Event("source", new("owner", "home"), new("receiver", "away"));
            ledger.Append(source);
            ledger.Append(Event("received", new("other", "elsewhere"), source.Actor));
            ledger.Append(Event("self", source.Actor, source.Actor));
            ledger.Append(Event("unrelated", new("owner", "other-home"), new("receiver", "other-away")));
            ledger.MarkDelivered(source.EventId, "base", At);
            var original = ledger.ListAll();
            string before = Snapshot(ledger);
            var expected = new InMemoryAuthoritativeEventLedger();
            foreach (var item in original) expected.Append(item);
            int expectedResult = Mutate(expected, operation, source.EventId);
            string after = Snapshot(expected);
            Require(before != after, "fixture did not exercise a state change");

            string blocked = failure == "write" ? path + ".tmp" : path;
            if (!File.Exists(blocked)) File.WriteAllText(blocked, string.Empty);
            using (var held = new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // Repeating a failed mutation in the same instance must still reach storage.
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    bool failed = false;
                    try { Mutate(ledger, operation, source.EventId); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
                    Require(failed, $"{operation}/{failure}: fault was not reached on attempt {attempt}");
                    foreach (var current in new[] { ledger, new FileAuthoritativeEventLedger(path) })
                    {
                        Require(Snapshot(current) == before, $"{operation}/{failure}: failed persistence published changed records");
                        foreach (var item in original)
                            Require(JsonSerializer.Serialize(current.FindByIdempotencyKey(item.IdempotencyKey)) == JsonSerializer.Serialize(item),
                                $"{operation}/{failure}: idempotency index diverged");
                    }
                }
                Require(ledger.DeleteForColony("missing", "missing") == 0, "empty deletion unnecessarily writes the file");
            }

            Require(Mutate(ledger, operation, source.EventId) == expectedResult, "failed deletion poisoned the retry count");
            foreach (var current in new[] { ledger, new FileAuthoritativeEventLedger(path) })
            {
                Require(Snapshot(current) == after, $"{operation}/{failure}: successful retry was not durable");
                foreach (var item in original)
                    Require(JsonSerializer.Serialize(current.FindByIdempotencyKey(item.IdempotencyKey)) ==
                        JsonSerializer.Serialize(expected.FindByIdempotencyKey(item.IdempotencyKey)), "successful retry left a stale index");
                Require(JsonSerializer.Serialize(current.ListQueueForTarget("receiver")) == JsonSerializer.Serialize(expected.ListQueueForTarget("receiver")),
                    "queue projection diverged after retry");
            }
            if (operation == "delete")
                Require(expectedResult == 3 && ledger.ListAll().Single().IdempotencyKey == "unrelated"
                    && ledger.DeleteForColony("owner", "home") == 0, "deletion crossed colony boundaries or counted self twice");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static int Mutate(IAuthoritativeEventLedger ledger, string operation, string id)
    {
        switch (operation)
        {
            case "status": ledger.ChangeStatus(id, ServerEventStatus.Failed); break;
            case "delivered": ledger.MarkDelivered(id, "new-base", At.AddHours(1)); break;
            case "applied": ledger.MarkApplied(id, "applied", At.AddHours(2)); break;
            case "accepted": ledger.MarkAccepted(id, At.AddHours(3), "accepted"); break;
            case "rejected": ledger.MarkRejected(id, At.AddHours(4), "rejected"); break;
            case "result": ledger.ReportApplicationResult(id, EventApplicationResultKind.SnapshotBaseMismatch, "mismatch", At.AddHours(5)); break;
            case "delete": return ledger.DeleteForColony("owner", "home");
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
        return 0;
    }

    private static AuthoritativeEvent Event(string key, EventParty actor, EventParty target) =>
        AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, actor, target, key, false,
            new ItemDeliveryEventPayload([], key), At);
    private static string Snapshot(IAuthoritativeEventLedger ledger) => JsonSerializer.Serialize(ledger.ListAll());
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
