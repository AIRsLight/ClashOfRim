using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

internal static class RaidCommitCrashTests
{
    private static readonly ProtocolIdentity Attacker = new("userA", "colonyA", "before");
    private static readonly ProtocolIdentity Defender = new("userB", "colonyB", "defender");
    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        foreach (bool guarded in new[] { false, true })
        {
            foreach (bool reopen in new[] { false, true })
            foreach (int fault in Enumerable.Range(0, 11).Where(f => guarded || f is not (2 or 4 or 7)))
                VerifyRecovery(fixture, guarded, reopen, fault);
            VerifyEndpoints(fixture, guarded).GetAwaiter().GetResult();
            VerifyEndpointRecovery(fixture, guarded).GetAwaiter().GetResult();
        }
    }

    private static void VerifyRecovery(Func<string, SaveSnapshotPackage> fixture, bool guarded, bool reopen, int fault)
    {
        WithStorage((database, root) =>
        {
            var guards = Guards(database);
            var guard = guarded ? SeedGuard(guards) : null;
            var ledger = new SqliteAuthoritativeEventLedger(database);
            string directory = Path.Combine(root, "snapshots"), journal = Path.Combine(root, "intents");
            var files = new FileColonySnapshotIndexStore(directory);
            var bank = new BankLoanRegistry();
            PendingOperationCommitCrashTests.AddAncillary(bank);
            var contracts = new MercenaryContractRegistry();
            var package = fixture("departed");
            var snapshot = new SnapshotUploadReceiver(files).Receive(new SnapshotUploadContext("userA", "colonyA", "departed"),
                package, DateTimeOffset.UnixEpoch, storeAccepted: false).AcceptedSnapshot!;
            var raid = AuthoritativeEventFactory.Create(ServerEventType.Raid, new EventParty("userA", "colonyA"),
                new EventParty("userB", "colonyB"), "raid", false,
                new RaidEventPayload("defender", null, DateTimeOffset.UnixEpoch, null, null,
                    new RaidAttackForceRecord("departed", ["pawn"], []), GuardDeploymentId: guard?.ContractId), DateTimeOffset.UnixEpoch,
                new EventTargetContext("world", "0", 1, EventLandingMode.MapEdge));
            var notice = MercenaryGuardActivationNotificationFactory.Create(guard, raid, false, "Guard", "Deployed", DateTimeOffset.UnixEpoch);
            var mutation = new RaidSnapshotMutation(raid, guard, notice);
            var commits = new SnapshotCommitService(journal);
            if (fault is 2 or 3 or 4) Execute(database, $"create trigger fail_raid before insert on {(fault == 2 ? "server_mercenary_guard_contracts" : "events")} "
                + (fault == 4 ? "when NEW.idempotency_key like 'mercenary-guard-activated:%' " : "")
                + "begin select raise(abort, 'injected failure'); end;");
            bool failed = false;
            try
            {
                commits.CommitRaid(new CrashStore(files, fault), bank, ledger, contracts, guards, package, snapshot, mutation,
                    (_, _) => { if (fault is 6 or 10) throw new IOException("post-upload"); },
                    _ => { if (fault == 5) throw new IOException("signal"); });
            }
            catch (IOException) { failed = true; }
            catch (SqliteException) { failed = true; }
            Require(failed && Directory.EnumerateFiles(journal, "*.json").Count() == 1, "Raid interruption lost the intent.");
            if (fault is 2 or 3 or 4)
            {
                Require(ledger.ListAll().Count == 0 && new SqliteAuthoritativeEventLedger(database).ListAll().Count == 0,
                    "Failed raid batch exposed a partial event or notification.");
                if (guarded) Require(guards.FindByIdempotencyKey("guard")!.Status ==
                    (fault == 2 ? MercenaryGuardContractRegistry.StatusActive : MercenaryGuardContractRegistry.StatusConsumed),
                    "Guard cache did not reflect the persisted boundary.");
                Execute(database, "drop trigger fail_raid;");
            }
            if (fault == 7) guards.ConsumeForRaid("userB", "colonyB", "other-raid", "guard", DateTimeOffset.UnixEpoch);
            if (fault == 8)
            {
                var newer = fixture("newer");
                files.StoreLatest(newer, newer.Index, DateTimeOffset.UtcNow);
            }
            if (fault == 9) ledger.Append(raid with { EventId = "collision", Target = new EventParty("other", "other-colony") });
            if (fault == 10)
            {
                ledger.MarkDelivered(raid.EventId, "settled", DateTimeOffset.UnixEpoch.AddHours(1));
                ledger.MarkApplied(raid.EventId, "settled", DateTimeOffset.UnixEpoch.AddHours(1));
                var advancedRaid = ledger.Find(raid.EventId)!;
                ledger.ApplyBatch(new LedgerBatch([], [new LedgerStatusChange(advancedRaid.EventId, advancedRaid.Status, advancedRaid.Status,
                    advancedRaid.Payload, ((RaidEventPayload)advancedRaid.Payload) with
                        { TimeoutWork = new RaidTimeoutWork(DateTimeOffset.UnixEpoch.AddMinutes(30), true, Completed: true) })]));
                if (notice is not null)
                {
                    ledger.MarkDelivered(notice.EventId, "notified", DateTimeOffset.UnixEpoch.AddHours(1));
                    ledger.MarkApplied(notice.EventId, "notified", DateTimeOffset.UnixEpoch.AddHours(1));
                }
                if (guarded) SeedGuard(guards, "replacement");
            }
            if (reopen)
            {
                guards = Guards(database);
                ledger = new SqliteAuthoritativeEventLedger(database);
                files = new FileColonySnapshotIndexStore(directory);
                commits = new SnapshotCommitService(journal);
            }
            if (fault is 7 or 8 or 9)
            {
                bool rejected = false;
                try { commits.Recover(files, bank, (_, _) => { }, ledger, contracts: contracts, guards: guards); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected && Directory.EnumerateFiles(journal, "*.json").Any()
                    && files.GetLatest("userA", "colonyA")?.Identity.SnapshotId == (fault == 8 ? "newer" : null),
                    "Raid conflict overwrote a snapshot or discarded its intent.");
                return;
            }
            int posts = 0, wakes = 0;
            for (int retry = 0; retry < 2; retry++) commits.Recover(files, bank, (_, _) => posts++, ledger, _ => wakes++,
                contracts: contracts, guards: guards);
            Require(posts == 1 && wakes == 1 && !Directory.EnumerateFiles(journal, "*.json").Any()
                && ledger.ListAll().Count == (guarded ? 2 : 1)
                && files.GetLatest("userA", "colonyA")!.Envelope.NextLineageToken == snapshot.Envelope.NextLineageToken,
                "Raid recovery duplicated or lost snapshot, events, notifications or downstream work.");
            PendingOperationCommitCrashTests.VerifyAncillary(bank, "departed");
            if (guarded) Require(Guards(database).FindByIdempotencyKey("guard") ==
                MercenaryGuardContractRegistry.Consumed(guard!, raid.EventId, DateTimeOffset.UnixEpoch)
                && guards.FindActiveForColony("userB", "colonyB")?.ContractId == (fault == 10 ? "replacement" : null),
                "Raid recovery consumed a replacement or reopened the original guard.");
            if (fault == 10) Require(ledger.Find(raid.EventId)!.AppliedSnapshotId == "settled"
                && ledger.Find(raid.EventId)!.Payload is RaidEventPayload { TimeoutWork.Completed: true }
                && (notice is null || ledger.Find(notice.EventId)!.AppliedSnapshotId == "notified"), "Raid recovery reset later event application.");
        });
    }

    private static async Task VerifyEndpoints(Func<string, SaveSnapshotPackage> fixture, bool guarded)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var state = new ClashOfRimNetworkState(serverConfiguration: new ClashOfRimServerConfiguration(pvpEnabled: true));
        var package = Setup(state, fixture, guarded);
        var accepted = await Invoke(state, package, guarded);
        Require(accepted.Result.Accepted && accepted.AppliedSnapshotId == "departed" && !string.IsNullOrWhiteSpace(accepted.NextLineageToken),
            "Raid endpoint rejected: " + JsonSerializer.Serialize(accepted));
        var replay = await Invoke(state, package, guarded);
        Require(replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && replay.EventId == accepted.EventId
            && replay.NextLineageToken == accepted.NextLineageToken, "Raid replay lost the accepted identity or lineage.");
        foreach (string changed in new[] { "owner", "defender", "snapshot", "baseline", "map", "tile", "pawns", "items", "guard", "opponent" })
            Require(!(await Invoke(state, package, guarded, changed)).Result.Accepted, "Raid retry accepted changed " + changed);
        Require((await Invoke(state, package, guarded, principal: Defender)).Result.ErrorCode == ProtocolErrorCode.Unauthorized,
            "Raid endpoint accepted a mismatched multipart identity.");
        state.Ledger.MarkDelivered(accepted.EventId!, "settled", DateTimeOffset.UtcNow);
        state.Ledger.MarkApplied(accepted.EventId!, "settled", DateTimeOffset.UtcNow);
        if (guarded) SeedGuard(state.MercenaryGuards, "replacement");
        Require((await Invoke(state, package, guarded)).Result.Accepted
            && state.Ledger.Find(accepted.EventId!)!.AppliedSnapshotId == "settled"
            && (!guarded || state.MercenaryGuards.FindActiveForColony("userB", "colonyB")?.ContractId == "replacement"),
            "Raid replay reset completed work or consumed a later guard.");

        var invalid = new ClashOfRimNetworkState(serverConfiguration: new ClashOfRimServerConfiguration(pvpEnabled: true));
        var invalidPackage = Setup(invalid, fixture, guarded);
        Require(!(await Invoke(invalid, invalidPackage with { Payload = [1] }, guarded)).Result.Accepted
            && invalid.SnapshotStore.GetLatest("userA", "colonyA")!.Identity.SnapshotId == "before" && invalid.Ledger.ListAll().Count == 0,
            "Invalid raid upload published a snapshot or event.");
        if (guarded)
            Require(!(await Invoke(invalid, invalidPackage, false)).Result.Accepted
                && invalid.SnapshotStore.GetLatest("userA", "colonyA")!.Identity.SnapshotId == "before", "Omitted guard ID bypassed a defender's deployment.");

        var raced = new ClashOfRimNetworkState(serverConfiguration: new ClashOfRimServerConfiguration(pvpEnabled: true));
        var candidate = Setup(raced, fixture, guarded);
        raced.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out string session);
        string token = raced.AuthTokens.Issue("userA", "userA", "colonyA", session, DateTimeOffset.UtcNow);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(() => Invoke(raced,
            candidate with { Envelope = candidate.Envelope with { Identity = new SnapshotIdentity("userA", "colonyA", "race-" + i) } },
            guarded, key: "race-" + i, token: token))));
        Require(responses.Count(r => r.Result.Accepted) == 1 && raced.Ledger.ListByType(ServerEventType.Raid).Count == 1,
            "Concurrent raid starts duplicated the expedition.");
        if (guarded)
        {
            var sharedGuard = new ClashOfRimNetworkState(serverConfiguration: new ClashOfRimServerConfiguration(pvpEnabled: true));
            var first = Setup(sharedGuard, fixture, true);
            var other = new ProtocolIdentity("userC", "colonyC", "before");
            var second = Setup(sharedGuard, fixture, false, other);
            var competing = await Task.WhenAll(Task.Run(() => Invoke(sharedGuard, first, true, key: "first")),
                Task.Run(() => Invoke(sharedGuard, second, true, key: "second", actorOverride: other)));
            var winner = competing.Single(r => r.Result.Accepted);
            Require(sharedGuard.Ledger.ListByType(ServerEventType.Raid).Count == 1
                && sharedGuard.MercenaryGuards.FindByIdempotencyKey("guard")!.ConsumedRaidEventId == winner.EventId,
                "Two colonies consumed the same defender guard.");
            string loser = competing[0].Result.Accepted ? "userC" : "userA";
            Require(sharedGuard.SnapshotStore.GetLatest(loser, loser == "userC" ? "colonyC" : "colonyA")!.Identity.SnapshotId == "before",
                "Rejected guard competitor published its departure snapshot.");
        }
    }

    private static async Task VerifyEndpointRecovery(Func<string, SaveSnapshotPackage> fixture, bool guarded)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-raid-endpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string database = Path.Combine(root, "server.sqlite");
            ServerDatabaseMigrator.Migrate(database);
            ClashOfRimNetworkState Open() => new(snapshotStore: new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots")),
                ledger: new SqliteAuthoritativeEventLedger(database), mercenaryGuards: Guards(database),
                snapshotCommits: new SnapshotCommitService(Path.Combine(root, "intents")),
                serverConfiguration: new ClashOfRimServerConfiguration(pvpEnabled: true));
            var state = Open();
            var package = Setup(state, fixture, guarded);
            Execute(database, "create trigger fail_raid before insert on events begin select raise(abort, 'injected failure'); end;");
            bool failed = false;
            try { await Invoke(state, package, guarded); } catch (SqliteException) { failed = true; }
            Require(failed && state.SnapshotStore.GetLatest("userA", "colonyA")!.Identity.SnapshotId == "departed", "Raid endpoint fault missed the commit boundary.");
            Execute(database, "drop trigger fail_raid;");
            state = Open();
            var recovered = await Invoke(state, package, guarded);
            Require(recovered.Result.Accepted && recovered.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
                && state.Ledger.ListByType(ServerEventType.Raid).Count == 1
                && (!guarded || state.MercenaryGuards.FindByIdempotencyKey("guard")!.ConsumedRaidEventId == recovered.EventId),
                "Raid endpoint did not recover before handling its retry.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static Task<EventCreationResponse> Invoke(ClashOfRimNetworkState state, SaveSnapshotPackage package, bool guarded,
        string? changed = null, ProtocolIdentity? principal = null, string key = "raid", string? token = null, ProtocolIdentity? actorOverride = null)
    {
        var actor = actorOverride ?? (changed == "owner" ? new ProtocolIdentity("other", "other-colony", "before") : Attacker);
        var target = changed == "defender" ? new ProtocolIdentity("other", "other-colony", "defender") : Defender;
        var metadata = ProtocolDtoMapper.ToMetadataDto(changed == "snapshot" ? package with
            { Envelope = package.Envelope with { Identity = new SnapshotIdentity("userA", "colonyA", "changed") } } : package);
        var raid = new CreateRaidRequest(key, actor, target, true, false, 100000, null, null, "world",
            changed == "map" ? "other-map" : "0", changed == "baseline" ? "changed" : "defender",
            changed == "pawns" ? ["other-pawn"] : ["pawn"], changed == "items" ? [new ThingReferenceDto("item", "Silver", 1)] : [],
            changed == "tile" ? 999 : 1, changed == "opponent" ? "VanillaNpc" : "Player",
            changed == "guard" ? "changed-guard" : guarded ? "guard" : null);
        return SnapshotEndpointTestClient.Invoke<EventCreationResponse>("CreateRaidWithSnapshot", state,
            _ => new CreateRaidWithSnapshotRequest(raid, metadata), package.Payload, principal ?? actor, token);
    }

    private static SaveSnapshotPackage Setup(ClashOfRimNetworkState state, Func<string, SaveSnapshotPackage> fixture, bool guarded,
        ProtocolIdentity? actor = null)
    {
        actor ??= Attacker;
        var baseline = fixture("before");
        baseline = baseline with { Envelope = baseline.Envelope with { Identity = new SnapshotIdentity(actor.UserId, actor.ColonyId!, "before") } };
        var accepted = new SnapshotUploadReceiver(state.SnapshotStore).Receive(new SnapshotUploadContext(actor.UserId, actor.ColonyId!, "before"), baseline, DateTimeOffset.UtcNow).AcceptedSnapshot!;
        var defender = fixture("defender");
        defender = defender with { Envelope = defender.Envelope with { Identity = new SnapshotIdentity("userB", "colonyB", "defender") } };
        new SnapshotUploadReceiver(state.SnapshotStore).Receive(new SnapshotUploadContext("userB", "colonyB", "defender"), defender, DateTimeOffset.UtcNow);
        state.DiplomacyRelations.SetRelationKind(actor.UserId, actor.ColonyId!, "userB", "colonyB", DiplomacyRelationRegistry.RelationHostile, null, DateTimeOffset.UtcNow);
        if (guarded) SeedGuard(state.MercenaryGuards);
        using var compressed = new MemoryStream(baseline.Payload);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        var document = XDocument.Load(gzip);
        document.Root!.Element("game")!.Add(new XElement("components", new XElement("li",
            new XAttribute("Class", "AIRsLight.ClashOfRim.ClashOfRimGameComponent"),
            new XElement("clashOfRimLineageSnapshotId", "before"), new XElement("clashOfRimLineageToken", accepted.Envelope.NextLineageToken))));
        string path = Path.Combine(Path.GetTempPath(), "cor-raid-child-" + Guid.NewGuid().ToString("N") + ".rws");
        try { document.Save(path); return SaveSnapshotPackageBuilder.FromFile(path, new SnapshotIdentity(actor.UserId, actor.ColonyId!, "departed"), DateTimeOffset.UtcNow); }
        finally { File.Delete(path); }
    }
    private static MercenaryGuardContractRecord SeedGuard(MercenaryGuardContractRegistry guards, string key = "guard") =>
        guards.ActivateWithSnapshot(key, "userB", "colonyB", key, "defender", "Apprentice", 600, 0.35f, 1000, DateTimeOffset.UnixEpoch)!;
    private static MercenaryGuardContractRegistry Guards(string database) => new(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.MercenaryGuards), null);
    private static void WithStorage(Action<string, string> run)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-raid-commit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { string database = Path.Combine(root, "server.sqlite"); ServerDatabaseMigrator.Migrate(database); run(database, root); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class CrashStore(IColonySnapshotPackageStore inner, int fault) : IColonySnapshotPackageStore
    {
        public void StoreLatest(SaveSnapshotPackage package, SaveSnapshotIndex index, DateTimeOffset at)
        {
            if (fault is 0 or 7 or 8 or 9) throw new IOException("before snapshot");
            inner.StoreLatest(package, index, at);
            if (fault == 1) throw new IOException("after snapshot");
        }
        public void StoreLatest(LatestSnapshotRecord snapshot) => throw new NotSupportedException();
        public LatestSnapshotRecord? GetLatest(string owner, string colony) => inner.GetLatest(owner, colony);
        public SaveSnapshotPackage? GetLatestPackage(string owner, string colony) => inner.GetLatestPackage(owner, colony);
        public IReadOnlyList<LatestSnapshotRecord> ListLatest() => inner.ListLatest();
        public bool RemoveLatest(string owner, string colony) => inner.RemoveLatest(owner, colony);
    }
}
