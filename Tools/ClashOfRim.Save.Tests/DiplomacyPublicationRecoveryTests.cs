using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using System.Reflection;
using System.Globalization;
using System.Text.Json;
using static DiplomacyTestSupport;

internal static class DiplomacyPublicationRecoveryTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clash-diplomacy-publication-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string backend in new[] { "memory", "file", "sqlite" })
            foreach (string kind in new[] { "WarDeclaration", "AllianceCancellation", "AllianceRequest", "PeaceRequest" })
            foreach (bool restart in new[] { false, true })
                RelationFailure(factory, root, backend, kind, restart);
            foreach (string backend in new[] { "memory", "file", "sqlite" })
            {
                foreach (string kind in new[] { "WarDeclaration", "AllianceCancellation", "AllianceRequest", "PeaceRequest", "SupportRequest" })
                foreach (string phase in new[] { "commit-before", "commit-after", "signal-before", "signal-after" })
                foreach (bool restart in new[] { false, true })
                    InterruptedPublication(factory, root, backend, kind, phase, restart);
                foreach (string projection in new[] { "existing", "missing", "administrator" }) LatestWins(factory, root, backend, projection);
                foreach (string kind in new[] { "AllianceRequest", "PeaceRequest" })
                foreach (bool accepted in new[] { true, false })
                foreach (string receipt in new[] { "missing", "legacy", "conflict" })
                    LegacyDecision(factory, root, backend, kind, accepted, receipt);
                foreach (bool actor in new[] { false, true })
                foreach (string phase in new[] { "create", "decision", "legacy" }) Abandoned(factory, root, backend, actor, phase);
            }
            foreach (string backend in new[] { "file", "sqlite" })
            foreach (bool decision in new[] { false, true }) DurableRelationFailure(factory, root, backend, decision);
            OrderingAndScan(factory);
            CompositionDeletionOrder();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!string.Equals(Directory.GetParent(root)!.FullName, Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Temporary test path escaped its parent.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static void RelationFailure(Func<string, SaveSnapshotPackage> factory, string root, string backend, string kind, bool restart)
    {
        string path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var memory = new InMemoryAuthoritativeEventLedger();
        IAuthoritativeEventLedger Open() => backend switch
        { "file" => new FileAuthoritativeEventLedger(path), "sqlite" => new SqliteAuthoritativeEventLedger(path), _ => memory };
        var slot = new RecoveryTestSupport.Slot();
        var f = Fixture(factory, Open(), new DiplomacyRelationRegistry(slot)); SetRelation(f, Initial(kind));
        bool unilateral = kind is "WarDeclaration" or "AllianceCancellation";
        string? id = unilateral ? null : f.Diplomacy.Create(Request(f, kind)).EventId;
        string token = f.Issue("userB", "colonyB");
        slot.Fail = true;
        bool failed = false;
        try { if (unilateral) f.Diplomacy.Create(Request(f, kind)); else f.Diplomacy.Respond(Response(id!, token)); }
        catch (IOException) { failed = true; }
        Check(failed && Relation(f) == Initial(kind), "Expected relation persistence failure.");
        id ??= f.Ledger.FindByIdempotencyKey("request")!.EventId;
        var source = f.Ledger.Find(id)!;
        slot.Fail = false;
        if (restart) { f = Fixture(factory, Open(), new DiplomacyRelationRegistry(slot)); token = f.Issue("userB", "colonyB"); }
        var replay = unilateral ? f.Diplomacy.Create(Request(f, kind)) : f.Diplomacy.Respond(Response(id, token, false));
        Check(replay.Result.Accepted && Relation(f) == Final(kind), "Persisted diplomacy replay did not recover its relation: " + kind);
        var relation = f.Relations.GetRelation("userA", "colonyA", "userB", "colonyB")!;
        Check(relation.SourceEventId == id && relation.UpdatedAtUtc == (unilateral ? source.CreatedAtUtc : source.DecisionAtUtc),
            "Recovery restarted the relation cooldown or lost its source.");
        Check(f.Notifications.GetVersion(unilateral ? "userB" : "userA") == 1, "Recovery did not wake the original recipient.");
    }

    private static Func<IAuthoritativeEventLedger> Storage(string root, string backend)
    {
        string path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var memory = new InMemoryAuthoritativeEventLedger();
        return () => backend switch
        { "file" => new FileAuthoritativeEventLedger(path), "sqlite" => new SqliteAuthoritativeEventLedger(path), _ => memory };
    }

    private static void InterruptedPublication(Func<string, SaveSnapshotPackage> factory, string root, string backend,
        string kind, string phase, bool restart)
    {
        var open = Storage(root, backend); var ledger = open(); var slot = new RecoveryTestSupport.Slot();
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, SnapshotSupportDeathTests.InterruptedLedger>();
        var faults = (SnapshotSupportDeathTests.InterruptedLedger)proxy; faults.Inner = ledger;
        bool armed = false;
        void Signal(string user, EventNotificationHub hub)
        {
            if (armed && phase == "signal-before") throw new IOException("before signal");
            hub.SignalUser(user);
            if (armed && phase == "signal-after") throw new IOException("after signal");
        }
        var f = Fixture(factory, proxy, new DiplomacyRelationRegistry(slot), Signal); SetRelation(f, Initial(kind));
        bool decision = kind is "AllianceRequest" or "PeaceRequest";
        string? id = decision ? f.Diplomacy.Create(Request(f, kind)).EventId : null;
        string token = f.Issue("userB", "colonyB");
        if (phase.StartsWith("commit", StringComparison.Ordinal))
        {
            Action fail = () => throw new IOException("ledger interruption");
            if (decision) { if (phase == "commit-before") faults.BeforeBatch = fail; else faults.AfterBatch = fail; }
            else { if (phase == "commit-before") faults.BeforeAppend = fail; else faults.AfterAppend = fail; }
        }
        armed = true;
        Fails<IOException>(() => { if (decision) f.Diplomacy.Respond(Response(id!, token)); else f.Diplomacy.Create(Request(f, kind)); });
        armed = false; faults.BeforeBatch = faults.AfterBatch = faults.BeforeAppend = faults.AfterAppend = null;
        var stored = ledger.FindByIdempotencyKey("request");
        if (phase != "commit-before")
        {
            Check(stored is not null, "Post-commit interruption lost its source.");
            string before = Evidence(f); int writes = slot.Writes;
            var denied = decision ? f.Diplomacy.Respond(Response(id!, "bad-token")) : f.Diplomacy.Create(Request(f, kind, token: "bad-token"));
            Check(!denied.Result.Accepted && before == Evidence(f) && slot.Writes == writes, "Unauthorized retry started publication recovery.");
        }
        if (restart) { f = Fixture(factory, open(), new DiplomacyRelationRegistry(slot)); token = f.Issue("userB", "colonyB"); }
        if (phase != "commit-before")
        {
            f.Configuration = new(diplomacyRelationChangeCooldown: TimeSpan.FromDays(100), diplomacySupportRequestCooldown: TimeSpan.FromDays(100));
            foreach (var who in new[] { ("userA", "colonyA"), ("userB", "colonyB") })
            {
                var latest = f.Snapshots.GetLatest(who.Item1, who.Item2)!;
                f.Snapshots.StoreLatest(latest with { Identity = latest.Identity with { SnapshotId = "advanced" } });
            }
        }
        var replay = decision ? f.Diplomacy.Respond(Response(id!, token, phase == "commit-before")) : f.Diplomacy.Create(Request(f, kind));
        Check(replay.Result.Accepted && Relation(f) == Final(kind), "Interrupted publication failed to complete: " + kind + "/" + phase);
        string userId = decision ? "userA" : "userB";
        Check(f.Notifications.GetVersion(userId) >= 1, "Persisted event was not signalled.");
        var source = f.Ledger.Find(replay.EventId!)!;
        if (decision) Check(replay.NotificationEventId is not null && source.TargetDecision == TargetEventDecision.Accepted, "Opposite retry changed the committed decision.");
        string evidence = Evidence(f); int afterWrites = slot.Writes; long version = f.Notifications.GetVersion(userId);
        if (decision) f.Diplomacy.Respond(Response(source.EventId, token, false)); else f.Diplomacy.Create(Request(f, kind));
        Check(Evidence(f) == evidence && slot.Writes == afterWrites && f.Notifications.GetVersion(userId) == version,
            "Completed replay repeated relation writes or wake-ups.");
    }

    private static void LatestWins(Func<string, SaveSnapshotPackage> factory, string root, string backend, string projection)
    {
        var open = Storage(root, backend); var slot = new RecoveryTestSupport.Slot();
        var f = Fixture(factory, open(), new DiplomacyRelationRegistry(slot));
        string invitation = f.Diplomacy.Create(Request(f, "AllianceRequest", "alliance")).EventId!;
        string token = f.Issue("userB", "colonyB");
        slot.Fail = true; Fails<IOException>(() => f.Diplomacy.Respond(Response(invitation, token))); slot.Fail = false;
        // The next command must recover Ally before validating the cancellation.
        var cancellation = f.Diplomacy.Create(Request(f, "AllianceCancellation", "cancel"));
        Check(cancellation.Result.Accepted && Relation(f) == "Neutral", "New command validated against an unrecovered relation.");
        var final = f.Relations.GetRelation("userB", "colonyB", "userA", "colonyA")!;
        Check(final.SourceEventId == cancellation.EventId && final.UpdatedAtUtc > f.Ledger.Find(invitation)!.DecisionAtUtc,
            "Later relation did not retain a strictly later timestamp.");
        if (projection == "missing") slot = new RecoveryTestSupport.Slot();
        else if (projection == "administrator")
            final = f.Relations.SetRelationKind("userA", "colonyA", "userB", "colonyB", "Hostile", "administrator", final.UpdatedAtUtc.AddDays(1));
        f = Fixture(factory, open(), new DiplomacyRelationRegistry(slot)); token = f.Issue("userB", "colonyB");
        var replay = f.Diplomacy.Respond(Response(invitation, token, false));
        Check(replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
            && f.Relations.GetRelation("userA", "colonyA", "userB", "colonyB") == final, "Old retry overwrote a newer relation.");
        if (projection == "missing") Check(slot.Writes == 1, "History recovery rewrote every intermediate relation instead of the latest one.");
    }

    private static void LegacyDecision(Func<string, SaveSnapshotPackage> factory, string root, string backend, string kind, bool accepted, string receipt)
    {
        var open = Storage(root, backend); var ledger = open(); var slot = new RecoveryTestSupport.Slot();
        var f = Fixture(factory, ledger, new DiplomacyRelationRegistry(slot)); SetRelation(f, Initial(kind));
        string id = f.Diplomacy.Create(Request(f, kind)).EventId!; var at = ledger.Find(id)!.CreatedAtUtc.AddTicks(1);
        var source = accepted ? ledger.MarkAccepted(id, at, "original decision") : ledger.MarkRejected(id, at, "original decision");
        string? originalReceipt = null;
        if (receipt != "missing")
        {
            var notification = DiplomacyPolicy.CreateDiplomacyResponseNotification(source, accepted, false, at);
            notification = notification with { EventId = AuthoritativeEventFactory.BuildLegacyEventId(notification.Type, notification.IdempotencyKey),
                Payload = ((ServerNotificationEventPayload)notification.Payload) with { Title = "已保存的旧标题", Message = "original localized body",
                    RelatedAccepted = receipt == "conflict" ? !accepted : accepted } };
            ledger.Append(notification); originalReceipt = JsonSerializer.Serialize(notification);
        }
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, SnapshotSupportDeathTests.InterruptedLedger>();
        var faults = (SnapshotSupportDeathTests.InterruptedLedger)proxy; faults.Inner = open();
        f = Fixture(factory, proxy, new DiplomacyRelationRegistry(slot)); string token = f.Issue("userB", "colonyB");
        string before = Evidence(f);
        if (receipt == "conflict")
        {
            Fails<InvalidDataException>(() => f.Diplomacy.Respond(Response(id, token, !accepted)));
            Check(Evidence(f) == before, "Invalid legacy receipt changed relation or ledger before validation."); return;
        }
        if (receipt == "missing")
        {
            faults.AfterAppend = () => throw new IOException("legacy notice persisted before interruption");
            Fails<IOException>(() => f.Diplomacy.Respond(Response(id, token, !accepted))); faults.AfterAppend = null;
        }
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var replay = f.Diplomacy.Respond(Response(id, token, !accepted));
            var notice = f.Ledger.Find(replay.NotificationEventId!)!;
            Check(replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && f.Ledger.Find(id) == source
                && notice.CreatedAtUtc == at && Relation(f) == (accepted ? Final(kind) : Initial(kind)), "Legacy repair changed its first decision.");
            if (originalReceipt is not null) Check(JsonSerializer.Serialize(notice) == originalReceipt, "Recovery regenerated a persisted legacy notice.");
            Check(f.Notifications.GetVersion("userA") == 1, "Legacy repair did not recover its wake-up.");
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static AuthoritativeEvent Tombstone(EventParty party, DateTimeOffset at) => AuthoritativeEventFactory.Create(
        ServerEventType.ServerNotification, party, new("server"), ColonyTombstones.Key(party.UserId, party.ColonyId!), false,
        new ServerNotificationEventPayload(ColonyTombstones.NotificationId(party.UserId, party.ColonyId!), "Colony tombstone", "Colony instance was abandoned.",
            ServerNotificationSeverity.Info, FromAdministrator: false, OnlineOnly: true, RelatedUserId: party.UserId, RelatedColonyId: party.ColonyId), at)
        with { Status = ServerEventStatus.AppliedToSnapshot, AppliedAtUtc = at };

    private static void Abandoned(Func<string, SaveSnapshotPackage> factory, string root, string backend, bool actor, string phase)
    {
        var open = Storage(root, backend); var slot = new RecoveryTestSupport.Slot();
        var f = Fixture(factory, open(), new DiplomacyRelationRegistry(slot));
        bool decision = phase != "create";
        string kind = decision ? "AllianceRequest" : "WarDeclaration";
        string? id = decision ? f.Diplomacy.Create(Request(f, kind)).EventId : null;
        if (phase == "legacy") f.Ledger.MarkAccepted(id!, DateTimeOffset.UtcNow, "old decision without notification");
        else
        {
            slot.Fail = true;
            Fails<IOException>(() => { if (decision) f.Diplomacy.Respond(Response(id!, f.Issue("userB", "colonyB"))); else f.Diplomacy.Create(Request(f, kind)); });
            slot.Fail = false;
        }
        var source = f.Ledger.FindByIdempotencyKey("request")!;
        var party = actor ? source.Actor : source.Target;
        f.Ledger.Append(Tombstone(party, DateTimeOffset.UtcNow)); f.Relations.RemoveForColony(party.UserId, party.ColonyId!);
        f = Fixture(factory, open(), new DiplomacyRelationRegistry(slot));
        string evidence = Evidence(f);
        var replay = f.Diplomacy.Create(Request(f, kind));
        Check(replay.Result.Accepted && Evidence(f) == evidence && f.Relations.List().Count == 0
            && f.Notifications.GetVersion("userA") == 0 && f.Notifications.GetVersion("userB") == 0, "Recovery resurrected an abandoned colony's relation or notifications.");
        Check(!f.Diplomacy.Create(Request(f, kind, "new-to-deleted")).Result.Accepted && Evidence(f) == evidence,
            "New diplomacy targeted a tombstoned colony.");
        // The same account can build a different colony instance; old work must not bind to it.
        string rebuilt = party.ColonyId + "-rebuilt";
        var identity = new SnapshotIdentity(party.UserId, rebuilt, "rebuilt");
        f.Snapshots.StoreLatest(new(identity, f.Package.Envelope with { Identity = identity }, f.Package.Index, DateTimeOffset.UtcNow));
        string token = actor ? f.Issue(party.UserId, rebuilt) : f.Token;
        var fresh = f.Diplomacy.Create(new("new-colony", new("userA", actor ? rebuilt : "colonyA", actor ? "rebuilt" : "before"),
            new("userB", actor ? "colonyB" : rebuilt, actor ? "target" : "rebuilt"), "WarDeclaration", null, null, token));
        Check(fresh.Result.Accepted && f.Relations.List().Count == 1 && Relation(f) == "Neutral", "Tombstone incorrectly blocked a new colony instance or rebound old work.");
    }

    private static void OrderingAndScan(Func<string, SaveSnapshotPackage> factory)
    {
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, SnapshotSupportDeathTests.InterruptedLedger>();
        var spy = (SnapshotSupportDeathTests.InterruptedLedger)proxy; spy.Inner = new InMemoryAuthoritativeEventLedger();
        int scans = 0; spy.OnCall = name => { if (name == nameof(IAuthoritativeEventLedger.ListByType)) scans++; };
        var f = Fixture(factory, proxy); SetRelation(f, "Neutral", DateTimeOffset.UtcNow.AddDays(10));
        var future = f.Relations.List().Single().UpdatedAtUtc;
        var war = f.Diplomacy.Create(Request(f, "WarDeclaration"));
        Check(war.Result.Accepted && f.Relations.List().Single().UpdatedAtUtc == future.AddTicks(1), "Clock rollback made a new relation older than its predecessor.");
        int initialScans = scans;
        for (int i = 0; i < 5; i++) f.Diplomacy.Create(Request(f, "WarDeclaration"));
        Check(initialScans == 5 && scans == initialScans, "Publication recovery rescanned all event history on ordinary retries.");

        var ledger = new InMemoryAuthoritativeEventLedger();
        var at = DateTimeOffset.UtcNow;
        var first = AuthoritativeEventFactory.Create(ServerEventType.WarDeclaration, new("userA", "colonyA"), new("userB", "colonyB"), "war", false,
            new WarDeclarationEventPayload("war", null), at);
        var second = AuthoritativeEventFactory.Create(ServerEventType.AllianceCancellation, first.Target, first.Actor, "cancel", false,
            new AllianceCancellationEventPayload("cancel", null), at);
        ledger.Append(first); ledger.Append(second);
        var ambiguous = Fixture(factory, ledger); Fails<InvalidDataException>(() => ambiguous.DiplomacyPublications.Recover());
        Check(ambiguous.Relations.List().Count == 0, "Ambiguous legacy timestamps invented a relation order.");
        ambiguous.Relations.SetRelationKind("userA", "colonyA", "userB", "colonyB", "Neutral", second.EventId, at);
        ambiguous.DiplomacyPublications.Recover();
        Check(Relation(ambiguous) == "Neutral", "Equal-time history replaced an already-persisted projection.");
    }

    private static void CompositionDeletionOrder()
    {
        var ledger = new InMemoryAuthoritativeEventLedger(); var party = new EventParty("userB", "colonyB"); var at = DateTimeOffset.UtcNow;
        ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.WarDeclaration, new("userA", "colonyA"), party, "war", false,
            new WarDeclarationEventPayload("war", null), at));
        var live = new ClashOfRimNetworkState(ledger: ledger);
        live.SnapshotMutations.Recover();
        Check(live.DiplomacyRelations.GetRelationKind("userA", "colonyA", "userB", "colonyB") == "Hostile"
            && live.EventNotifications.GetVersion("userB") == 1, "Actual server recovery did not publish the persisted diplomacy event.");
        var deletion = new ColonyDeletionService(); var tombstone = Tombstone(party, at.AddSeconds(1));
        var intent = new ColonyDeletionIntent(1, Guid.NewGuid().ToString("N"), party.UserId, party.ColonyId!, null, tombstone.CreatedAtUtc,
            new LedgerBatch([tombstone], []), [], [party.UserId], [party.UserId]);
        Fails<IOException>(() => deletion.Commit<bool>(intent, _ => throw new IOException("pending deletion")));
        var slot = new RecoveryTestSupport.Slot { Fail = true };
        var state = new ClashOfRimNetworkState(ledger: ledger, diplomacyRelations: new DiplomacyRelationRegistry(slot), colonyDeletions: deletion);
        state.SnapshotMutations.Recover();
        Check(!deletion.HasPending && state.DiplomacyRelations.List().Count == 0 && slot.Writes == 0,
            "Composition recovered diplomacy before finishing colony deletion.");
    }

    private static void DurableRelationFailure(Func<string, SaveSnapshotPackage> factory, string root, string backend, bool decision)
    {
        if (backend == "file" && !OperatingSystem.IsWindows()) return; // File sharing denial is a Windows fault mechanism.
        string path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string relationsPath = path + ".relations.json";
        IAuthoritativeEventLedger Ledger() => backend == "sqlite" ? new SqliteAuthoritativeEventLedger(path) : new FileAuthoritativeEventLedger(path);
        DiplomacyRelationRegistry Relations() => backend == "sqlite"
            ? new(new SqliteDomainKeyedJsonRecordStore(path, SqliteDomainRegistrySchema.DiplomacyRelations), null)
            : new(relationsPath);
        var f = Fixture(factory, Ledger(), Relations()); SetRelation(f, "Neutral");
        string kind = decision ? "AllianceRequest" : "WarDeclaration";
        string? id = decision ? f.Diplomacy.Create(Request(f, kind)).EventId : null;
        string token = f.Issue("userB", "colonyB");
        void Execute() { if (decision) f.Diplomacy.Respond(Response(id!, token)); else f.Diplomacy.Create(Request(f, kind)); }
        if (backend == "sqlite")
        {
            using var connection = new SqliteConnection("Data Source=" + path); connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "create trigger fail_relation before insert on server_diplomacy_relations begin select raise(abort, 'relation failure'); end;";
            command.ExecuteNonQuery(); Fails<SqliteException>(Execute);
            command.CommandText = "drop trigger fail_relation;"; command.ExecuteNonQuery();
        }
        else
        {
            // The real persistence slot can write its candidate but cannot replace the held target.
            using var held = new FileStream(relationsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Fails<IOException>(Execute, typeof(UnauthorizedAccessException));
        }
        Check(Relation(f) == "Neutral" && Relations().GetRelationKind("userA", "colonyA", "userB", "colonyB") == "Neutral", "Failed relation write changed memory or storage.");
        f = Fixture(factory, Ledger(), Relations()); token = f.Issue("userB", "colonyB");
        var replay = decision ? f.Diplomacy.Respond(Response(id!, token, false)) : f.Diplomacy.Create(Request(f, kind));
        Check(replay.Result.Accepted && Relation(f) == Final(kind) && Relations().GetRelationKind("userA", "colonyA", "userB", "colonyB") == Final(kind),
            "Real persistent relation backend failed to recover after reopening.");
    }

    private static void Fails<T>(Action action, params Type[] alternatives) where T : Exception
    {
        try { action(); } catch (Exception error) when (error is T || alternatives.Any(type => type.IsInstanceOfType(error))) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
