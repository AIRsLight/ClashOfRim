using AIRsLight.ClashOfRim.Network.Modules.Support;
using System.Reflection;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using static SupportClosureTestData;
using static EventLedgerTestEvidence;
using static SnapshotTestAssertions;

internal static class SupportClosureTests
{
    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (string kind in Kinds)
        {
            foreach (bool reopen in new[] { false, true })
            {
                foreach (string failure in new[] { "insert", "update" }) Rollback(kind, failure, reopen);
                InterruptedWake(kind, reopen);
            }
            if (OperatingSystem.IsWindows()) FileRollback(kind);
            foreach (string storage in new[] { "memory", "file", "sqlite" }) Legacy(kind, storage);
            StaleSource(kind);
        }
        Guards();
        AuthenticationGuards();
        foreach (string kind in Kinds)
        foreach (bool replay in new[] { false, true })
        foreach (bool replaced in new[] { false, true })
        {
            var state = new ClashOfRimNetworkState(); state.Ledger.Append(Source());
            var client = SupportClosureTestClient.Create(state);
            string token = client.Token("userA");
            if (replay) Check(client.Call(kind).Accepted, "initial closure for queued retry failed");
            SnapshotMutationAuthorizationProbe.RevokedWhileWaiting(state, "userA", "colonyA", replaced,
                () => client.Call(kind, token: token, authenticate: false).Accepted);
        }
        ConflictingReturns();
        ConcurrentFinish();
        OtherClosureProducers(fixture);
        foreach (string storage in new[] { "memory", "file", "sqlite" })
        foreach (string kind in Kinds)
        foreach (bool legacyChild in new[] { false, true }) NestedSourceIdentities(storage, kind, legacyChild);
        KeyEncodingAndMigration();
        LegacyLossCannotSuppressOtherSource();
    }

    private static void NestedSourceIdentities(string storage, string kind, bool legacyChild) => SqliteTestDirectory.With("cor-support-closure-", root =>
    {
        IAuthoritativeEventLedger Open() => storage switch
        {
            "file" => new FileAuthoritativeEventLedger(Path.Combine(root, "events.json")),
            "sqlite" => new SqliteAuthoritativeEventLedger(Path.Combine(root, "events.sqlite")),
            _ => new InMemoryAuthoritativeEventLedger()
        };
        var ledger = Open();
        var parent = Source();
        var child = Source() with { EventId = "source:child", IdempotencyKey = "source:child" };
        ledger.Append(parent); ledger.Append(child);
        if (legacyChild)
        {
            var legacy = AsLegacy(Closure(child, kind, "manual:finished"));
            ledger.Append(legacy);
            ledger.MarkDelivered(legacy.EventId, "owner-base", OriginalTime);
            ledger.MarkApplied(legacy.EventId, "owner-consumed", OriginalTime);
        }
        var state = new ClashOfRimNetworkState(ledger: ledger);
        var client = SupportClosureTestClient.Create(state);
        var childResult = client.Call(kind, reason: "manual:finished", eventId: child.EventId);
        Check(childResult.Accepted && childResult.Created != legacyChild, "nested source could not finish");
        var childClosure = ledger.Find(childResult.ReturnId!)!;
        var endedChild = ledger.Find(child.EventId)!;
        if (storage != "memory") { state = new ClashOfRimNetworkState(ledger: Open()); client = SupportClosureTestClient.Create(state); }
        // These tuples produced exactly the same legacy finish/loss key. Both must now succeed.
        var parentResult = client.Call(kind, reason: "child:manual:finished");
        Check(parentResult.Accepted && parentResult.Created && parentResult.ReturnId != childResult.ReturnId,
            "a nested source closure was treated as the parent closure");
        Check(Same(childClosure, state.Ledger.Find(childClosure.EventId)!) && Same(endedChild, state.Ledger.Find(child.EventId)!),
            "ending the parent modified the nested source");
        foreach (var pair in new[] { (parent.EventId, parentResult.ReturnId), (child.EventId, childResult.ReturnId) })
        {
            var retry = client.Call(kind, reason: "different:retry", eventId: pair.EventId);
            Check(retry.Accepted && !retry.Created && retry.ReturnId == pair.ReturnId, "nested identity replay chose another closure");
        }
        Check(state.Ledger.ListAll().Count == 4, "nested identity created extra closures");
    });

    private static void Rollback(string kind, string failure, bool reopen) => SqliteTestDirectory.With("cor-support-closure-", root =>
    {
        string database = Path.Combine(root, "events.sqlite");
        IAuthoritativeEventLedger Open() => new SqliteAuthoritativeEventLedger(database);
        var ledger = Open(); ledger.Append(Source());
        var source = ledger.Find("source")!;
        var state = new ClashOfRimNetworkState(ledger: ledger);
        var client = SupportClosureTestClient.Create(state);
        string condition = failure == "insert" ? "new.event_id != 'source'" : "new.event_id = 'source'";
        SqliteTestDatabase.Execute(database, $"create trigger fail_closure before {failure} on events when {condition} begin select raise(abort, 'injected support failure'); end;");
        Expect<SqliteException>(() => client.Call(kind), "SQL fault not reached: " + kind);
        foreach (var current in new[] { ledger, Open() })
            Check(current.ListAll().Count == 1 && Same(source, current.Find("source")!), "support batch partially committed");
        Check(state.EventNotifications.GetVersion("owner") == 0, "failed batch woke owner");
        SqliteTestDatabase.Execute(database, "drop trigger fail_closure;");
        if (reopen) { state = new ClashOfRimNetworkState(ledger: Open()); client = SupportClosureTestClient.Create(state); }
        var response = client.Call(kind);
        Check(response.Accepted && response.Created, "retry did not create closure");
        SupportClosureTestAssertions.Closed(state.Ledger.ListAll(), kind);
        SupportClosureTestAssertions.Closed(Open().ListAll(), kind);
    });

    private static void InterruptedWake(string kind, bool reopen) => SqliteTestDirectory.With("cor-support-closure-", root =>
    {
        string database = Path.Combine(root, "events.sqlite");
        var ledger = new SqliteAuthoritativeEventLedger(database); ledger.Append(Source());
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, InterruptedEventLedger>();
        var fault = (InterruptedEventLedger)proxy; fault.Inner = ledger;
        fault.AfterBatch = () => throw new IOException("after support commit");
        var state = new ClashOfRimNetworkState(ledger: proxy);
        var client = SupportClosureTestClient.Create(state);
        Expect<IOException>(() => client.Call(kind), "post-commit interruption not reached");
        SupportClosureTestAssertions.Closed(ledger.ListAll(), kind);
        Check(state.EventNotifications.GetVersion("owner") == 0, "post-commit fault already woke owner");
        var closure = ledger.ListAll().Single(item => item.EventId != "source");
        ledger.MarkDelivered(closure.EventId, "owner-base", OriginalTime);
        ledger.MarkApplied(closure.EventId, "owner-confirmed", OriginalTime);
        closure = ledger.Find(closure.EventId)!;
        var source = ledger.Find("source")!;
        fault.AfterBatch = null;
        if (reopen) { state = new ClashOfRimNetworkState(ledger: new SqliteAuthoritativeEventLedger(database)); client = SupportClosureTestClient.Create(state); }
        // Even a new reason and a contradictory alive/dead report cannot create another return.
        var response = client.Call(kind == "return" ? "loss" : kind == "loss" ? "return" : kind, "changed");
        Check(response.Accepted && !response.Created && response.ReturnId == closure.EventId, "retry changed closure identity");
        Check(state.Ledger.ListAll().Count == 2 && Same(source, state.Ledger.Find("source")!)
            && Same(closure, state.Ledger.Find(closure.EventId)!), "retry reset attribution or consumed return");
        Check(state.EventNotifications.GetVersion("owner") == 1, "retry lost wake-up");
    });

    private static void FileRollback(string kind) => SqliteTestDirectory.With("cor-support-closure-", root =>
    {
        string path = Path.Combine(root, "events.json");
        var ledger = new FileAuthoritativeEventLedger(path); ledger.Append(Source());
        var source = ledger.Find("source")!;
        var state = new ClashOfRimNetworkState(ledger: ledger);
        var client = SupportClosureTestClient.Create(state);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool failed = false;
            try { client.Call(kind); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            Check(failed, "file replacement fault not reached");
            Check(ledger.ListAll().Count == 1 && Same(source, ledger.Find("source")!), "file failure poisoned cache");
            var reopened = new FileAuthoritativeEventLedger(path);
            Check(reopened.ListAll().Count == 1 && Same(source, reopened.Find("source")!), "file failure partially persisted");
        }
        Check(client.Call(kind).Created, "file retry failed");
        SupportClosureTestAssertions.Closed(new FileAuthoritativeEventLedger(path).ListAll(), kind);
    });

    private static void Legacy(string kind, string storage) => SqliteTestDirectory.With("cor-support-closure-", root =>
    {
        IAuthoritativeEventLedger Open() => storage switch
        {
            "file" => new FileAuthoritativeEventLedger(Path.Combine(root, "events.json")),
            "sqlite" => new SqliteAuthoritativeEventLedger(Path.Combine(root, "events.sqlite")),
            _ => new InMemoryAuthoritativeEventLedger()
        };
        var ledger = Open(); ledger.Append(Source());
        var source = ledger.Find("source")!;
        var closure = AsLegacy(Closure(source, kind));
        ledger.Append(closure); // Historical append succeeded but closing the source failed.
        var state = new ClashOfRimNetworkState(ledger: ledger);
        var client = SupportClosureTestClient.Create(state);
        Check(client.Call(kind, "retry reason").Accepted, "legacy return was not recovered");
        SupportClosureTestAssertions.Closed(ledger.ListAll(), kind);
        Check(Same(closure, ledger.Find(closure.EventId)!), "legacy return rewritten");
        if (storage != "memory") SupportClosureTestAssertions.Closed(Open().ListAll(), kind);
        if (kind == "reject")
            Check(ledger.Find("source")!.DecisionAtUtc == OriginalTime && ledger.Find("source")!.DecisionReason == "original",
                "legacy rejection lost original decision");
        if (kind != "reject") return;

        // Opposite legacy window: rejection metadata exists but no return was appended.
        var second = Source() with { EventId = "rejected-only", IdempotencyKey = "rejected-only" };
        ledger.Append(second); ledger.MarkRejected(second.EventId, OriginalTime, "original");
        var response = client.Call(kind, "new reason", eventId: second.EventId);
        var repaired = ledger.Find(response.ReturnId!)!;
        Check(response.Created && repaired.CreatedAtUtc == OriginalTime
            && ((SupportPawnEventPayload)repaired.Payload).RejectionReason == "original", "legacy rejection guessed retry attribution");
    });

    private static void StaleSource(string kind)
    {
        var ledger = new InMemoryAuthoritativeEventLedger(); ledger.Append(Source());
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, InterruptedEventLedger>();
        var fault = (InterruptedEventLedger)proxy; fault.Inner = ledger;
        fault.BeforeBatch = () => ledger.ReportApplicationResult("source", EventApplicationResultKind.NeedsManualReview, "newer report", null);
        var state = new ClashOfRimNetworkState(ledger: proxy);
        var client = SupportClosureTestClient.Create(state);
        Expect<InvalidOperationException>(() => client.Call(kind), "stale full source accepted");
        Check(ledger.ListAll().Count == 1 && ledger.Find("source")!.LastFailureReason == "newer report", "stale batch overwrote newer metadata");
    }

    private static void Guards()
    {
        var state = new ClashOfRimNetworkState(); state.Ledger.Append(Source());
        var client = SupportClosureTestClient.Create(state);
        Check(!client.Call("loss", user: "intruder").Accepted, "wrong target finished support");
        Check(!client.Call("reject", user: "intruder").Accepted, "wrong target rejected support");
        Check(!client.Call("loss", pawnKey: "different").Accepted, "wrong pawn finished support");
        Check(!client.Call("return", packageKey: "different").Accepted, "wrong package returned");
        Check(state.Ledger.ListAll().Count == 1, "invalid request mutated ledger");
        foreach (var status in new[] { ServerEventStatus.Cancelled, ServerEventStatus.Failed, ServerEventStatus.AppliedToSnapshot })
        {
            var ledger = new InMemoryAuthoritativeEventLedger(); ledger.Append(Source() with { Status = status });
            var ended = new ClashOfRimNetworkState(ledger: ledger);
            var endedClient = SupportClosureTestClient.Create(ended);
            Check(!endedClient.Call("reject").Accepted, "ended support rejected again");
            if (status == ServerEventStatus.AppliedToSnapshot)
                Check(endedClient.Call("return").Accepted, "arrived support cannot finish");
            else Check(!endedClient.Call("loss").Accepted && ledger.ListAll().Count == 1, "ended support created another closure");
        }
        var returned = Closure(Source(), "return") with { EventId = "source", Target = new("userA", "colonyA") };
        var returnState = new ClashOfRimNetworkState(); returnState.Ledger.Append(returned);
        var returnClient = SupportClosureTestClient.Create(returnState);
        Check(!returnClient.Call("loss").Accepted && returnState.Ledger.ListAll().Count == 1, "return-of-return accepted");
    }

    private static void ConflictingReturns()
    {
        foreach (string conflict in new[] { "multiple", "owner", "pawn", "source", "return-flag", "decision" })
        {
            var state = new ClashOfRimNetworkState(); var source = Source(); state.Ledger.Append(source);
            var client = SupportClosureTestClient.Create(state);
            var closure = Closure(source, conflict == "decision" ? "reject" : "return");
            if (conflict == "owner") closure = closure with { Target = new("intruder", "home") };
            if (conflict == "pawn") closure = closure with { Payload = ((SupportPawnEventPayload)closure.Payload) with { PawnGlobalKey = "other" } };
            if (conflict == "source") closure = closure with { Payload = ((SupportPawnEventPayload)closure.Payload) with { SourceEventId = "other" } };
            if (conflict == "return-flag") closure = closure with { Payload = ((SupportPawnEventPayload)closure.Payload) with { ReturnToSender = false } };
            state.Ledger.Append(closure);
            if (conflict == "multiple") state.Ledger.Append(Closure(source, "loss"));
            if (conflict == "decision") state.Ledger.MarkRejected("source", OriginalTime, "other decision");
            string before = JsonSerializer.Serialize(state.Ledger.ListAll());
            Expect<InvalidOperationException>(() => client.Call(conflict == "decision" ? "reject" : "return"), "conflicting closure accepted: " + conflict);
            Check(before == JsonSerializer.Serialize(state.Ledger.ListAll()), "conflict mutated ledger");
        }
    }

    private static void ConcurrentFinish()
    {
        foreach (bool mixed in new[] { false, true })
        {
            var state = new ClashOfRimNetworkState(); state.Ledger.Append(Source());
            var client = SupportClosureTestClient.Create(state);
            var calls = Enumerable.Range(0, 16).Select(index => Task.Run(() =>
                client.Call(mixed ? Kinds[index % Kinds.Count] : "return", "reason-" + index))).ToArray();
            Task.WaitAll(calls);
            Check(state.Ledger.ListAll().Count == 2 && calls.Count(task => task.Result.Created) == 1,
                "concurrent completions created duplicate returns");
        }
    }

    private static void OtherClosureProducers(Func<string, SaveSnapshotPackage> fixture)
    {
        foreach (string kind in Kinds)
        {
            var state = new ClashOfRimNetworkState(); var source = Source();
            var closure = Closure(source, kind);
            state.Ledger.Append(source); state.Ledger.Append(closure);
            var package = fixture("dead-support");
            var index = package.Index with { Pawns = [new PawnSummary("Pawn_1", PawnKey,
                null, null, "Human", null, "Ally", true, null, null)] };
            var upload = SnapshotUploadResult.Accept(new LatestSnapshotRecord(package.Envelope.Identity,
                package.Envelope, index, OriginalTime.AddDays(1)));
            state.SupportDeaths.Process(upload);
            Check(state.Ledger.ListAll().Count == 2 && Same(source, state.Ledger.Find("source")!),
                "snapshot death manufactured another closure after a legacy half-commit");
            Check(SupportClosures.HasSupportFinishEvent(source, state.Ledger.ListAll()),
                "raid cleanup failed to recognize an existing closure");
        }
    }

    private static void KeyEncodingAndMigration() => SqliteTestDirectory.With("cor-support-closure-", root =>
    {
        string[] values = ["source", "source:child", "a:b", "a-b", "a_b", "a/b", "a+b", "中文", "[\"x\"]"];
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string source in values)
        foreach (string reason in values)
        foreach (string key in new[] { SupportClosureKeys.Finish(source, reason), SupportClosureKeys.Loss(source, reason) })
            Check(keys.Add(key) && ids.Add(AuthoritativeEventFactory.BuildEventId(ServerEventType.SupportPawn, key)),
                "tuple encoding or event normalization collapsed distinct identities");
        string database = Path.Combine(root, "server.sqlite");
        ServerDatabaseMigrator.Migrate(database);
        var ledger = new SqliteAuthoritativeEventLedger(database); var sourceEvent = Source();
        ledger.Append(sourceEvent); ledger.Append(AsLegacy(Closure(sourceEvent, "loss")));
        SqliteTestDatabase.Execute(database, "update server_schema_metadata set metadata_value = '23' where metadata_key = 'schema_version';");
        var original = ledger.ListAll().ToDictionary(item => item.EventId);
        var migration = ServerDatabaseMigrator.Migrate(database);
        Check(migration.AppliedMigrations.Contains("23->24") && migration.FinalVersion == ServerDatabaseSchema.CurrentVersion, "support key migration missing");
        var reopened = new SqliteAuthoritativeEventLedger(database);
        Check(original.All(pair => Same(pair.Value, reopened.Find(pair.Key)!)), "migration renamed legacy support data");
    });

    private static void LegacyLossCannotSuppressOtherSource()
    {
        var state = new ClashOfRimNetworkState(); var first = Source();
        var client = SupportClosureTestClient.Create(state);
        var second = Source() with { EventId = "source:child", IdempotencyKey = "source:child" };
        state.Ledger.Append(first); state.Ledger.Append(second);
        var legacy = AsLegacy(Closure(first, "loss", "child:RaidSettlementLost"));
        state.Ledger.Append(legacy);
        Check(!SupportClosures.HasSupportFinishEvent(second, state.Ledger.ListAll()), "another source's legacy key suppressed raid cleanup");
        Check(client.Call("loss", reason: "RaidSettlementLost", eventId: second.EventId).Created,
            "another source's legacy key suppressed a new loss");
        Check(Same(legacy, state.Ledger.Find(legacy.EventId)!), "new loss rewrote another source's legacy return");
    }

    private static void AuthenticationGuards()
    {
        foreach (string kind in Kinds)
        foreach (bool replay in new[] { false, true })
        foreach (string bad in new[] { "missing", "invalid", "owner", "colony", "expired", "replaced" })
        {
            var state = new ClashOfRimNetworkState(); state.Ledger.Append(Source());
            var client = SupportClosureTestClient.Create(state);
            string valid = client.Token("userA");
            if (replay) Check(client.Call(kind).Accepted, "authenticated initial closure failed");
            string? token = bad switch
            {
                "missing" => null,
                "invalid" => "invalid-token",
                "owner" => client.Token("owner", "home"),
                "colony" => client.Token("userA", "other-colony"),
                _ => valid
            };
            if (bad == "expired") state.LoginSessions.EndUser("userA");
            if (bad == "replaced") state.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out _);
            string before = JsonSerializer.Serialize(state.Ledger.ListAll());
            long ownerVersion = state.EventNotifications.GetVersion("owner"), targetVersion = state.EventNotifications.GetVersion("userA");
            Check(!client.Call(kind, token: token, authenticate: false).Accepted,
                $"{kind}/{bad}/replay={replay}: unauthorized support closure accepted");
            Check(before == JsonSerializer.Serialize(state.Ledger.ListAll())
                && ownerVersion == state.EventNotifications.GetVersion("owner") && targetVersion == state.EventNotifications.GetVersion("userA"),
                "unauthorized closure changed events or signalled players");
        }
    }
}
