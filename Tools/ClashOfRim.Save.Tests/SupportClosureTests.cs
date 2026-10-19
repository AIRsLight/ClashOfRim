using AIRsLight.ClashOfRim.Network.Modules.Support;
using AIRsLight.ClashOfRim.Network.Modules.Transfers;
using System.Reflection;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static partial class SupportClosureTests
{
    private const string PawnKey = "owner:owner/colony:home/pawn:Pawn_1";
    private static readonly DateTimeOffset OriginalTime = DateTimeOffset.UnixEpoch.AddDays(2);
    private static readonly string[] Kinds = ["return", "loss", "reject"];
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClashOfRimNetworkState, Dictionary<string, string>> Tokens = new();

    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        RunRecoveryPreparation(fixture);
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
            string token = Token(state, "userA");
            if (replay) Require(Call(state, kind).Accepted, "initial closure for queued retry failed");
            SnapshotMutationAuthorizationProbe.RevokedWhileWaiting(state, "userA", "colonyA", replaced,
                () => Call(state, kind, token: token, authenticate: false).Accepted);
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

    private static void NestedSourceIdentities(string storage, string kind, bool legacyChild) => WithDirectory(root =>
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
        var childResult = Call(state, kind, reason: "manual:finished", eventId: child.EventId);
        Require(childResult.Accepted && childResult.Created != legacyChild, "nested source could not finish");
        var childClosure = ledger.Find(childResult.ReturnId!)!;
        var endedChild = ledger.Find(child.EventId)!;
        if (storage != "memory") state = new ClashOfRimNetworkState(ledger: Open());
        // These tuples produced exactly the same legacy finish/loss key. Both must now succeed.
        var parentResult = Call(state, kind, reason: "child:manual:finished");
        Require(parentResult.Accepted && parentResult.Created && parentResult.ReturnId != childResult.ReturnId,
            "a nested source closure was treated as the parent closure");
        Require(Same(childClosure, state.Ledger.Find(childClosure.EventId)!) && Same(endedChild, state.Ledger.Find(child.EventId)!),
            "ending the parent modified the nested source");
        foreach (var pair in new[] { (parent.EventId, parentResult.ReturnId), (child.EventId, childResult.ReturnId) })
        {
            var retry = Call(state, kind, reason: "different:retry", eventId: pair.EventId);
            Require(retry.Accepted && !retry.Created && retry.ReturnId == pair.ReturnId, "nested identity replay chose another closure");
        }
        Require(state.Ledger.ListAll().Count == 4, "nested identity created extra closures");
    });

    private static void Rollback(string kind, string failure, bool reopen) => WithDirectory(root =>
    {
        string database = Path.Combine(root, "events.sqlite");
        IAuthoritativeEventLedger Open() => new SqliteAuthoritativeEventLedger(database);
        var ledger = Open(); ledger.Append(Source());
        var source = ledger.Find("source")!;
        var state = new ClashOfRimNetworkState(ledger: ledger);
        string condition = failure == "insert" ? "new.event_id != 'source'" : "new.event_id = 'source'";
        Sql(database, $"create trigger fail_closure before {failure} on events when {condition} begin select raise(abort, 'injected support failure'); end;");
        Expect<SqliteException>(() => Call(state, kind), "SQL fault not reached: " + kind);
        foreach (var current in new[] { ledger, Open() })
            Require(current.ListAll().Count == 1 && Same(source, current.Find("source")!), "support batch partially committed");
        Require(state.EventNotifications.GetVersion("owner") == 0, "failed batch woke owner");
        Sql(database, "drop trigger fail_closure;");
        if (reopen) state = new ClashOfRimNetworkState(ledger: Open());
        var response = Call(state, kind);
        Require(response.Accepted && response.Created, "retry did not create closure");
        Verify(state.Ledger, kind);
        Verify(Open(), kind);
    });

    private static void InterruptedWake(string kind, bool reopen) => WithDirectory(root =>
    {
        string database = Path.Combine(root, "events.sqlite");
        var ledger = new SqliteAuthoritativeEventLedger(database); ledger.Append(Source());
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, InterruptedEventLedger>();
        var fault = (InterruptedEventLedger)proxy; fault.Inner = ledger;
        fault.AfterBatch = () => throw new IOException("after support commit");
        var state = new ClashOfRimNetworkState(ledger: proxy);
        Expect<IOException>(() => Call(state, kind), "post-commit interruption not reached");
        Verify(ledger, kind);
        Require(state.EventNotifications.GetVersion("owner") == 0, "post-commit fault already woke owner");
        var closure = ledger.ListAll().Single(item => item.EventId != "source");
        ledger.MarkDelivered(closure.EventId, "owner-base", OriginalTime);
        ledger.MarkApplied(closure.EventId, "owner-confirmed", OriginalTime);
        closure = ledger.Find(closure.EventId)!;
        var source = ledger.Find("source")!;
        fault.AfterBatch = null;
        if (reopen) state = new ClashOfRimNetworkState(ledger: new SqliteAuthoritativeEventLedger(database));
        // Even a new reason and a contradictory alive/dead report cannot create another return.
        var response = Call(state, kind == "return" ? "loss" : kind == "loss" ? "return" : kind, "changed");
        Require(response.Accepted && !response.Created && response.ReturnId == closure.EventId, "retry changed closure identity");
        Require(state.Ledger.ListAll().Count == 2 && Same(source, state.Ledger.Find("source")!)
            && Same(closure, state.Ledger.Find(closure.EventId)!), "retry reset attribution or consumed return");
        Require(state.EventNotifications.GetVersion("owner") == 1, "retry lost wake-up");
    });

    private static void FileRollback(string kind) => WithDirectory(root =>
    {
        string path = Path.Combine(root, "events.json");
        var ledger = new FileAuthoritativeEventLedger(path); ledger.Append(Source());
        var source = ledger.Find("source")!;
        var state = new ClashOfRimNetworkState(ledger: ledger);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool failed = false;
            try { Call(state, kind); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            Require(failed, "file replacement fault not reached");
            Require(ledger.ListAll().Count == 1 && Same(source, ledger.Find("source")!), "file failure poisoned cache");
            var reopened = new FileAuthoritativeEventLedger(path);
            Require(reopened.ListAll().Count == 1 && Same(source, reopened.Find("source")!), "file failure partially persisted");
        }
        Require(Call(state, kind).Created, "file retry failed");
        Verify(new FileAuthoritativeEventLedger(path), kind);
    });

    private static void Legacy(string kind, string storage) => WithDirectory(root =>
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
        Require(Call(state, kind, "retry reason").Accepted, "legacy return was not recovered");
        Verify(ledger, kind);
        Require(Same(closure, ledger.Find(closure.EventId)!), "legacy return rewritten");
        if (storage != "memory") Verify(Open(), kind);
        if (kind == "reject")
            Require(ledger.Find("source")!.DecisionAtUtc == OriginalTime && ledger.Find("source")!.DecisionReason == "original",
                "legacy rejection lost original decision");
        if (kind != "reject") return;

        // Opposite legacy window: rejection metadata exists but no return was appended.
        var second = Source() with { EventId = "rejected-only", IdempotencyKey = "rejected-only" };
        ledger.Append(second); ledger.MarkRejected(second.EventId, OriginalTime, "original");
        var response = Call(state, kind, "new reason", eventId: second.EventId);
        var repaired = ledger.Find(response.ReturnId!)!;
        Require(response.Created && repaired.CreatedAtUtc == OriginalTime
            && ((SupportPawnEventPayload)repaired.Payload).RejectionReason == "original", "legacy rejection guessed retry attribution");
    });

    private static void StaleSource(string kind)
    {
        var ledger = new InMemoryAuthoritativeEventLedger(); ledger.Append(Source());
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, InterruptedEventLedger>();
        var fault = (InterruptedEventLedger)proxy; fault.Inner = ledger;
        fault.BeforeBatch = () => ledger.ReportApplicationResult("source", EventApplicationResultKind.NeedsManualReview, "newer report", null);
        var state = new ClashOfRimNetworkState(ledger: proxy);
        Expect<InvalidOperationException>(() => Call(state, kind), "stale full source accepted");
        Require(ledger.ListAll().Count == 1 && ledger.Find("source")!.LastFailureReason == "newer report", "stale batch overwrote newer metadata");
    }

    private static void Guards()
    {
        var state = new ClashOfRimNetworkState(); state.Ledger.Append(Source());
        Require(!Call(state, "loss", user: "intruder").Accepted, "wrong target finished support");
        Require(!Call(state, "reject", user: "intruder").Accepted, "wrong target rejected support");
        Require(!Call(state, "loss", pawnKey: "different").Accepted, "wrong pawn finished support");
        Require(!Call(state, "return", packageKey: "different").Accepted, "wrong package returned");
        Require(state.Ledger.ListAll().Count == 1, "invalid request mutated ledger");
        foreach (var status in new[] { ServerEventStatus.Cancelled, ServerEventStatus.Failed, ServerEventStatus.AppliedToSnapshot })
        {
            var ledger = new InMemoryAuthoritativeEventLedger(); ledger.Append(Source() with { Status = status });
            var ended = new ClashOfRimNetworkState(ledger: ledger);
            Require(!Call(ended, "reject").Accepted, "ended support rejected again");
            if (status == ServerEventStatus.AppliedToSnapshot)
                Require(Call(ended, "return").Accepted, "arrived support cannot finish");
            else Require(!Call(ended, "loss").Accepted && ledger.ListAll().Count == 1, "ended support created another closure");
        }
        var returned = Closure(Source(), "return") with { EventId = "source", Target = new("userA", "colonyA") };
        var returnState = new ClashOfRimNetworkState(); returnState.Ledger.Append(returned);
        Require(!Call(returnState, "loss").Accepted && returnState.Ledger.ListAll().Count == 1, "return-of-return accepted");
    }

    private static void ConflictingReturns()
    {
        foreach (string conflict in new[] { "multiple", "owner", "pawn", "source", "return-flag", "decision" })
        {
            var state = new ClashOfRimNetworkState(); var source = Source(); state.Ledger.Append(source);
            var closure = Closure(source, conflict == "decision" ? "reject" : "return");
            if (conflict == "owner") closure = closure with { Target = new("intruder", "home") };
            if (conflict == "pawn") closure = closure with { Payload = ((SupportPawnEventPayload)closure.Payload) with { PawnGlobalKey = "other" } };
            if (conflict == "source") closure = closure with { Payload = ((SupportPawnEventPayload)closure.Payload) with { SourceEventId = "other" } };
            if (conflict == "return-flag") closure = closure with { Payload = ((SupportPawnEventPayload)closure.Payload) with { ReturnToSender = false } };
            state.Ledger.Append(closure);
            if (conflict == "multiple") state.Ledger.Append(Closure(source, "loss"));
            if (conflict == "decision") state.Ledger.MarkRejected("source", OriginalTime, "other decision");
            string before = JsonSerializer.Serialize(state.Ledger.ListAll());
            Expect<InvalidOperationException>(() => Call(state, conflict == "decision" ? "reject" : "return"), "conflicting closure accepted: " + conflict);
            Require(before == JsonSerializer.Serialize(state.Ledger.ListAll()), "conflict mutated ledger");
        }
    }

    private static void ConcurrentFinish()
    {
        foreach (bool mixed in new[] { false, true })
        {
            var state = new ClashOfRimNetworkState(); state.Ledger.Append(Source());
            var calls = Enumerable.Range(0, 16).Select(index => Task.Run(() =>
                Call(state, mixed ? Kinds[index % Kinds.Length] : "return", "reason-" + index))).ToArray();
            Task.WaitAll(calls);
            Require(state.Ledger.ListAll().Count == 2 && calls.Count(task => task.Result.Created) == 1,
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
            Require(state.Ledger.ListAll().Count == 2 && Same(source, state.Ledger.Find("source")!),
                "snapshot death manufactured another closure after a legacy half-commit");
            Require(SupportClosures.HasSupportFinishEvent(source, state.Ledger.ListAll()),
                "raid cleanup failed to recognize an existing closure");
        }
    }

    private static AuthoritativeEvent Source() => AuthoritativeEventFactory.Create(ServerEventType.SupportPawn,
        new("owner", "home"), new("userA", "colonyA"), "source", true,
        new SupportPawnEventPayload(PawnKey, "original", "Ally", true, null, PawnPackage: Package()), OriginalTime.AddDays(-1))
        with { EventId = "source", Status = ServerEventStatus.DeliveredToClient, RejectionPolicy = EventRejectionPolicy.RejectableByTarget };

    private static PawnExchangePackage Package(string key = PawnKey)
    {
        const string xml = "<saveable Class=\"Pawn\"><id>Pawn_1</id><kindDef>Colonist</kindDef></saveable>";
        return new(1, new(key, "original", "Ally", false, "Player"), new("Human", "Colonist", "Player", "Male"),
            new("Ally", null, null, null, null, null, null), new(false, null, null, null, null), [], [], [],
            new(xml, SafePawnExchangeSerializer.ComputeScribeXmlSha256(xml), []));
    }

    private static AuthoritativeEvent Closure(AuthoritativeEvent source, string kind, string reason = "original")
    {
        var payload = (SupportPawnEventPayload)source.Payload;
        return kind switch
        {
            "reject" => SupportReturns.CreateRejectedSupportPawnReturnEvent(source, payload, reason, false, OriginalTime),
            "loss" => SupportReturns.CreateSupportPawnLossReturnEvent(source, payload, "Ally", reason, false, OriginalTime),
            _ => SupportReturns.CreateSupportPawnFinishReturnEvent(source, payload, Request("return", reason), Package(), false, OriginalTime)
        };
    }

    private static AuthoritativeEvent AsLegacy(AuthoritativeEvent item)
    {
        var payload = (SupportPawnEventPayload)item.Payload;
        if (item.IdempotencyKey.EndsWith(":rejected-return", StringComparison.Ordinal)) return item;
        string key = $"support-{(payload.PawnPackage is null ? "loss" : "finish")}:{payload.SourceEventId}:{payload.ReturnReason}";
        return item with { EventId = AuthoritativeEventFactory.BuildLegacyEventId(item.Type, key), IdempotencyKey = key };
    }

    private static void KeyEncodingAndMigration() => WithDirectory(root =>
    {
        string[] values = ["source", "source:child", "a:b", "a-b", "a_b", "a/b", "a+b", "中文", "[\"x\"]"];
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string source in values)
        foreach (string reason in values)
        foreach (string key in new[] { SupportClosureKeys.Finish(source, reason), SupportClosureKeys.Loss(source, reason) })
            Require(keys.Add(key) && ids.Add(AuthoritativeEventFactory.BuildEventId(ServerEventType.SupportPawn, key)),
                "tuple encoding or event normalization collapsed distinct identities");
        string database = Path.Combine(root, "server.sqlite");
        ServerDatabaseMigrator.Migrate(database);
        var ledger = new SqliteAuthoritativeEventLedger(database); var sourceEvent = Source();
        ledger.Append(sourceEvent); ledger.Append(AsLegacy(Closure(sourceEvent, "loss")));
        Sql(database, "update server_schema_metadata set metadata_value = '23' where metadata_key = 'schema_version';");
        var original = ledger.ListAll().ToDictionary(item => item.EventId);
        var migration = ServerDatabaseMigrator.Migrate(database);
        Require(migration.AppliedMigrations.Contains("23->24") && migration.FinalVersion == ServerDatabaseSchema.CurrentVersion, "support key migration missing");
        var reopened = new SqliteAuthoritativeEventLedger(database);
        Require(original.All(pair => Same(pair.Value, reopened.Find(pair.Key)!)), "migration renamed legacy support data");
    });

    private static void LegacyLossCannotSuppressOtherSource()
    {
        var state = new ClashOfRimNetworkState(); var first = Source();
        var second = Source() with { EventId = "source:child", IdempotencyKey = "source:child" };
        state.Ledger.Append(first); state.Ledger.Append(second);
        var legacy = AsLegacy(Closure(first, "loss", "child:RaidSettlementLost"));
        state.Ledger.Append(legacy);
        Require(!SupportClosures.HasSupportFinishEvent(second, state.Ledger.ListAll()), "another source's legacy key suppressed raid cleanup");
        Require(Call(state, "loss", reason: "RaidSettlementLost", eventId: second.EventId).Created,
            "another source's legacy key suppressed a new loss");
        Require(Same(legacy, state.Ledger.Find(legacy.EventId)!), "new loss rewrote another source's legacy return");
    }

    private static FinishSupportPawnRequest Request(string kind, string reason = "original", string user = "userA",
        string pawnKey = PawnKey, string packageKey = PawnKey, string eventId = "source", string? token = null) =>
        new("request-" + reason, eventId, user, "colonyA", "current", reason, pawnKey, "Ally", kind == "loss",
            kind == "loss" ? null : ItemTransferMapper.ToPawnExchangePackageDto(Package(packageKey)), token);

    private static (bool Accepted, bool Created, string? ReturnId) Call(ClashOfRimNetworkState state, string kind,
        string reason = "original", string user = "userA", string pawnKey = PawnKey, string packageKey = PawnKey, string eventId = "source",
        string? token = null, bool authenticate = true)
    {
        if (authenticate) token = Token(state, user);
        if (kind == "reject")
        {
            var response = state.Support.RejectSupportPawn(new RejectSupportPawnRequest(eventId, user, "colonyA", "current", reason, token));
            return (response.Result.Accepted, response.ReturnEventCreated, response.ReturnEventId);
        }
        var finish = state.Support.FinishSupportPawn(Request(kind, reason, user, pawnKey, packageKey, eventId, token));
        if (kind == "loss" && finish.Result.Accepted && finish.Created)
            Require(((SupportPawnEventPayload)state.Ledger.Find(finish.ReturnEventId!)!.Payload).PawnReference?.Dead == true,
                "Loss return did not explicitly identify the dead pawn independently of its reason.");
        return (finish.Result.Accepted, finish.Created, finish.ReturnEventId);
    }

    private static string Token(ClashOfRimNetworkState state, string user, string colony = "colonyA")
    {
        var tokens = Tokens.GetOrCreateValue(state);
        lock (tokens)
        {
            string key = user + "/" + colony;
            if (tokens.TryGetValue(key, out var token)) return token;
            var now = DateTimeOffset.UtcNow;
            Require(state.LoginSessions.TryCreate(user, colony, now, out var session), "cannot create test session");
            return tokens[key] = state.AuthTokens.Issue(user, user, colony, session, now);
        }
    }

    private static void AuthenticationGuards()
    {
        foreach (string kind in Kinds)
        foreach (bool replay in new[] { false, true })
        foreach (string bad in new[] { "missing", "invalid", "owner", "colony", "expired", "replaced" })
        {
            var state = new ClashOfRimNetworkState(); state.Ledger.Append(Source());
            string valid = Token(state, "userA");
            if (replay) Require(Call(state, kind).Accepted, "authenticated initial closure failed");
            string? token = bad switch
            {
                "missing" => null,
                "invalid" => "invalid-token",
                "owner" => Token(state, "owner", "home"),
                "colony" => Token(state, "userA", "other-colony"),
                _ => valid
            };
            if (bad == "expired") state.LoginSessions.EndUser("userA");
            if (bad == "replaced") state.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out _);
            string before = JsonSerializer.Serialize(state.Ledger.ListAll());
            long ownerVersion = state.EventNotifications.GetVersion("owner"), targetVersion = state.EventNotifications.GetVersion("userA");
            Require(!Call(state, kind, token: token, authenticate: false).Accepted,
                $"{kind}/{bad}/replay={replay}: unauthorized support closure accepted");
            Require(before == JsonSerializer.Serialize(state.Ledger.ListAll())
                && ownerVersion == state.EventNotifications.GetVersion("owner") && targetVersion == state.EventNotifications.GetVersion("userA"),
                "unauthorized closure changed events or signalled players");
        }
    }

    private static void Verify(IAuthoritativeEventLedger ledger, string kind)
    {
        var source = ledger.Find("source")!;
        Require(ledger.ListAll().Count == 2 && source.Status == (kind == "reject" ? ServerEventStatus.RejectedByTarget
            : kind == "loss" ? ServerEventStatus.Failed : ServerEventStatus.AppliedToSnapshot), "closure source status missing");
        var result = ledger.ListAll().Single(item => item.EventId != "source");
        var payload = (SupportPawnEventPayload)result.Payload;
        Require(result.Actor == source.Target && result.Target == source.Actor && payload.SourceEventId == source.EventId
            && payload.ReturnToSender && payload.PawnGlobalKey == PawnKey, "return not bound to original support");
        if (kind == "reject") Require(source.TargetDecision == TargetEventDecision.Rejected
            && source.LastApplicationResult == EventApplicationResultKind.Rejected && source.DecisionAtUtc == result.CreatedAtUtc
            && source.DecisionReason == payload.RejectionReason, "rejection metadata not committed with return");
    }

    private static bool Same(AuthoritativeEvent left, AuthoritativeEvent right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right)
        && JsonSerializer.Serialize(left.Payload, left.Payload.GetType()) == JsonSerializer.Serialize(right.Payload, right.Payload.GetType());
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Expect<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception(message); }
    private static void Sql(string path, string sql)
    { using var connection = new SqliteConnection("Data Source=" + path); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static void WithDirectory(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-support-closure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); } finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
