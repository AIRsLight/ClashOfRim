using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class TradeCommitCrashTests
{
    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        foreach (int fault in Enumerable.Range(0, 7))
        {
            string root = Path.Combine(Path.GetTempPath(), "cor-trade-commit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string database = Path.Combine(root, "server.sqlite");
                ServerDatabaseMigrator.Migrate(database);
                var ledger = new SqliteAuthoritativeEventLedger(database);
                AuthoritativeEvent order = SeedOrder(ledger);
                var files = new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots"));
                var bank = new BankLoanRegistry();
                if (fault == 1) PendingBankTestDebt.AddAncillary(bank);
                SaveSnapshotPackage package = fixture("trade-commit");
                LatestSnapshotRecord snapshot = new SnapshotUploadReceiver(files).Receive(
                    new SnapshotUploadContext("userA", "colonyA", "trade-commit"), package,
                    DateTimeOffset.UtcNow, storeAccepted: false).AcceptedSnapshot!;
                AuthoritativeEvent exchange = AuthoritativeEventFactory.Create(ServerEventType.Trade,
                    new EventParty("userA", "colonyA"), order.Actor, "exchange", false,
                    new TradeEventPayload(order.EventId, TradeStage.ServerDropPodExchange,
                        ((TradeEventPayload)order.Payload).OfferedItems, new[] { new EventThingReference("silver", "Silver", 20, "trade-commit") },
                        0, FulfillmentMode: TradeFulfillmentMode.ServerDropPod), DateTimeOffset.UtcNow)
                    with { Status = ServerEventStatus.AppliedToSnapshot, AppliedSnapshotId = "trade-commit" };
                AuthoritativeEvent Delivery(string key, EventParty target) => AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery,
                    exchange.Actor, target, key, false, new ItemDeliveryEventPayload(new[] { new EventThingReference(key, "Silver", 10, "trade-commit") }, null,
                        Purpose: ItemDeliveryPurpose.TradeCompletedOwnerDelivery), DateTimeOffset.UtcNow);
                var mutation = new TradeSnapshotMutation(new LedgerBatch(new[] { exchange, Delivery("owner", order.Actor), Delivery("buyer", exchange.Actor) },
                    new[] { new LedgerStatusChange(order.EventId, order.Status, ServerEventStatus.AppliedToSnapshot) }), new[] { "userA", "userB" });
                var commits = new SnapshotCommitService(Path.Combine(root, "intents"));
                if (fault == 2) Execute(database, "create trigger fail_trade before update on events begin select raise(abort, 'injected batch failure'); end;");
                bool failed = false;
                try
                {
                    commits.CommitTrade(new CrashStore(files, fault), bank, ledger, package, snapshot, mutation,
                        (_, _) => { if (fault == 5) throw new IOException("post-upload"); },
                        _ => { if (fault == 4) throw new IOException("signal"); },
                        _ => { if (fault == 3) throw new IOException("achievement"); });
                }
                catch (IOException) { failed = true; }
                catch (SqliteException) when (fault == 2) { failed = true; }
                Require(failed, "Fault was not injected.");
                if (fault == 2)
                {
                    Require(ledger.ListAll().Count == 1 && ledger.Find(order.EventId)!.Status == order.Status,
                        "Failed batch leaked deliveries or closed the order.");
                    Execute(database, "drop trigger fail_trade;");
                }
                if (fault == 6) ledger.ChangeStatus(order.EventId, ServerEventStatus.Cancelled);
                ledger = new SqliteAuthoritativeEventLedger(database);
                files = new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots"));
                commits = new SnapshotCommitService(Path.Combine(root, "intents"));
                if (fault == 6)
                {
                    bool rejected = false;
                    try { commits.Recover(files, bank, (_, _) => { }, ledger); }
                    catch (InvalidOperationException) { rejected = true; }
                    Require(rejected && files.GetLatest("userA", "colonyA") is null
                        && Directory.EnumerateFiles(Path.Combine(root, "intents"), "*.json").Any(), "Changed order was overwritten during recovery.");
                    continue;
                }
                int processed = 0, signaled = 0, recorded = 0;
                commits.Recover(files, bank, (_, _) => processed++, ledger, _ => signaled++, _ => recorded++);
                commits.Recover(files, bank, (_, _) => processed++, ledger, _ => signaled++, _ => recorded++);
                Require(ledger.ListAll().Count == 4 && ledger.Find(order.EventId)!.Status == ServerEventStatus.AppliedToSnapshot
                    && ledger.Find(exchange.EventId)!.AppliedSnapshotId == "trade-commit" && processed == 1 && signaled == 2 && recorded == 1
                    && files.GetLatest("userA", "colonyA")!.Envelope.NextLineageToken == snapshot.Envelope.NextLineageToken
                    && !Directory.EnumerateFiles(Path.Combine(root, "intents"), "*.json").Any(), "Trade recovery was incomplete or duplicated events.");
                if (fault == 1) PendingBankTestDebt.VerifyAncillary(bank, "trade-commit");
            }
            finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
        }
        VerifyEndpoints(fixture).GetAwaiter().GetResult();
        VerifyEndpointStorageFailures(fixture).GetAwaiter().GetResult();
    }

    // Replaces the retired JSON fulfillment's SQL tests with the real snapshot transaction and restart recovery.
    private static async Task VerifyEndpointStorageFailures(Func<string, SaveSnapshotPackage> fixture)
    {
        foreach (string phase in new[] { "insert", "update" })
        foreach (string mode in new[] { "SelfDelivery", "ServerDropPod", "DirectDropPod" })
        {
            string root = Path.Combine(Path.GetTempPath(), "cor-trade-endpoint-fault-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string database = Path.Combine(root, "server.sqlite"), intents = Path.Combine(root, "intents");
                ServerDatabaseMigrator.Migrate(database);
                ClashOfRimNetworkState Open() => new(ledger: new SqliteAuthoritativeEventLedger(database),
                    snapshotStore: new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots")), snapshotCommits: new SnapshotCommitService(intents));
                var state = Open(); var order = SeedOrder(state.Ledger);
                var buyer = new ProtocolIdentity("userA", "colonyA", "before");
                string memoId = "drop-pod-direct:" + order.EventId;
                if (mode != "DirectDropPod")
                {
                    var memo = AuthoritativeEventFactory.Create(ServerEventType.Trade, new EventParty("userA", "colonyA"),
                        new EventParty("userA", "colonyA"), "memo", false, ((TradeEventPayload)order.Payload) with
                        { TradeId = order.EventId, Stage = TradeStage.AcceptedMemo, AcceptedByUserId = "userA" }, DateTimeOffset.UtcNow)
                        with { Status = ServerEventStatus.Recorded };
                    state.Ledger.Append(memo); memoId = memo.EventId;
                }
                var package = fixture("trade-endpoint-fault");
                var request = new FulfillTradeOrderWithSnapshotRequest(new FulfillTradeOrderRequest("fulfill", order.EventId, memoId,
                    buyer, new[] { new ThingReferenceDto("paid", "Silver", 20) }, mode == "SelfDelivery" ? mode : "ServerDropPod"), ProtocolDtoMapper.ToMetadataDto(package));
                string before = System.Text.Json.JsonSerializer.Serialize(state.Ledger.ListAll());
                Execute(database, $"create trigger fail_trade before {phase} on events begin select raise(abort, 'injected endpoint batch failure'); end;");
                bool failed = false;
                try { await SnapshotEndpointTestClient.Invoke<FulfillTradeOrderResponse>("FulfillTradeOrderWithSnapshot", state, request, package.Payload, buyer); }
                catch (SqliteException) { failed = true; }
                Require(failed && System.Text.Json.JsonSerializer.Serialize(Open().Ledger.ListAll()) == before,
                    "Real fulfillment did not roll back the complete ledger batch: " + mode + "/" + phase);
                Require(Directory.EnumerateFiles(intents, "*.json").Any(), "Failed fulfillment lost its durable intent.");
                Execute(database, "drop trigger fail_trade;");
                state = Open();
                var recovered = await SnapshotEndpointTestClient.Invoke<FulfillTradeOrderResponse>("FulfillTradeOrderWithSnapshot", state, request, package.Payload, buyer);
                Require(recovered.Result.Accepted && recovered.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && !recovered.ExchangeCreated
                    && recovered.AppliedSnapshotId == "trade-endpoint-fault"
                    && state.SnapshotStore.GetLatest("userA", "colonyA")!.Identity.SnapshotId == recovered.AppliedSnapshotId
                    && state.Ledger.Find(order.EventId)!.Status == ServerEventStatus.AppliedToSnapshot
                    && state.Ledger.Find(recovered.ExchangeEventId!)!.AppliedSnapshotId == recovered.AppliedSnapshotId
                    && state.Ledger.ListByType(ServerEventType.ItemDelivery).Count == (mode == "SelfDelivery" ? 1 : 2)
                    && !Directory.EnumerateFiles(intents, "*.json").Any(), "Restart did not recover exactly one complete fulfillment.");
                string committed = System.Text.Json.JsonSerializer.Serialize(state.Ledger.ListAll());
                state = Open();
                var replay = await SnapshotEndpointTestClient.Invoke<FulfillTradeOrderResponse>("FulfillTradeOrderWithSnapshot", state, request, package.Payload, buyer);
                Require(replay.Result.Accepted && replay.ExchangeEventId == recovered.ExchangeEventId && replay.OwnerDeliveryEventId == recovered.OwnerDeliveryEventId
                    && replay.AcceptorDeliveryEventId == recovered.AcceptorDeliveryEventId && committed == System.Text.Json.JsonSerializer.Serialize(state.Ledger.ListAll()),
                    "Second restart/replay changed recovered fulfillment.");
            }
            finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
        }
    }

    private static AuthoritativeEvent SeedOrder(IAuthoritativeEventLedger ledger)
    {
        AuthoritativeEvent order = AuthoritativeEventFactory.Create(ServerEventType.Trade,
            new EventParty("userB", "colonyB"), new EventParty("server"), "order", false,
            new TradeEventPayload("order", TradeStage.MarketOrder,
                new[] { new EventThingReference("offer", "Silver", 10, "seller-snapshot") },
                new[] { new EventThingReference("request", "Silver", 20, "seller-snapshot") }, 0), DateTimeOffset.UtcNow);
        ledger.Append(order);
        return order;
    }

    private static async Task VerifyEndpoints(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        SaveSnapshotPackage package = fixture("trade-endpoint");
        var actor = new ProtocolIdentity("userA", "colonyA", "before");
        var state = new ClashOfRimNetworkState(serverConfiguration: new ClashOfRimServerConfiguration(
            tradeFeePolicy: new TradeFeePolicy(standardMarketValuePerThing: new Dictionary<string, float> { ["Silver"] = 1 })));
        // A buy-only order still needs a stable confirmed snapshot for retries.
        var create = new CreateTradeOrderWithSnapshotRequest(new CreateTradeOrderRequest("create", actor,
            Array.Empty<ThingReferenceDto>(), new[] { new ThingReferenceDto("wanted", "Silver", 10) }, 100, true, true), ProtocolDtoMapper.ToMetadataDto(package));
        EventCreationResponse created = await SnapshotEndpointTestClient.Invoke<EventCreationResponse>("CreateTradeOrderWithSnapshot", state, create, package.Payload, actor);
        Require(created.Result.Accepted, "Create order failed: " + System.Text.Json.JsonSerializer.Serialize(created));
        EventCreationResponse retried = await SnapshotEndpointTestClient.Invoke<EventCreationResponse>("CreateTradeOrderWithSnapshot", state, create, package.Payload, actor);
        Require(retried.Result.Accepted && retried.EventId == created.EventId && retried.AppliedSnapshotId == "trade-endpoint",
            "Buy-only order retry lost its confirmed snapshot.");

        foreach (string mode in new[] { "SelfDelivery", "ServerDropPod" })
        {
            state = new ClashOfRimNetworkState();
            AuthoritativeEvent order = SeedOrder(state.Ledger);
            string memoId = "drop-pod-direct:" + order.EventId;
            if (mode == "SelfDelivery")
            {
                AuthoritativeEvent memo = AuthoritativeEventFactory.Create(ServerEventType.Trade, new EventParty("userA", "colonyA"),
                    new EventParty("userA", "colonyA"), "memo", false, ((TradeEventPayload)order.Payload) with
                    { TradeId = order.EventId, Stage = TradeStage.AcceptedMemo, AcceptedByUserId = "userA" }, DateTimeOffset.UtcNow) with { Status = ServerEventStatus.Recorded };
                state.Ledger.Append(memo);
                memoId = memo.EventId;
            }
            FulfillTradeOrderWithSnapshotRequest Request(string key, string tradeId) => new(
                new FulfillTradeOrderRequest(key, tradeId, memoId, actor, new[] { new ThingReferenceDto("paid", "Silver", 20) }, mode),
                ProtocolDtoMapper.ToMetadataDto(package));
            var request = Request("fulfill", order.EventId);
            FulfillTradeOrderResponse fulfilled = await SnapshotEndpointTestClient.Invoke<FulfillTradeOrderResponse>("FulfillTradeOrderWithSnapshot", state, request, package.Payload, actor);
            Require(fulfilled.Result.Accepted, "Fulfillment failed: " + System.Text.Json.JsonSerializer.Serialize(fulfilled));
            FulfillTradeOrderResponse replay = await SnapshotEndpointTestClient.Invoke<FulfillTradeOrderResponse>("FulfillTradeOrderWithSnapshot", state, request, package.Payload, actor);
            Require(replay.Result.Accepted && replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
                && replay.ExchangeEventId == fulfilled.ExchangeEventId && replay.OwnerDeliveryEventId == fulfilled.OwnerDeliveryEventId
                && replay.AcceptorDeliveryEventId == fulfilled.AcceptorDeliveryEventId
                && state.Ledger.ListByType(ServerEventType.ItemDelivery).Count == (mode == "ServerDropPod" ? 2 : 1), "Fulfillment replay duplicated deliveries.");
            if (mode == "ServerDropPod") Require(state.Ledger.Find(fulfilled.AcceptorDeliveryEventId!)!.TargetContext?.MapUniqueId is not null,
                "Preparing before snapshot publication lost the delivery map.");
            FulfillTradeOrderResponse wrongOrder = await SnapshotEndpointTestClient.Invoke<FulfillTradeOrderResponse>("FulfillTradeOrderWithSnapshot", state, Request("fulfill", "different-order"), package.Payload, actor);
            Require(wrongOrder.Result.ErrorCode == ProtocolErrorCode.Conflict, "Replay accepted a different order.");
        }

        // Distinct colonies can prepare concurrently, but only one may spend its snapshot on the order.
        state = new ClashOfRimNetworkState();
        AuthoritativeEvent racedOrder = SeedOrder(state.Ledger);
        var buyers = new[] { actor, new ProtocolIdentity("userC", "colonyC", "before") };
        Task<FulfillTradeOrderResponse>[] attempts = buyers.Select((buyer, i) => Task.Run(async () =>
        {
            SaveSnapshotPackage candidate = package with { Envelope = package.Envelope with
                { Identity = new SnapshotIdentity(buyer.UserId, buyer.ColonyId!, "race-" + i) } };
            var request = new FulfillTradeOrderWithSnapshotRequest(new FulfillTradeOrderRequest("race-" + i,
                racedOrder.EventId, "", buyer, new[] { new ThingReferenceDto("paid", "Silver", 20) }, "ServerDropPod"), ProtocolDtoMapper.ToMetadataDto(candidate));
            return await SnapshotEndpointTestClient.Invoke<FulfillTradeOrderResponse>("FulfillTradeOrderWithSnapshot", state, request, candidate.Payload, buyer);
        })).ToArray();
        FulfillTradeOrderResponse[] results = await Task.WhenAll(attempts);
        Require(results.Count(result => result.Result.Accepted) == 1 && state.SnapshotStore.ListLatest().Count == 1
            && state.Ledger.ListByType(ServerEventType.ItemDelivery).Count == 2, "Concurrent buyers both spent snapshots or received goods.");
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
            if (fault is 0 or 6) throw new IOException("before snapshot");
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
