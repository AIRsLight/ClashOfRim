using AIRsLight.ClashOfRim.Network.Modules.Support;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class SnapshotSupportDeathTests
{
    private static readonly DateTimeOffset Accepted = DateTimeOffset.UnixEpoch.AddDays(3);

    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        foreach (bool reopen in new[] { false, true })
        {
            foreach (string fault in new[] { "insert-first", "insert-last", "status-first", "status-last" })
                VerifyRollback(fixture, reopen, fault);
            VerifyCommittedBeforeWake(fixture, reopen);
        }
        foreach (string kind in new[] { "memory", "file", "sqlite" }) VerifySemantics(fixture, kind);
        foreach (string conflict in new[] { "loss-owner", "loss-pawn", "source-status", "source-payload" })
            VerifyConflict(fixture, conflict);
    }

    private static void VerifyRollback(Func<string, SaveSnapshotPackage> fixture, bool reopen, string fault) => WithDirectory(root =>
    {
        string database = Path.Combine(root, "events.sqlite");
        IAuthoritativeEventLedger Open() => new SqliteAuthoritativeEventLedger(database);
        var ledger = Open();
        var sources = Seed(ledger);
        var state = new ClashOfRimNetworkState(ledger: ledger);
        bool insert = fault.StartsWith("insert", StringComparison.Ordinal);
        string id = (insert ? Loss(sources[fault.EndsWith("first", StringComparison.Ordinal) ? 0 : 2])
            : sources[fault.EndsWith("first", StringComparison.Ordinal) ? 0 : 2]).EventId;
        Sql(database, $"create trigger fail_death before {(insert ? "insert" : "update")} on events when new.event_id = '{id}' "
            + "begin select raise(abort, 'injected support death failure'); end;");
        Expect<SqliteException>(() => Process(state, Snapshot(fixture)), "SQL failure was not reached");
        foreach (var current in new[] { ledger, Open() })
            Require(current.ListAll().Count == sources.Length && sources.All(source => Same(current.Find(source.EventId)!, source)),
                "a failed stage left a partial loss/status batch");
        Require(state.EventNotifications.GetVersion("owner") == 0 && state.EventNotifications.GetVersion("ally") == 0,
            "failed batch woke an owner");
        Sql(database, "drop trigger fail_death;");
        if (reopen) state = new ClashOfRimNetworkState(ledger: Open());
        Process(state, Snapshot(fixture));
        VerifyCompleted(state, sources);
        VerifyCompleted(new ClashOfRimNetworkState(ledger: Open()), sources, checkWake: false);
    });

    private static void VerifyCommittedBeforeWake(Func<string, SaveSnapshotPackage> fixture, bool reopen) => WithDirectory(root =>
    {
        string database = Path.Combine(root, "events.sqlite");
        IAuthoritativeEventLedger Open() => new SqliteAuthoritativeEventLedger(database);
        var ledger = Open();
        var sources = Seed(ledger);
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, InterruptedLedger>();
        var interruption = (InterruptedLedger)proxy;
        interruption.Inner = ledger;
        interruption.AfterBatch = () => throw new IOException("interrupted after batch");
        var state = new ClashOfRimNetworkState(ledger: proxy);
        Expect<IOException>(() => Process(state, Snapshot(fixture)), "post-commit failure was not reached");
        VerifyCompleted(state, sources, checkWake: false);
        Require(state.EventNotifications.GetVersion("owner") == 0 && state.EventNotifications.GetVersion("ally") == 0,
            "test did not interrupt before notification");
        interruption.AfterBatch = null;
        // A consumer can advance a loss event before the original upload is replayed.
        var loss = ledger.FindByIdempotencyKey(Loss(sources[0]).IdempotencyKey)!;
        ledger.MarkDelivered(loss.EventId, "delivery", Accepted.AddMinutes(1));
        ledger.MarkApplied(loss.EventId, "applied", Accepted.AddMinutes(2));
        var before = ledger.ListAll().ToDictionary(item => item.EventId);
        if (reopen) state = new ClashOfRimNetworkState(ledger: Open());
        Process(state, Snapshot(fixture));
        VerifyCompleted(state, sources);
        Require(before.All(pair => Same(pair.Value, state.Ledger.Find(pair.Key)!)),
            "replay rewrote an event, timestamp, payload or consumed state");
    });

    private static void VerifySemantics(Func<string, SaveSnapshotPackage> fixture, string kind) => WithDirectory(root =>
    {
        IAuthoritativeEventLedger ledger = kind switch
        {
            "memory" => new InMemoryAuthoritativeEventLedger(),
            "file" => new FileAuthoritativeEventLedger(Path.Combine(root, "events.json")),
            _ => new SqliteAuthoritativeEventLedger(Path.Combine(root, "events.sqlite"))
        };
        var sources = Seed(ledger);
        string legacyKey = $"support-loss:{sources[0].EventId}:SnapshotDeath";
        var priorLoss = Loss(sources[0]) with { CreatedAtUtc = Accepted.AddMinutes(-1), IdempotencyKey = legacyKey,
            EventId = AuthoritativeEventFactory.BuildLegacyEventId(ServerEventType.SupportPawn, legacyKey) };
        // Legacy half-commit: the loss is present, but its source was never closed.
        ledger.Append(priorLoss);
        var ignored = new List<AuthoritativeEvent>();
        foreach (var status in new[] { ServerEventStatus.Failed, ServerEventStatus.Cancelled, ServerEventStatus.RejectedByTarget,
            ServerEventStatus.Conflict, ServerEventStatus.AppliedToSnapshot })
            ignored.Add(Support("ignored-" + status, "dead-0") with { Status = status });
        var payload = (SupportPawnEventPayload)sources[0].Payload;
        ignored.Add(Support("permanent", "dead-0") with { Payload = payload with { TemporaryControl = false } });
        ignored.Add(Support("return", "dead-0") with { Payload = payload with { ReturnToSender = true } });
        ignored.Add(Support("alive", "alive"));
        ignored.Add(Support("absent", "absent"));
        ignored.Add(Support("bad-key", "dead-0") with { Payload = payload with { PawnGlobalKey = "no-pawn-key" } });
        ignored.Add(Support("other-colony", "dead-0") with { Target = new EventParty("userA", "other") });
        ignored.Add(Support("other-user", "dead-0") with { Target = new EventParty("other", "colonyA") });
        foreach (var item in ignored) ledger.Append(item);
        var state = new ClashOfRimNetworkState(ledger: ledger);
        Process(state, SnapshotUploadResult.Reject(SnapshotUploadResultKind.IdentityMismatch, "rejected"));
        Require(sources.All(source => Same(source, ledger.Find(source.EventId)!)), "rejected upload changed support events");
        Process(state, Snapshot(fixture));
        VerifyCompleted(state, sources);
        Require(Same(priorLoss, ledger.Find(priorLoss.EventId)!), "legacy retry rewrote the existing loss");
        Require(ignored.All(item => Same(item, ledger.Find(item.EventId)!)) && ledger.ListAll().Count == ignored.Count + 6,
            "death processing changed excluded events or manufactured losses for unrelated failures");
        var before = ledger.ListAll().ToDictionary(item => item.EventId);
        Process(state, Snapshot(fixture));
        Require(before.All(pair => Same(pair.Value, ledger.Find(pair.Key)!)) && ledger.ListAll().Count == before.Count,
            "repeat processing is not idempotent");
        Require(state.EventNotifications.GetVersion("owner") == 2 && state.EventNotifications.GetVersion("ally") == 2,
            "replay omitted owner wake-up or did not deduplicate owners");
    });

    private static void VerifyConflict(Func<string, SaveSnapshotPackage> fixture, string conflict) => WithDirectory(root =>
    {
        var ledger = new SqliteAuthoritativeEventLedger(Path.Combine(root, "events.sqlite"));
        var sources = Seed(ledger);
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, InterruptedLedger>();
        var interruption = (InterruptedLedger)proxy;
        interruption.Inner = ledger;
        if (conflict.StartsWith("loss", StringComparison.Ordinal))
        {
            var loss = Loss(sources[0]);
            ledger.Append(conflict == "loss-owner" ? loss with { Target = new EventParty("wrong") }
                : loss with { Payload = ((SupportPawnEventPayload)loss.Payload) with { PawnGlobalKey = "wrong" } });
        }
        else interruption.BeforeBatch = () =>
        {
            if (conflict == "source-status") ledger.ChangeStatus(sources[0].EventId, ServerEventStatus.Cancelled);
            else ledger.ApplyBatch(new LedgerBatch([], [new LedgerStatusChange(sources[0].EventId, sources[0].Status, sources[0].Status,
                sources[0].Payload, ((SupportPawnEventPayload)sources[0].Payload) with { PawnGlobalKey = "changed" })]));
        };
        var state = new ClashOfRimNetworkState(ledger: proxy);
        Expect<InvalidOperationException>(() => Process(state, Snapshot(fixture)), "conflicting event was not rejected");
        Require(ledger.ListAll().Count == (conflict.StartsWith("loss", StringComparison.Ordinal) ? 4 : 3)
            && sources.Skip(1).All(source => Same(source, ledger.Find(source.EventId)!)), "conflict partially changed the batch");
        Require(state.EventNotifications.GetVersion("owner") == 0 && state.EventNotifications.GetVersion("ally") == 0,
            "conflicting batch woke owners");
        if (conflict == "source-status") Require(ledger.Find(sources[0].EventId)!.Status == ServerEventStatus.Cancelled, "overwrote concurrent cancellation");
        if (conflict == "source-payload") Require(((SupportPawnEventPayload)ledger.Find(sources[0].EventId)!.Payload).PawnGlobalKey == "changed",
            "overwrote concurrent source update");
    });

    private static AuthoritativeEvent[] Seed(IAuthoritativeEventLedger ledger)
    {
        var sources = new[] { Support("source-0", "dead-0"),
            Support("source-1", "dead-1") with { Status = ServerEventStatus.DeliveredToClient },
            Support("source-2", "dead-2") with { Actor = new EventParty("ally", "ally-colony"), Status = ServerEventStatus.ReadyForImmediateDelivery } };
        foreach (var item in sources) ledger.Append(item);
        return sources;
    }

    private static AuthoritativeEvent Support(string id, string pawn) => AuthoritativeEventFactory.Create(ServerEventType.SupportPawn,
        new EventParty("owner", "owner-colony"), new EventParty("userA", "colonyA"), id, false,
        new SupportPawnEventPayload("owner:owner/colony:owner-colony/pawn:" + pawn, "support-snapshot", "Original name", true, null),
        Accepted.AddHours(-1)) with { EventId = id };

    private static AuthoritativeEvent Loss(AuthoritativeEvent source) => SupportReturns.CreateSupportPawnLossReturnEvent(
        source, (SupportPawnEventPayload)source.Payload, "Earlier name", "SnapshotDeath", false, Accepted);

    private static SnapshotUploadResult Snapshot(Func<string, SaveSnapshotPackage> fixture)
    {
        var package = fixture("death-snapshot");
        var pawns = Enumerable.Range(0, 3).Select(index => new PawnSummary(index == 2 ? "remapped" : "dead-" + index,
            "snapshot:death/pawn:dead-" + index, null, null, "Human", null, "Current name", true, null, null)).ToList();
        pawns.Add(new PawnSummary("alive", "pawn:alive", null, null, "Human", null, "Alive", false, null, null));
        return SnapshotUploadResult.Accept(new LatestSnapshotRecord(package.Envelope.Identity, package.Envelope,
            package.Index with { Pawns = pawns }, Accepted));
    }

    private static void VerifyCompleted(ClashOfRimNetworkState state, AuthoritativeEvent[] sources, bool checkWake = true)
    {
        foreach (var source in sources)
        {
            Require(state.Ledger.Find(source.EventId)!.Status == ServerEventStatus.Failed, "source was not closed");
            var loss = state.Ledger.FindByIdempotencyKey(Loss(source).IdempotencyKey)
                ?? state.Ledger.FindByIdempotencyKey($"support-loss:{source.EventId}:SnapshotDeath");
            Require(loss is { Type: ServerEventType.SupportPawn } && loss.Actor == source.Target && loss.Target == source.Actor
                && loss.Payload is SupportPawnEventPayload { ReturnToSender: true, TemporaryControl: false, PawnPackage: null,
                    ReturnReason: "SnapshotDeath" } payload && payload.SourceEventId == source.EventId,
                "loss is absent or has incorrect ownership/semantics");
        }
        if (checkWake) Require(state.EventNotifications.GetVersion("owner") == 1 && state.EventNotifications.GetVersion("ally") == 1,
            "owners were not each notified once");
    }

    private static void Process(ClashOfRimNetworkState state, SnapshotUploadResult upload) => state.SupportDeaths.Process(upload);
    private static bool Same(AuthoritativeEvent left, AuthoritativeEvent right) =>
        JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right)
        && JsonSerializer.Serialize(left.Payload, left.Payload.GetType()) == JsonSerializer.Serialize(right.Payload, right.Payload.GetType());
    private static void Expect<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception(message); }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception("Snapshot support death: " + message); }
    private static void Sql(string database, string sql)
    { using var connection = new SqliteConnection("Data Source=" + database); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static void WithDirectory(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-snapshot-death-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    public class InterruptedLedger : DispatchProxy
    {
        public IAuthoritativeEventLedger Inner = null!;
        public Action? BeforeBatch, AfterBatch;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            bool batch = targetMethod!.Name == nameof(IAuthoritativeEventLedger.ApplyBatch);
            if (batch) BeforeBatch?.Invoke();
            object? result;
            try { result = targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
            if (batch) AfterBatch?.Invoke();
            return result;
        }
    }
}
