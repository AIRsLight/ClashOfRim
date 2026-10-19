using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using static RaidTestPreparation;
using static RaidCreationTestClient;
using static SnapshotTestAssertions;

internal static class RaidCommitCrashTests
{
    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (bool guarded in new[] { false, true })
        {
            foreach (bool reopen in new[] { false, true })
            foreach (int fault in Enumerable.Range(0, 11).Where(f => guarded || f is not (2 or 4 or 7)))
                VerifyRecovery(fixture, guarded, reopen, fault);
            VerifyEndpoints(fixture, guarded).GetAwaiter().GetResult();
            VerifyEndpointRecovery(fixture, guarded);
        }
    }

    private static void VerifyRecovery(Func<string, SaveSnapshotPackage> fixture, bool guarded, bool reopen, int fault)
    {
        SqliteTestDirectory.With("cor-raid-commit-", root =>
        {
            string database = Path.Combine(root, "server.sqlite");
            ServerDatabaseMigrator.Migrate(database);
            var guards = OpenGuards(database);
            var guard = guarded ? SeedGuard(guards) : null;
            var ledger = new SqliteAuthoritativeEventLedger(database);
            string directory = Path.Combine(root, "snapshots"), journal = Path.Combine(root, "intents");
            var files = new FileColonySnapshotIndexStore(directory);
            var bank = new BankLoanRegistry();
            PendingBankTestDebt.AddAncillary(bank);
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
            Check(failed && Directory.EnumerateFiles(journal, "*.json").Count() == 1, "Raid interruption lost the intent.");
            if (fault is 2 or 3 or 4)
            {
                Check(ledger.ListAll().Count == 0 && new SqliteAuthoritativeEventLedger(database).ListAll().Count == 0,
                    "Failed raid batch exposed a partial event or notification.");
                if (guarded) Check(guards.FindByIdempotencyKey("guard")!.Status ==
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
                guards = OpenGuards(database);
                ledger = new SqliteAuthoritativeEventLedger(database);
                files = new FileColonySnapshotIndexStore(directory);
                commits = new SnapshotCommitService(journal);
            }
            if (fault is 7 or 8 or 9)
            {
                bool rejected = false;
                try { commits.Recover(files, bank, (_, _) => { }, ledger, contracts: contracts, guards: guards); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected && Directory.EnumerateFiles(journal, "*.json").Any()
                    && files.GetLatest("userA", "colonyA")?.Identity.SnapshotId == (fault == 8 ? "newer" : null),
                    "Raid conflict overwrote a snapshot or discarded its intent.");
                return;
            }
            int posts = 0, wakes = 0;
            for (int retry = 0; retry < 2; retry++) commits.Recover(files, bank, (_, _) => posts++, ledger, _ => wakes++,
                contracts: contracts, guards: guards);
            Check(posts == 1 && wakes == 1 && !Directory.EnumerateFiles(journal, "*.json").Any()
                && ledger.ListAll().Count == (guarded ? 2 : 1)
                && files.GetLatest("userA", "colonyA")!.Envelope.NextLineageToken == snapshot.Envelope.NextLineageToken,
                "Raid recovery duplicated or lost snapshot, events, notifications or downstream work.");
            PendingBankTestDebt.VerifyAncillary(bank, "departed");
            if (guarded) Check(OpenGuards(database).FindByIdempotencyKey("guard") ==
                MercenaryGuardContractRegistry.Consumed(guard!, raid.EventId, DateTimeOffset.UnixEpoch)
                && guards.FindActiveForColony("userB", "colonyB")?.ContractId == (fault == 10 ? "replacement" : null),
                "Raid recovery consumed a replacement or reopened the original guard.");
            if (fault == 10) Check(ledger.Find(raid.EventId)!.AppliedSnapshotId == "settled"
                && ledger.Find(raid.EventId)!.Payload is RaidEventPayload { TimeoutWork.Completed: true }
                && (notice is null || ledger.Find(notice.EventId)!.AppliedSnapshotId == "notified"), "Raid recovery reset later event application.");
        });
    }

    private static async Task VerifyEndpoints(Func<string, SaveSnapshotPackage> fixture, bool guarded)
    {
        var state = new ClashOfRimNetworkState(serverConfiguration: new ClashOfRimServerConfiguration(pvpEnabled: true));
        var package = Prepare(state.SnapshotStore, state.DiplomacyRelations, state.MercenaryGuards, fixture, guarded);
        var accepted = await Invoke(state, package, guarded);
        Check(accepted.Result.Accepted && accepted.AppliedSnapshotId == "departed" && !string.IsNullOrWhiteSpace(accepted.NextLineageToken),
            "Raid endpoint rejected: " + JsonSerializer.Serialize(accepted));
        var replay = await Invoke(state, package, guarded);
        Check(replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && replay.EventId == accepted.EventId
            && replay.NextLineageToken == accepted.NextLineageToken, "Raid replay lost the accepted identity or lineage.");
        foreach (string changed in new[] { "owner", "defender", "snapshot", "baseline", "map", "tile", "pawns", "items", "guard", "opponent" })
            Check(!(await Invoke(state, package, guarded, changed)).Result.Accepted, "Raid retry accepted changed " + changed);
        Check((await Invoke(state, package, guarded, principal: Defender)).Result.ErrorCode == ProtocolErrorCode.Unauthorized,
            "Raid endpoint accepted a mismatched multipart identity.");
        state.Ledger.MarkDelivered(accepted.EventId!, "settled", DateTimeOffset.UtcNow);
        state.Ledger.MarkApplied(accepted.EventId!, "settled", DateTimeOffset.UtcNow);
        if (guarded) SeedGuard(state.MercenaryGuards, "replacement");
        Check((await Invoke(state, package, guarded)).Result.Accepted
            && state.Ledger.Find(accepted.EventId!)!.AppliedSnapshotId == "settled"
            && (!guarded || state.MercenaryGuards.FindActiveForColony("userB", "colonyB")?.ContractId == "replacement"),
            "Raid replay reset completed work or consumed a later guard.");

        var invalid = new ClashOfRimNetworkState(serverConfiguration: new ClashOfRimServerConfiguration(pvpEnabled: true));
        var invalidPackage = Prepare(invalid.SnapshotStore, invalid.DiplomacyRelations, invalid.MercenaryGuards, fixture, guarded);
        Check(!(await Invoke(invalid, invalidPackage with { Payload = [1] }, guarded)).Result.Accepted
            && invalid.SnapshotStore.GetLatest("userA", "colonyA")!.Identity.SnapshotId == "before" && invalid.Ledger.ListAll().Count == 0,
            "Invalid raid upload published a snapshot or event.");
        if (guarded)
            Check(!(await Invoke(invalid, invalidPackage, false)).Result.Accepted
                && invalid.SnapshotStore.GetLatest("userA", "colonyA")!.Identity.SnapshotId == "before", "Omitted guard ID bypassed a defender's deployment.");

        var raced = new ClashOfRimNetworkState(serverConfiguration: new ClashOfRimServerConfiguration(pvpEnabled: true));
        var candidate = Prepare(raced.SnapshotStore, raced.DiplomacyRelations, raced.MercenaryGuards, fixture, guarded);
        string token = SessionTestAuthentication.Token(raced, "userA", "colonyA");
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(() => Invoke(raced,
            candidate with { Envelope = candidate.Envelope with { Identity = new SnapshotIdentity("userA", "colonyA", "race-" + i) } },
            guarded, key: "race-" + i, token: token))));
        Check(responses.Count(r => r.Result.Accepted) == 1 && raced.Ledger.ListByType(ServerEventType.Raid).Count == 1,
            "Concurrent raid starts duplicated the expedition.");
        if (guarded)
        {
            var sharedGuard = new ClashOfRimNetworkState(serverConfiguration: new ClashOfRimServerConfiguration(pvpEnabled: true));
            var first = Prepare(sharedGuard.SnapshotStore, sharedGuard.DiplomacyRelations, sharedGuard.MercenaryGuards, fixture, true);
            var other = new ProtocolIdentity("userC", "colonyC", "before");
            var second = Prepare(sharedGuard.SnapshotStore, sharedGuard.DiplomacyRelations, sharedGuard.MercenaryGuards, fixture, false, other);
            var competing = await Task.WhenAll(Task.Run(() => Invoke(sharedGuard, first, true, key: "first")),
                Task.Run(() => Invoke(sharedGuard, second, true, key: "second", actorOverride: other)));
            var winner = competing.Single(r => r.Result.Accepted);
            Check(sharedGuard.Ledger.ListByType(ServerEventType.Raid).Count == 1
                && sharedGuard.MercenaryGuards.FindByIdempotencyKey("guard")!.ConsumedRaidEventId == winner.EventId,
                "Two colonies consumed the same defender guard.");
            string loser = competing[0].Result.Accepted ? "userC" : "userA";
            Check(sharedGuard.SnapshotStore.GetLatest(loser, loser == "userC" ? "colonyC" : "colonyA")!.Identity.SnapshotId == "before",
                "Rejected guard competitor published its departure snapshot.");
        }
    }

    private static void VerifyEndpointRecovery(Func<string, SaveSnapshotPackage> factory, bool guarded) =>
        RaidCreationFixture.With(factory, guarded, f =>
        {
            Execute(f.Database, "create trigger fail_raid before insert on events begin select raise(abort, 'injected failure'); end;");
            bool failed = false;
            try { f.Call(); } catch (SqliteException) { failed = true; }
            Check(failed && f.Store.GetLatest("userA", "colonyA")!.Identity.SnapshotId == "departed", "Raid endpoint fault missed the commit boundary.");
            Execute(f.Database, "drop trigger fail_raid;");
            f.Open();
            var recovered = f.Call();
            Check(recovered.Result.Accepted && recovered.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
                && f.State.Ledger.ListByType(ServerEventType.Raid).Count == 1
                && (!guarded || f.State.MercenaryGuards.FindByIdempotencyKey("guard")!.ConsumedRaidEventId == recovered.EventId),
                "Raid endpoint did not recover before handling its retry.");
        });

    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
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
