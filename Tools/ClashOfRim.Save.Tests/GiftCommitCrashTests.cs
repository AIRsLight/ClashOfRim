using AIRsLight.ClashOfRim.Network.Modules.Gifts;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using System.Text.Json;

internal static class GiftCommitCrashTests
{
    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        // Interrupt before/after snapshot publication, during SQL append, wake-up and post-processing.
        // Also check recovery refuses a newer snapshot or a conflicting event key.
        foreach (int crashAt in Enumerable.Range(0, 7))
        {
            string root = Path.Combine(Path.GetTempPath(), "cor-gift-commit-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                string database = Path.Combine(root, "server.sqlite");
                ServerDatabaseMigrator.Migrate(database);
                var ledger = new SqliteAuthoritativeEventLedger(database);
                var bank = new BankLoanRegistry();
                if (crashAt == 1) PendingOperationCommitCrashTests.AddAncillary(bank);
                string snapshotDirectory = Path.Combine(root, "snapshots");
                string journalDirectory = Path.Combine(root, "intents");
                var files = new FileColonySnapshotIndexStore(snapshotDirectory);
                SaveSnapshotPackage package = fixture("gift-commit");
                LatestSnapshotRecord snapshot = new SnapshotUploadReceiver(files).Receive(
                    new SnapshotUploadContext("userA", "colonyA", "gift-commit"), package,
                    DateTimeOffset.UnixEpoch, storeAccepted: false).AcceptedSnapshot!;
                AuthoritativeEvent gift = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery,
                    new EventParty("userA", "colonyA"), new EventParty("userB", "colonyB"), "gift-key", false,
                    new ItemDeliveryEventPayload(new[] { new EventThingReference("silver", "Silver", 100, "gift-commit") }, "礼物"),
                    DateTimeOffset.UnixEpoch);
                var commits = new SnapshotCommitService(journalDirectory);
                if (crashAt == 2) Execute(database,
                    "create trigger fail_gift before insert on events begin select raise(abort, 'injected ledger failure'); end;");
                bool failed = false;
                try
                {
                    commits.CommitGift(new CrashStore(files, crashAt), bank, ledger, package, snapshot, gift,
                        (_, _) => { if (crashAt == 4) throw new IOException("injected post-upload crash"); },
                        _ => { if (crashAt == 3) throw new IOException("injected notification crash"); });
                }
                catch (IOException) { failed = true; }
                catch (SqliteException) when (crashAt == 2) { failed = true; }
                if (!failed) throw new Exception("Gift interruption was not injected.");
                if (crashAt == 2)
                {
                    if (ledger.FindByIdempotencyKey("gift-key") is not null)
                        throw new Exception("Failed append published an event.");
                    Execute(database, "drop trigger fail_gift;");
                }

                if (crashAt == 5)
                {
                    SaveSnapshotPackage newer = fixture("newer");
                    files.StoreLatest(newer, newer.Index, DateTimeOffset.UtcNow);
                }
                if (crashAt == 6)
                    ledger.Append(gift with { EventId = "other-event", Actor = new EventParty("other-user", "other-colony") });

                // Reopen all persistent stores, retaining only the durable journal.
                ledger = new SqliteAuthoritativeEventLedger(database);
                files = new FileColonySnapshotIndexStore(snapshotDirectory);
                commits = new SnapshotCommitService(journalDirectory);
                int processed = 0, signaled = 0;
                if (crashAt >= 5)
                {
                    bool rejected = false;
                    try { commits.Recover(files, bank, (_, _) => processed++, ledger); }
                    catch (InvalidDataException) { rejected = true; }
                    if (!rejected || !Directory.EnumerateFiles(journalDirectory, "*.json").Any()
                        || crashAt == 5 && files.GetLatest("userA", "colonyA")?.Identity.SnapshotId != "newer"
                        || crashAt == 6 && files.GetLatest("userA", "colonyA") is not null)
                        throw new Exception("Conflicting recovery must preserve the journal and current snapshot.");
                    continue;
                }
                commits.Recover(files, bank, (_, _) => processed++, ledger, _ => signaled++);
                commits.Recover(files, bank, (_, _) => processed++, ledger, _ => signaled++);
                AuthoritativeEvent? recovered = ledger.FindByIdempotencyKey("gift-key");
                if (crashAt == 1) PendingOperationCommitCrashTests.VerifyAncillary(bank, "gift-commit");
                if (recovered?.EventId != gift.EventId || recovered.Target != gift.Target
                    || recovered.Payload is not ItemDeliveryEventPayload { Message: "礼物" } payload
                    || payload.Items.Single().StackCount != 100 || ledger.ListAll().Count != 1
                    || !GiftSnapshotReplayPolicy.Matches(recovered, "userA", "colonyA", "gift-commit")
                    || GiftSnapshotReplayPolicy.Matches(recovered, "other-user", "colonyA", "gift-commit")
                    || GiftSnapshotReplayPolicy.Matches(recovered, "userA", "colonyA", "other-snapshot")
                    || GiftSnapshotReplayPolicy.Matches(recovered with { Payload = payload with { Purpose = ItemDeliveryPurpose.TradeCancelledOwnerReturn } }, "userA", "colonyA", "gift-commit")
                    || files.GetLatest("userA", "colonyA")?.Envelope.NextLineageToken != snapshot.Envelope.NextLineageToken
                    || !files.GetLatestPackage("userA", "colonyA")!.Payload.SequenceEqual(package.Payload)
                    || processed != 1 || signaled != 1 || Directory.EnumerateFiles(journalDirectory, "*.json").Any())
                    throw new Exception("Gift recovery was incomplete or duplicated a delivery.");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
        VerifyEndpoint(fixture).GetAwaiter().GetResult();
    }

