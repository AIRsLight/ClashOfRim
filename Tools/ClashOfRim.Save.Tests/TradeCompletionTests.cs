using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

internal static class TradeCompletionTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(5);
    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        TradeLifecycleRecoveryTests.Run(fixture);
        foreach (string storage in new[] { "memory", "file", "sqlite" })
        {
            Unfulfilled(storage);
            foreach (string mode in new[] { "SelfDelivery", "ServerDropPod", "DirectDropPod" }) Valid(storage, mode);
            foreach (string corruption in new[] { "no-exchange", "two-exchanges", "exchange-status", "snapshot", "delivered-snapshot", "applied-at",
                "delivered-at", "result", "actor", "target", "accepted-user", "fee", "offered", "payment-source", "mode", "postage", "exchange-context",
                "order-mode", "no-memo", "memo-active", "memo-party", "memo-offered", "watcher-active", "watcher-applied", "no-owner-delivery",
                "owner-type", "owner-purpose", "owner-party", "owner-items", "owner-context", "no-buyer-delivery", "buyer-items", "buyer-party",
                "buyer-terminal", "owner-terminal", "message" }) Corrupt(storage, corruption);
        }
        ActualFulfillment(fixture).GetAwaiter().GetResult();
    }

    private static void Unfulfilled(string storage) => WithRoot(root =>
    {
        var state = State(Open(storage, root)); var order = Order(); state.Ledger.Append(order);
        string token = TradeCommandAuthTests.Token(state, "seller");
        foreach (var status in new[] { ServerEventStatus.Recorded, ServerEventStatus.PendingOfflineDelivery, ServerEventStatus.AppliedToSnapshot,
            ServerEventStatus.Cancelled, ServerEventStatus.Failed, ServerEventStatus.Conflict, ServerEventStatus.RejectedByTarget })
        {
            state.Ledger.ChangeStatus(order.EventId, status); string before = Snapshot(state.Ledger);
            var result = Complete(state, order.EventId, token);
            Require(!result.Result.Accepted && result.Result.ErrorCode == ProtocolErrorCode.Conflict && before == Snapshot(state.Ledger)
                && state.EventNotifications.GetVersion("seller") == 0, "completion bypassed snapshot fulfillment: " + status);
        }
    });

    private static void Valid(string storage, string mode) => WithRoot(root =>
    {
        var ledger = Open(storage, root); var order = Order(); ledger.Append(order); SeedCompleted(ledger, order, mode);
        foreach (var delivery in ledger.ListByType(ServerEventType.ItemDelivery))
        { ledger.MarkDelivered(delivery.EventId, "before", At); ledger.MarkApplied(delivery.EventId, "consumed", At); }
        if (storage != "memory") ledger = Open(storage, root);
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, ReadOnlyLedger>(); ((ReadOnlyLedger)proxy).Inner = ledger;
        var state = State(proxy); string token = TradeCommandAuthTests.Token(state, "seller"); string before = Snapshot(ledger);
        for (int i = 0; i < 3; i++)
        {
            var response = Complete(state, order.EventId, token, "retry-" + i);
            Require(response.Result.Accepted && response.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
                && response.NotifiedAcceptorCount == 2 && before == Snapshot(ledger), "valid completion replay changed durable state: " + mode);
            foreach (string user in new[] { "seller", "buyer", "watcher" })
                Require(state.EventNotifications.GetVersion(user) == i + 1, "completion replay missed participant: " + user);
        }
        string buyerToken = TradeCommandAuthTests.Token(state, "buyer");
        Require(!Complete(state, order.EventId, buyerToken, user: "buyer").Result.Accepted && before == Snapshot(ledger), "buyer completed seller order");
        if (mode == "SelfDelivery")
        {
            var exchange = ledger.FindByIdempotencyKey("exchange")!; var p = (TradeEventPayload)exchange.Payload;
            ledger.Append(Delivery("trade-completed-acceptor-delivery:" + order.EventId + ":" + p.AcceptedMemoEventId,
                order.Actor, exchange.Actor, p.OfferedItems, ItemDeliveryPurpose.TradeCompletedAcceptorDelivery));
            Require(!Complete(state, order.EventId, token).Result.Accepted, "self delivery accepted an extra goods delivery");
        }
    });

    private static void Corrupt(string storage, string kind) => WithRoot(root =>
    {
        var source = new InMemoryAuthoritativeEventLedger(); var order = Order(); source.Append(order); SeedCompleted(source, order, "ServerDropPod");
        var records = source.ListAll().ToList();
        void Change(string key, Func<AuthoritativeEvent, AuthoritativeEvent> change)
        { int i = records.FindIndex(e => e.IdempotencyKey == key); records[i] = change(records[i]); }
        void Drop(string key) => records.RemoveAll(e => e.IdempotencyKey == key);
        string ownerKey = "trade-completed-owner-delivery:" + order.EventId + ":" + source.FindByIdempotencyKey("memo")!.EventId;
        string buyerKey = ownerKey.Replace("owner-delivery", "acceptor-delivery");
        switch (kind)
        {
            case "no-exchange": Drop("exchange"); break;
            case "two-exchanges": records.Add(source.FindByIdempotencyKey("exchange")! with { EventId = "extra", IdempotencyKey = "extra" }); break;
            case "exchange-status": Change("exchange", e => e with { Status = ServerEventStatus.Recorded }); break;
            case "snapshot": Change("exchange", e => e with { AppliedSnapshotId = null }); break;
            case "delivered-snapshot": Change("exchange", e => e with { DeliveredToSnapshotId = null }); break;
            case "applied-at": Change("exchange", e => e with { AppliedAtUtc = null }); break;
            case "delivered-at": Change("exchange", e => e with { DeliveredAtUtc = null }); break;
            case "result": Change("exchange", e => e with { LastApplicationResult = EventApplicationResultKind.ReadyToApply }); break;
            case "actor": Change("exchange", e => e with { Actor = new("other", "other-colony") }); break;
            case "target": Change("exchange", e => e with { Target = new("other", "other-colony") }); break;
            case "accepted-user": Change("exchange", e => e with { Payload = ((TradeEventPayload)e.Payload) with { AcceptedByUserId = "other" } }); break;
            case "fee": Change("exchange", e => e with { Payload = ((TradeEventPayload)e.Payload) with { FeeSilver = 99 } }); break;
            case "offered": Change("exchange", e => e with { Payload = ((TradeEventPayload)e.Payload) with { OfferedItems = [] } }); break;
            case "payment-source": Change("exchange", e => e with { Payload = ((TradeEventPayload)e.Payload) with { RequestedItems = [new("silver", "Silver", 20, "wrong-snapshot")] } }); break;
            case "mode": Change("exchange", e => e with { Payload = ((TradeEventPayload)e.Payload) with { FulfillmentMode = TradeFulfillmentMode.SelfDelivery } }); break;
            case "postage": Change("exchange", e => e with { Payload = ((TradeEventPayload)e.Payload) with { PostagePaidByAcceptor = false } }); break;
            case "exchange-context": Change("exchange", e => e with { TargetContext = new("other", "map", 2, EventLandingMode.DropPod) }); break;
            case "order-mode": Change("order", e => e with { Payload = ((TradeEventPayload)e.Payload) with { FulfillmentMode = TradeFulfillmentMode.SelfDelivery } }); break;
            case "no-memo": Drop("memo"); break;
            case "memo-active": Change("memo", e => e with { Status = ServerEventStatus.Recorded }); break;
            case "memo-party": Change("memo", e => e with { Target = new("other", "other-colony") }); break;
            case "memo-offered": Change("memo", e => e with { Payload = ((TradeEventPayload)e.Payload) with { OfferedItems = [] } }); break;
            case "watcher-active": Change("watcher-memo", e => e with { Status = ServerEventStatus.Recorded }); break;
            case "watcher-applied": Change("watcher-memo", e => e with { Status = ServerEventStatus.AppliedToSnapshot }); break;
            case "no-owner-delivery": Drop(ownerKey); break;
            case "owner-type": Change(ownerKey, e => e with { Type = ServerEventType.Trade }); break;
            case "owner-purpose": Change(ownerKey, e => e with { Payload = ((ItemDeliveryEventPayload)e.Payload) with { Purpose = ItemDeliveryPurpose.Gift } }); break;
            case "owner-party": Change(ownerKey, e => e with { Actor = new("other", "other-colony") }); break;
            case "owner-items": Change(ownerKey, e => e with { Payload = ((ItemDeliveryEventPayload)e.Payload) with { Items = [] } }); break;
            case "owner-context": Change(ownerKey, e => e with { TargetContext = new("other", "map", 2, EventLandingMode.MapEdge) }); break;
            case "no-buyer-delivery": Drop(buyerKey); break;
            case "buyer-items": Change(buyerKey, e => e with { Payload = ((ItemDeliveryEventPayload)e.Payload) with { Items = [] } }); break;
            case "buyer-party": Change(buyerKey, e => e with { Target = new("other", "other-colony") }); break;
            case "buyer-terminal": Change(buyerKey, e => e with { Status = ServerEventStatus.Failed }); break;
            case "owner-terminal": Change(ownerKey, e => e with { Status = ServerEventStatus.Conflict }); break;
            case "message": Change(ownerKey, e => e with { Payload = ((ItemDeliveryEventPayload)e.Payload) with { DeliveryKind = "Forced" } }); break;
        }
        var ledger = Open(storage, root); foreach (var record in records) ledger.Append(record);
        if (storage != "memory") ledger = Open(storage, root);
        var state = State(ledger); string token = TradeCommandAuthTests.Token(state, "seller"); string before = Snapshot(ledger);
        Require(!Complete(state, order.EventId, token).Result.Accepted && before == Snapshot(ledger)
            && new[] { "seller", "buyer", "watcher" }.All(u => state.EventNotifications.GetVersion(u) == 0), "invalid completion evidence accepted: " + kind);
    });

    internal static void SeedCompleted(IAuthoritativeEventLedger ledger, AuthoritativeEvent order, string mode = "DirectDropPod")
    {
        var p = (TradeEventPayload)order.Payload; var buyer = new EventParty("buyer", "buyer-colony"); bool drop = mode != "SelfDelivery";
        var memo = AuthoritativeEventFactory.Create(ServerEventType.Trade, buyer, buyer, "memo", false,
            new TradeEventPayload(order.EventId, TradeStage.AcceptedMemo, p.OfferedItems, p.RequestedItems, p.FeeSilver, AcceptedByUserId: buyer.UserId), At,
            order.TargetContext) with { Status = ServerEventStatus.AppliedToSnapshot };
        if (mode != "DirectDropPod") ledger.Append(memo);
        var watcher = new EventParty("watcher", "watcher-colony");
        ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.Trade, watcher, watcher, "watcher-memo", false,
            new TradeEventPayload(order.EventId, TradeStage.AcceptedMemo, p.OfferedItems, p.RequestedItems, p.FeeSilver, AcceptedByUserId: watcher.UserId), At,
            order.TargetContext) with { Status = ServerEventStatus.Cancelled });
        var paid = p.RequestedItems.Select(item => item with { SourceSnapshotId = "paid-snapshot" }).ToArray();
        string? memoId = mode == "DirectDropPod" ? null : memo.EventId;
        ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.Trade, buyer, order.Actor, "exchange", false,
            new TradeEventPayload(order.EventId, drop ? TradeStage.ServerDropPodExchange : TradeStage.SelfDeliveryExchange, p.OfferedItems, paid,
                p.FeeSilver, buyer.UserId, memoId, drop ? TradeFulfillmentMode.ServerDropPod : TradeFulfillmentMode.SelfDelivery, drop), At, order.TargetContext)
            with { Status = ServerEventStatus.AppliedToSnapshot, AppliedSnapshotId = "paid-snapshot", AppliedAtUtc = At,
                DeliveredToSnapshotId = "before", DeliveredAtUtc = At, LastApplicationResult = EventApplicationResultKind.Applied });
        string suffix = memoId ?? "buyer:buyer-colony";
        ledger.Append(Delivery("trade-completed-owner-delivery:" + order.EventId + ":" + suffix, buyer, order.Actor, paid,
            ItemDeliveryPurpose.TradeCompletedOwnerDelivery) with { TargetContext = order.TargetContext is null ? null :
                order.TargetContext with { LandingMode = drop ? EventLandingMode.DropPod : EventLandingMode.MapEdge } });
        if (drop) ledger.Append(Delivery("trade-completed-acceptor-delivery:" + order.EventId + ":" + suffix, order.Actor, buyer,
            p.OfferedItems, ItemDeliveryPurpose.TradeCompletedAcceptorDelivery));
        ledger.ChangeStatus(order.EventId, ServerEventStatus.AppliedToSnapshot);
    }

    private static async Task ActualFulfillment(Func<string, SaveSnapshotPackage> fixture)
    {
        foreach (string mode in new[] { "SelfDelivery", "ServerDropPod", "DirectDropPod" })
        {
            var state = State(new InMemoryAuthoritativeEventLedger()); var order = Order(); state.Ledger.Append(order);
            var buyer = new ProtocolIdentity("userA", "colonyA", "before"); string memoId = "drop-pod-direct:" + order.EventId;
            if (mode != "DirectDropPod")
            {
                var p = (TradeEventPayload)order.Payload; var party = new EventParty(buyer.UserId, buyer.ColonyId);
                var memo = AuthoritativeEventFactory.Create(ServerEventType.Trade, party, party, "actual-memo", false,
                    new TradeEventPayload(order.EventId, TradeStage.AcceptedMemo, p.OfferedItems, p.RequestedItems, p.FeeSilver,
                        AcceptedByUserId: buyer.UserId), At, order.TargetContext) with { Status = ServerEventStatus.Recorded };
                state.Ledger.Append(memo); memoId = memo.EventId;
            }
            var package = fixture("completion-" + mode);
            var request = new FulfillTradeOrderWithSnapshotRequest(new FulfillTradeOrderRequest("fulfill", order.EventId, memoId,
                buyer, [new("paid", "Silver", 20)], mode == "SelfDelivery" ? mode : "ServerDropPod"), ProtocolDtoMapper.ToMetadataDto(package));
            var response = await SnapshotEndpointTestClient.Invoke<FulfillTradeOrderResponse>("FulfillTradeOrderWithSnapshot", state, request, package.Payload, buyer);
            Require(response.Result.Accepted, "real snapshot fulfillment setup failed: " + response.Result.Message);
            string token = TradeCommandAuthTests.Token(state, "seller"); string before = Snapshot(state.Ledger);
            Require(Complete(state, order.EventId, token).Result.Accepted && before == Snapshot(state.Ledger), "real committed fulfillment was not recognized: " + mode);
        }
    }

    private static AuthoritativeEvent Order() => AuthoritativeEventFactory.Create(ServerEventType.Trade, new("seller", "seller-colony"), new("server"), "order", false,
        new TradeEventPayload("order", TradeStage.MarketOrder, [new("steel", "Steel", 10, "seller-snapshot")], [new("silver", "Silver", 20, "seller-snapshot")], 0), At);
    private static AuthoritativeEvent Delivery(string key, EventParty actor, EventParty target, IReadOnlyList<EventThingReference> items, ItemDeliveryPurpose purpose) =>
        AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, actor, target, key, false, new ItemDeliveryEventPayload(items, null, Purpose: purpose), At);
    private static CloseTradeOrderResponse Complete(ClashOfRimNetworkState state, string orderId, string token, string key = "complete", string user = "seller")
    {
        return state.Trades.CompleteTradeOrder(new CloseTradeOrderRequest(key, orderId, new(user, user + "-colony", "latest-snapshot"), null, token));
    }
    private static ClashOfRimNetworkState State(IAuthoritativeEventLedger ledger) => new(ledger: ledger, serverConfiguration: new ClashOfRimServerConfiguration(tradeOrderExpiration: TimeSpan.Zero));
    private static IAuthoritativeEventLedger Open(string storage, string root) => storage switch
    { "sqlite" => new SqliteAuthoritativeEventLedger(Path.Combine(root, "events.sqlite")), "file" => new FileAuthoritativeEventLedger(Path.Combine(root, "events.json")), _ => new InMemoryAuthoritativeEventLedger() };
    private static string Snapshot(IAuthoritativeEventLedger ledger) => JsonSerializer.Serialize(ledger.ListAll());
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void WithRoot(Action<string> action)
    { string root = Path.Combine(Path.GetTempPath(), "cor-completion-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); try { action(root); } finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); } }
    public class ReadOnlyLedger : DispatchProxy
    {
        public IAuthoritativeEventLedger Inner = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (!(method!.Name.StartsWith("Find") || method.Name.StartsWith("List"))) throw new Exception("Completion attempted a ledger mutation: " + method.Name);
            try { return method.Invoke(Inner, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }
    }
}
