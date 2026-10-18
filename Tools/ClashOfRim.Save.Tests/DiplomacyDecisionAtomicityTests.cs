using System.Text.Json;
using System.Globalization;
using System.Reflection;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using static DiplomacyTestSupport;

internal static class DiplomacyDecisionAtomicityTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clash-diplomacy-decision-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string kind in new[] { "AllianceRequest", "PeaceRequest", "WarDeclaration", "AllianceCancellation" })
            foreach (string phase in new[] { "insert", "update" })
            foreach (bool accepted in kind is "AllianceRequest" or "PeaceRequest" ? new[] { true, false } : new[] { true })
            foreach (bool restart in new[] { false, true })
                StorageFailure(factory, root, "sqlite", phase, kind, accepted, restart);
            if (OperatingSystem.IsWindows())
                foreach (string kind in new[] { "AllianceRequest", "PeaceRequest", "WarDeclaration", "AllianceCancellation" })
                foreach (string phase in new[] { "write", "replace" })
                foreach (bool accepted in kind is "AllianceRequest" or "PeaceRequest" ? new[] { true, false } : new[] { true })
                foreach (bool restart in new[] { false, true })
                    StorageFailure(factory, root, "file", phase, kind, accepted, restart);
            foreach (string backend in new[] { "memory", "file", "sqlite" })
            {
                foreach (string conflict in new[] { "actor", "target", "payload", "key" })
                foreach (bool accepted in new[] { true, false })
                    ReceiptConflict(factory, root, backend, conflict, accepted);
                foreach (bool accepted in new[] { true, false })
                    CommittedReplyLost(factory, root, backend, accepted);
                BatchSemantics(root, backend);
                BatchConflictAndReplay(root, backend);
                ConcurrentBatches(root, backend);
            }
            TerminalAndAutomaticRead(factory);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!string.Equals(Directory.GetParent(root)!.FullName, Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Temporary test path escaped its parent.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static IAuthoritativeEventLedger Open(string backend, string path) => backend switch
    {
        "file" => new FileAuthoritativeEventLedger(path),
        "sqlite" => new SqliteAuthoritativeEventLedger(path),
        _ => new InMemoryAuthoritativeEventLedger()
    };

    private static void StorageFailure(Func<string, SaveSnapshotPackage> factory, string root, string backend,
        string phase, string kind, bool accepted, bool restart)
    {
        string path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var ledger = Open(backend, path);
        var f = Fixture(factory, ledger); SetRelation(f, Initial(kind));
        string id = f.Diplomacy.Create(Request(f, kind)).EventId!;
        string token = f.Issue("userB", "colonyB");
        string before = Snapshot(ledger);
        string relation = JsonSerializer.Serialize(f.Relations.List());
        if (backend == "sqlite")
        {
            Sql(path, $"create trigger fail_decision before {phase} on events begin select raise(abort, 'decision failure'); end;");
            Fails<SqliteException>(() => f.Diplomacy.Respond(Response(id, token, accepted)));
            Sql(path, "drop trigger fail_decision;");
        }
        else
        {
            string blocked = path + (phase == "write" ? ".tmp" : "");
            if (!File.Exists(blocked)) File.WriteAllText(blocked, string.Empty);
            using var held = new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.Read);
            Fails<IOException>(() => f.Diplomacy.Respond(Response(id, token, accepted)), typeof(UnauthorizedAccessException));
        }
        Check(Snapshot(ledger) == before && Snapshot(Open(backend, path)) == before,
            "Decision/notification failure leaked a partial decision: " + phase);
        Check(JsonSerializer.Serialize(f.Relations.List()) == relation && f.Notifications.GetVersion("userA") == 0,
            "Failed decision published relation or wake-up.");
        if (restart) { f = Fixture(factory, Open(backend, path), f.Relations); token = f.Issue("userB", "colonyB"); }
        var result = f.Diplomacy.Respond(Response(id, token, accepted));
        Check(result.Result.Accepted && result.NotificationEventId is not null && f.Ledger.ListAll().Count == 2
            && f.Ledger.Find(id)!.TargetDecision == (accepted ? TargetEventDecision.Accepted : TargetEventDecision.Rejected),
            "Retry did not atomically publish the decision and notification.");
        Check(f.Notifications.GetVersion("userA") == 1 && Relation(f) == (accepted ? Final(kind) : Initial(kind)), "Retry did not publish effects.");
        string committed = Evidence(f);
        var reversed = f.Diplomacy.Respond(Response(id, token, !accepted));
        Check(reversed.Result.Accepted && committed == Evidence(f), "Opposite retry changed the committed decision.");
    }

    private static void ReceiptConflict(Func<string, SaveSnapshotPackage> factory, string root, string backend, string conflict, bool accepted)
    {
        string path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var f = Fixture(factory, Open(backend, path));
        string id = f.Diplomacy.Create(Request(f, "AllianceRequest")).EventId!;
        var source = f.Ledger.Find(id)!;
        var receipt = DiplomacyPolicy.CreateDiplomacyResponseNotification(source, accepted, false, DateTimeOffset.UtcNow);
        f.Ledger.Append(conflict switch
        {
            "actor" => receipt with { Actor = new("imposter") },
            "target" => receipt with { Target = new("wrong-recipient") },
            "payload" => receipt with { Payload = ((ServerNotificationEventPayload)receipt.Payload) with { RelatedAccepted = !accepted } },
            _ => receipt with { IdempotencyKey = "other-key" }
        });
        string before = Evidence(f);
        Fails<InvalidOperationException>(() => f.Diplomacy.Respond(Response(id, f.Issue("userB", "colonyB"), accepted)));
        Check(before == Evidence(f) && f.Notifications.GetVersion("userA") == 0,
            "Conflicting response changed the decision before validation: " + conflict);
        if (backend != "memory") Check(Snapshot(f.Ledger) == Snapshot(Open(backend, path)), "Conflict changed durable state.");
    }

    private static void CommittedReplyLost(Func<string, SaveSnapshotPackage> factory, string root, string backend, bool accepted)
    {
        string path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var ledger = Open(backend, path);
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, SnapshotSupportDeathTests.InterruptedLedger>();
        var faults = (SnapshotSupportDeathTests.InterruptedLedger)proxy; faults.Inner = ledger;
        var f = Fixture(factory, proxy);
        string id = f.Diplomacy.Create(Request(f, "AllianceRequest")).EventId!;
        string token = f.Issue("userB", "colonyB");
        faults.AfterBatch = () => throw new IOException("Simulated loss after durable decision batch.");
        Fails<IOException>(() => f.Diplomacy.Respond(Response(id, token, accepted)));
        faults.AfterBatch = null;
        Check(ledger.ListAll().Count == 2 && ledger.Find(id)!.TargetDecision == (accepted ? TargetEventDecision.Accepted : TargetEventDecision.Rejected),
            "Committed response loss did not retain both ledger records.");
        var receipt = ledger.ListByType(ServerEventType.ServerNotification).Single();
        // Only the ledger transaction is covered here; relation/wake-up recovery is M7e1b2.
        ledger.MarkAccepted(receipt.EventId, DateTimeOffset.UtcNow, "notification already read");
        string committed = Snapshot(ledger);
        f = Fixture(factory, backend == "memory" ? ledger : Open(backend, path)); token = f.Issue("userB", "colonyB");
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var result = f.Diplomacy.Respond(new(id, "userB", "colonyB", "target", !accepted, "different replay reason", token));
            Check(result.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && committed == Snapshot(f.Ledger),
                "Opposite replay after restart changed original decision, notice text, or notice consumption.");
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    private static void TerminalAndAutomaticRead(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (var status in new[] { ServerEventStatus.Cancelled, ServerEventStatus.Failed })
        foreach (bool accepted in new[] { true, false })
        {
            var f = Fixture(factory); string id = f.Diplomacy.Create(Request(f, "AllianceRequest")).EventId!;
            f.Ledger.ChangeStatus(id, status); string before = Evidence(f);
            var result = f.Diplomacy.Respond(Response(id, f.Issue("userB", "colonyB"), accepted));
            Check(!result.Result.Accepted && before == Evidence(f), "A closed invitation was resurrected.");
        }
        foreach (string kind in new[] { "WarDeclaration", "AllianceCancellation" })
        {
            var f = Fixture(factory); SetRelation(f, Initial(kind));
            string id = f.Diplomacy.Create(Request(f, kind)).EventId!;
            var source = f.Ledger.Find(id)!;
            f.Ledger.ApplyBatch(new LedgerBatch([], [], AcceptanceChanges: [new(source, DateTimeOffset.UtcNow, "read acknowledgment")]));
            string before = Evidence(f);
            var result = f.Diplomacy.Respond(Response(id, f.Issue("userB", "colonyB")));
            Check(result.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && result.NotificationEventId is null && before == Evidence(f),
                "Automatic notification read was reinterpreted as an explicit player response.");
        }
    }

    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(10);
    private static AuthoritativeEvent Source(ServerEventType type = ServerEventType.AllianceRequest) =>
        AuthoritativeEventFactory.Create(type, new("a", "colony-a"), new("b", "colony-b"), "source", false,
            DiplomacyPolicy.CreateDiplomacyPayload(type, "source", "original", null), At);

    private static LedgerBatch DecisionBatch(AuthoritativeEvent source, bool accepted) => new(
        [DiplomacyPolicy.CreateDiplomacyResponseNotification(source, accepted, false, At.AddHours(1))], [],
        DiplomacyDecisionChanges: [new(source, accepted, At.AddHours(1), "first decision")]);

    private static void BatchSemantics(string root, string backend)
    {
        foreach (var type in new[] { ServerEventType.AllianceRequest, ServerEventType.PeaceRequest,
            ServerEventType.WarDeclaration, ServerEventType.AllianceCancellation })
        foreach (var status in Enum.GetValues<ServerEventStatus>())
        foreach (bool accepted in new[] { true, false })
        {
            string path = Path.Combine(root, Guid.NewGuid().ToString("N"));
            var ledger = Open(backend, path); var source = Source(type) with { Status = status };
            ledger.Append(source); string before = Snapshot(ledger);
            bool allowed = status is ServerEventStatus.Recorded or ServerEventStatus.PendingOfflineDelivery
                or ServerEventStatus.ReadyForImmediateDelivery or ServerEventStatus.DeliveredToClient or ServerEventStatus.Conflict;
            allowed &= accepted || type is ServerEventType.AllianceRequest or ServerEventType.PeaceRequest;
            if (allowed)
            {
                ledger.ApplyBatch(DecisionBatch(source, accepted));
                var changed = ledger.Find(source.EventId)!;
                Check(changed.Status == (accepted ? ServerEventStatus.AppliedToSnapshot : ServerEventStatus.RejectedByTarget)
                    && changed.TargetDecision == (accepted ? TargetEventDecision.Accepted : TargetEventDecision.Rejected)
                    && changed.DecisionAtUtc == At.AddHours(1) && changed.DecisionReason == "first decision"
                    && changed.AppliedAtUtc == (accepted ? At.AddHours(1) : null) && changed.AppliedSnapshotId is null
                    && changed.LastApplicationResult == (accepted ? EventApplicationResultKind.Applied : EventApplicationResultKind.Rejected)
                    && changed.Actor == source.Actor && changed.Target == source.Target && ledger.ListAll().Count == 2,
                    "Explicit decision changed lifecycle metadata incorrectly.");
            }
            else
            {
                Fails<InvalidOperationException>(() => ledger.ApplyBatch(DecisionBatch(source, accepted)));
                Check(before == Snapshot(ledger), "Invalid decision changed source or created its receipt.");
            }
            if (backend != "memory") Check(Snapshot(ledger) == Snapshot(Open(backend, path)), "Decision batch differed after reopen.");
        }
        foreach (var type in Enum.GetValues<ServerEventType>().Except(new[] { ServerEventType.AllianceRequest,
            ServerEventType.PeaceRequest, ServerEventType.WarDeclaration, ServerEventType.AllianceCancellation }))
        {
            var ledger = Open(backend, Path.Combine(root, Guid.NewGuid().ToString("N")));
            var source = Source() with { Type = type };
            ledger.Append(source); string before = Snapshot(ledger);
            Fails<InvalidOperationException>(() => ledger.ApplyBatch(DecisionBatch(source, true)));
            Check(before == Snapshot(ledger), "A non-diplomacy event bypassed snapshot application via an explicit decision.");
        }
        foreach (var type in new[] { ServerEventType.AllianceRequest, ServerEventType.PeaceRequest })
        {
            var ledger = Open(backend, Path.Combine(root, Guid.NewGuid().ToString("N"))); var source = Source(type);
            ledger.Append(source); string before = Snapshot(ledger);
            Fails<InvalidOperationException>(() => ledger.ApplyBatch(new LedgerBatch([], [], AcceptanceChanges: [new(source, At, "automatic read")])));
            Check(before == Snapshot(ledger), "Ordinary event reading accepted an invitation.");
        }
    }

    private static void BatchConflictAndReplay(string root, string backend)
    {
        foreach (string scenario in new[] { "stale", "decision", "duplicate", "added-source", "mixed-status", "mixed-delivery", "mixed-rejection", "receipt-payload", "replay" })
        {
            string path = Path.Combine(root, Guid.NewGuid().ToString("N"));
            var ledger = Open(backend, path); var source = Source(); ledger.Append(source);
            var batch = DecisionBatch(source, true);
            switch (scenario)
            {
                case "stale": ledger.MarkDelivered(source.EventId, "base", At); break;
                case "decision":
                    // An inconsistent old record is not a new decision, even if its status is pending.
                    source = source with { TargetDecision = TargetEventDecision.Accepted };
                    batch = DecisionBatch(source, true); break;
                case "duplicate": batch = batch with { DiplomacyDecisionChanges = [batch.DiplomacyDecisionChanges![0], batch.DiplomacyDecisionChanges[0]] }; break;
                case "added-source": batch = batch with { Events = [batch.Events[0], source] }; break;
                case "mixed-status": batch = batch with { StatusChanges = [new(source.EventId, source.Status, ServerEventStatus.Cancelled)] }; break;
                case "mixed-delivery": batch = batch with { DeliveryChanges = [new(source, "base", At)] }; break;
                case "mixed-rejection": batch = batch with { RejectionChanges = [new(source, At, "other decision")] }; break;
                case "receipt-payload": ledger.Append(batch.Events[0] with { Payload = ((ServerNotificationEventPayload)batch.Events[0].Payload) with { RelatedEventId = "other-source" } }); break;
                case "replay":
                    ledger.ApplyBatch(batch);
                    ledger.MarkAccepted(batch.Events[0].EventId, At.AddHours(2), "notice read"); break;
            }
            string before = Snapshot(ledger);
            if (scenario == "replay") ledger.ApplyBatch(batch);
            else Fails<InvalidOperationException>(() => ledger.ApplyBatch(batch));
            Check(before == Snapshot(ledger), "Batch conflict/replay changed stored evidence: " + scenario);
            if (backend != "memory") Check(before == Snapshot(Open(backend, path)), "Batch conflict/replay changed durable evidence.");
        }
    }

    private static void ConcurrentBatches(string root, string backend)
    {
        string path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var ledger = Open(backend, path); var source = Source(); ledger.Append(source);
        int winners = 0;
        Parallel.For(0, 2, i =>
        {
            // Separate SQLite instances must arbitrate inside the database transaction.
            var current = backend == "sqlite" ? Open(backend, path) : ledger;
            try { current.ApplyBatch(DecisionBatch(source, i == 0)); Interlocked.Increment(ref winners); }
            catch (InvalidOperationException) { }
        });
        var decision = ledger.Find(source.EventId)!;
        var notice = ledger.ListByType(ServerEventType.ServerNotification).Single();
        Check(winners == 1 && ledger.ListAll().Count == 2
            && ((ServerNotificationEventPayload)notice.Payload).RelatedAccepted == (decision.TargetDecision == TargetEventDecision.Accepted),
            "Competing decisions published mismatched or duplicate receipts.");
    }

    private static string Snapshot(IAuthoritativeEventLedger ledger) => JsonSerializer.Serialize(ledger.ListAll());
    private static void Sql(string path, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + path);
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static void Fails<T>(Action action, params Type[] alternatives) where T : Exception
    {
        try { action(); } catch (Exception error) when (error is T || alternatives.Any(type => type.IsInstanceOfType(error))) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