    private static async Task VerifyEndpoint(Func<string, SaveSnapshotPackage> fixture)
    {
        ServerLocalization.MergeExternal(ServerLocalization.English,
            JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestLocalization", "English.json")))!);
        var state = new ClashOfRimNetworkState();
        SaveSnapshotPackage package = fixture("gift-endpoint");
        var actor = new ProtocolIdentity("userA", "colonyA", "before");
        var target = new ProtocolIdentity("userB", "colonyB", null);
        CreateGiftWithSnapshotRequest Request(ProtocolIdentity sender, ProtocolIdentity recipient, string key = "endpoint-gift")
            => new(new CreateGiftRequest(key, sender, recipient,
                new[] { new ThingReferenceDto("Silver_1", "Silver", 100) }, "礼物"), ProtocolDtoMapper.ToMetadataDto(package));
        EventCreationResponse created = await Invoke(state, Request(actor, target), package.Payload, actor);
        if (!created.Result.Accepted || state.SnapshotStore.GetLatest("userA", "colonyA")?.Identity.SnapshotId != "gift-endpoint")
            throw new Exception("Gift endpoint did not commit its confirmed snapshot: " + JsonSerializer.Serialize(created));
        EventCreationResponse replay = await Invoke(state, Request(actor, target), package.Payload, actor);
        if (!replay.Result.Accepted || replay.Result.ErrorCode != ProtocolErrorCode.DuplicateRequest
            || replay.EventId != created.EventId || state.Ledger.ListByType(ServerEventType.ItemDelivery).Count != 1)
            throw new Exception("Gift endpoint retry created another delivery.");
        var other = new ProtocolIdentity("other", "other-colony", null);
        EventCreationResponse wrongTarget = await Invoke(state, Request(actor, other), package.Payload, actor);
        EventCreationResponse wrongOwner = await Invoke(state, Request(other, target), package.Payload, other);
        EventCreationResponse forgedOwner = await Invoke(state, Request(actor, target), package.Payload, other);
        if (wrongTarget.Result.ErrorCode != ProtocolErrorCode.Conflict || wrongOwner.Result.ErrorCode != ProtocolErrorCode.Conflict
            || forgedOwner.Result.ErrorCode != ProtocolErrorCode.Unauthorized)
            throw new Exception("Gift endpoint replay did not validate principal, sender and recipient.");

        var rejectedState = new ClashOfRimNetworkState();
        EventCreationResponse invalid = await Invoke(rejectedState, Request(actor, target), new byte[] { 1 }, actor);
        if (invalid.Result.Accepted || rejectedState.Ledger.ListAll().Count != 0
            || rejectedState.SnapshotStore.GetLatest("userA", "colonyA") is not null)
            throw new Exception("Invalid gift payload published business state.");
    }

    private static Task<EventCreationResponse> Invoke(ClashOfRimNetworkState state,
        CreateGiftWithSnapshotRequest request, byte[] payload, ProtocolIdentity principal)
        => SnapshotEndpointTestClient.Invoke<EventCreationResponse>(nameof(GiftEndpoints.CreateGiftWithSnapshot),
            state, request, payload, principal);

    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class CrashStore(IColonySnapshotPackageStore inner, int crashAt) : IColonySnapshotPackageStore
    {
        public void StoreLatest(SaveSnapshotPackage package, SaveSnapshotIndex index, DateTimeOffset at)
        {
            if (crashAt is 0 or 5 or 6) throw new IOException("injected pre-snapshot crash");
            inner.StoreLatest(package, index, at);
            if (crashAt == 1) throw new IOException("injected post-snapshot crash");
        }
        public void StoreLatest(LatestSnapshotRecord snapshot) => throw new NotSupportedException();
        public LatestSnapshotRecord? GetLatest(string owner, string colony) => inner.GetLatest(owner, colony);
        public SaveSnapshotPackage? GetLatestPackage(string owner, string colony) => inner.GetLatestPackage(owner, colony);
        public IReadOnlyList<LatestSnapshotRecord> ListLatest() => inner.ListLatest();
        public bool RemoveLatest(string owner, string colony) => inner.RemoveLatest(owner, colony);
    }
}
