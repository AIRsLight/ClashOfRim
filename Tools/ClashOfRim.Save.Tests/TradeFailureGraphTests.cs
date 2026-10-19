using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class TradeFailureGraphTests
{
    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (string storage in new[] { "memory", "file", "sqlite" })
        {
            foreach (string mode in new[] { "SelfDelivery", "ServerDropPod", "DirectDropPod" })
            foreach (bool legacy in new[] { false, true }) Valid(storage, mode, legacy);
            foreach (bool replay in new[] { false, true })
            foreach (string kind in new[] { "no-exchange", "two-exchanges", "exchange-target", "exchange-items", "exchange-fee", "exchange-mode", "exchange-proof",
                "memo-party", "memo-items", "no-memo", "delivery-party", "delivery-items", "delivery-purpose", "extra-delivery", "no-owner-delivery",
                "owner-return-party", "owner-return-items", "owner-return-type", "owner-return-context", "owner-return-failed", "unknown-legacy-suffix", "two-owner-returns",
                "buyer-return-party", "buyer-return-items", "buyer-return-failed", "two-buyer-returns", "cancel-refund" }) Corrupt(storage, kind, replay);
            MissingReplayReturn(storage);
        }
        ActualFulfillment(fixture).GetAwaiter().GetResult();
    }

    private static void Valid(string storage, string mode, bool legacy) => WithRoot(root =>
    {
        var ledger = Open(storage, root); var order = Seed(ledger, mode); var failed = Root(ledger, mode);
        if (mode != "SelfDelivery") AddReturns(ledger, order, failed, legacy, consume: true);
        var preserved = ledger.ListAll().Where(item => item.Payload is ItemDeliveryEventPayload { IsTradeReturn: true }
            || item.Payload is TradeEventPayload { Stage: TradeStage.AcceptanceCancelled }).ToArray();
        string refunds = Snapshot(preserved.Where(item => item.Type == ServerEventType.ItemDelivery));
        string receipts = Snapshot(preserved.Where(item => item.Type == ServerEventType.Trade));
        if (storage != "memory") ledger = Open(storage, root);
        var state = State(ledger); var result = Report(state, failed);
        Require(result.Result.Accepted && ledger.Find(order.EventId)!.Status == (mode == "SelfDelivery" ? ServerEventStatus.Conflict : ServerEventStatus.Cancelled), "valid failure rejected: " + mode);
        Require(refunds == Snapshot(preserved.Where(item => item.Type == ServerEventType.ItemDelivery).Select(item => ledger.Find(item.EventId)!)), "failure changed consumed escrow returns");
        Require(receipts == Snapshot(preserved.Where(item => item.Type == ServerEventType.Trade).Select(item => ledger.Find(item.EventId)!)), "failure rewrote acceptance cancellation receipt");
        Require(ledger.ListByType(ServerEventType.ItemDelivery).Count(item => item.Payload is ItemDeliveryEventPayload { IsTradeReturn: true }) == (mode == "SelfDelivery" ? 0 : 2), "refund count changed");
        string completed = Snapshot(ledger.ListAll());
        for (int i = 0; i < 3; i++)
        {
            long version = state.EventNotifications.GetVersion("watcher");
            var retry = Report(state, failed);
            Require(retry.Result.Accepted && retry.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && completed == Snapshot(ledger.ListAll())
                && state.EventNotifications.GetVersion("watcher") == version + 1, "failure replay rewrote graph or lost participant");
        }
    });

    private static void Corrupt(string storage, string kind, bool replay) => WithRoot(root =>
    {
        var source = new InMemoryAuthoritativeEventLedger(); var order = Seed(source, "ServerDropPod"); var failed = Root(source, "ServerDropPod");
        AddReturns(source, order, failed, false, false);
        if (replay) Require(Report(State(source), failed).Result.Accepted, "replay setup failed");
        var records = source.ListAll().ToList();
        var exchange = source.FindByIdempotencyKey("exchange")!; var p = (TradeEventPayload)exchange.Payload;
        var ownerDelivery = source.ListAll().Single(item => item.Payload is ItemDeliveryEventPayload { Purpose: ItemDeliveryPurpose.TradeCompletedOwnerDelivery });
        var ownerReturn = source.ListAll().Single(item => item.Payload is ItemDeliveryEventPayload { Purpose: ItemDeliveryPurpose.TradeApplicationFailedOwnerReturn });
        var buyerReturn = source.ListAll().Single(item => item.Payload is ItemDeliveryEventPayload { Purpose: ItemDeliveryPurpose.TradeApplicationFailedAcceptorReturn });
        void Change(string id, Func<AuthoritativeEvent, AuthoritativeEvent> mutation) { int i = records.FindIndex(item => item.EventId == id); records[i] = mutation(records[i]); }
        void ChangeExchange(Func<TradeEventPayload, TradeEventPayload> mutation) => Change(exchange.EventId, item => item with { Payload = mutation((TradeEventPayload)item.Payload) });
        void ChangeReturn(AuthoritativeEvent item, Func<ItemDeliveryEventPayload, ItemDeliveryEventPayload> mutation) => Change(item.EventId, value => value with { Payload = mutation((ItemDeliveryEventPayload)value.Payload) });
        switch (kind)
        {
            case "no-exchange": records.RemoveAll(item => item.EventId == exchange.EventId); break;
            case "two-exchanges": records.Add(exchange with { EventId = "other-exchange", IdempotencyKey = "other-exchange" }); break;
            case "exchange-target": Change(exchange.EventId, item => item with { Target = new("other", "other-colony") }); break;
            case "exchange-items": ChangeExchange(payload => payload with { OfferedItems = [] }); break;
            case "exchange-fee": ChangeExchange(payload => payload with { FeeSilver = 999 }); break;
            case "exchange-mode": ChangeExchange(payload => payload with { FulfillmentMode = TradeFulfillmentMode.SelfDelivery }); break;
            case "exchange-proof": Change(exchange.EventId, item => item with { AppliedAtUtc = null }); break;
            case "memo-party": Change(source.FindByIdempotencyKey("memo")!.EventId, item => item with { Target = new("other", "other-colony") }); break;
            case "memo-items": Change(source.FindByIdempotencyKey("memo")!.EventId, item => item with { Payload = ((TradeEventPayload)item.Payload) with { OfferedItems = [] } }); break;
            case "no-memo": records.RemoveAll(item => item.IdempotencyKey == "memo"); break;
            case "delivery-party": Change(ownerDelivery.EventId, item => item with { Target = new("other", "other-colony") }); break;
            case "delivery-items": ChangeReturn(ownerDelivery, payload => payload with { Items = [] }); break;
            case "delivery-purpose": ChangeReturn(ownerDelivery, payload => payload with { Purpose = ItemDeliveryPurpose.Gift }); break;
            case "extra-delivery": records.Add(AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, exchange.Actor, order.Actor,
                "trade-completed-owner-delivery:" + order.EventId + ":unrelated", false, ownerDelivery.Payload, DateTimeOffset.UtcNow)); break;
            case "no-owner-delivery": records.RemoveAll(item => item.EventId == ownerDelivery.EventId); break;
            case "owner-return-party": Change(ownerReturn.EventId, item => item with { Target = new("other", "other-colony") }); break;
            case "owner-return-items": ChangeReturn(ownerReturn, payload => payload with { Items = [] }); break;
            case "owner-return-type": Change(ownerReturn.EventId, item => item with { Type = ServerEventType.Trade }); break;
            case "owner-return-context": Change(ownerReturn.EventId, item => item with { TargetContext = new("other", "map", 2, EventLandingMode.CenterNear) }); break;
            case "owner-return-failed": Change(ownerReturn.EventId, item => item with { Status = ServerEventStatus.Failed }); break;
            case "unknown-legacy-suffix":
                records.RemoveAll(item => item.EventId == ownerReturn.EventId);
                records.Add(AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("server"), order.Actor,
                    ownerReturn.IdempotencyKey + ":unrelated-event", false, ownerReturn.Payload, DateTimeOffset.UtcNow)); break;
            case "two-owner-returns": records.Add(AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("server"), order.Actor,
                ownerReturn.IdempotencyKey + ":" + failed.EventId, false, ownerReturn.Payload, DateTimeOffset.UtcNow)); break;
            case "buyer-return-party": Change(buyerReturn.EventId, item => item with { Target = new("other", "other-colony") }); break;
            case "buyer-return-items": ChangeReturn(buyerReturn, payload => payload with { Items = [] }); break;
            case "buyer-return-failed": Change(buyerReturn.EventId, item => item with { Status = ServerEventStatus.Failed }); break;
            case "two-buyer-returns": records.Add(AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("server"), exchange.Actor,
                buyerReturn.IdempotencyKey + ":other", false, buyerReturn.Payload, DateTimeOffset.UtcNow)); break;
            case "cancel-refund": records.Add(AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("server"), order.Actor,
                "trade-cancelled-owner-return:" + order.EventId, false, ownerReturn.Payload, DateTimeOffset.UtcNow)); break;
        }
        var ledger = Open(storage, root); foreach (var item in records) ledger.Append(item);
        if (storage != "memory") ledger = Open(storage, root);
        var state = State(ledger); string before = Snapshot(ledger.ListAll()); var result = Report(state, failed);
        Require(!result.Result.Accepted && result.Result.ErrorCode == ProtocolErrorCode.Conflict && before == Snapshot(ledger.ListAll())
            && state.EventNotifications.GetVersion("seller") == 0 && state.EventNotifications.GetVersion("buyer") == 0,
            "corrupt trade failure accepted: " + kind + "/replay=" + replay);
    });

    private static void MissingReplayReturn(string storage) => WithRoot(root =>
    {
        var source = new InMemoryAuthoritativeEventLedger(); Seed(source, "DirectDropPod"); var failed = Root(source, "DirectDropPod");
        Require(Report(State(source), failed).Result.Accepted, "missing-return setup failed");
        var ledger = Open(storage, root);
        foreach (var item in source.ListAll().Where(item => item.Payload is not ItemDeliveryEventPayload { Purpose: ItemDeliveryPurpose.TradeApplicationFailedAcceptorReturn })) ledger.Append(item);
        var state = State(ledger); string before = Snapshot(ledger.ListAll());
        Require(!Report(state, failed).Result.Accepted && before == Snapshot(ledger.ListAll()), "terminal root hid missing buyer refund");
    });

    private static async Task ActualFulfillment(Func<string, SaveSnapshotPackage> fixture)
    {
        foreach (string mode in new[] { "SelfDelivery", "ServerDropPod", "DirectDropPod" })
        {
            var state = State(new InMemoryAuthoritativeEventLedger()); var order = Order(); state.Ledger.Append(order);
            var buyer = new ProtocolIdentity("userA", "colonyA", "before"); string memoId = "drop-pod-direct:" + order.EventId;
            if (mode != "DirectDropPod")
            {
                var p = (TradeEventPayload)order.Payload; var party = new EventParty(buyer.UserId, buyer.ColonyId);
                var memo = AuthoritativeEventFactory.Create(ServerEventType.Trade, party, party, "memo", false,
                    new TradeEventPayload(order.EventId, TradeStage.AcceptedMemo, p.OfferedItems, p.RequestedItems, 0, AcceptedByUserId: buyer.UserId), DateTimeOffset.UtcNow);
                state.Ledger.Append(memo); memoId = memo.EventId;
            }
            var package = fixture("failure-graph-" + mode);
            var request = new FulfillTradeOrderWithSnapshotRequest(new FulfillTradeOrderRequest("fulfill", order.EventId, memoId, buyer,
                [new("paid", "Silver", 20)], mode == "SelfDelivery" ? mode : "ServerDropPod"), ProtocolDtoMapper.ToMetadataDto(package));
            var response = await SnapshotEndpointTestClient.Invoke<FulfillTradeOrderResponse>("FulfillTradeOrderWithSnapshot", state, request, package.Payload, buyer);
            Require(response.Result.Accepted, "actual fulfillment failed: " + response.Result.Message);
            var failed = state.Ledger.Find(mode == "SelfDelivery" ? response.OwnerDeliveryEventId! : response.AcceptorDeliveryEventId!)!;
            Require(Report(state, failed).Result.Accepted && state.Ledger.Find(order.EventId)!.Status == (mode == "SelfDelivery" ? ServerEventStatus.Conflict : ServerEventStatus.Cancelled),
                "actual snapshot fulfillment could not report landing failure: " + mode);
            Require(state.Ledger.ListAll().Count(item => item.Payload is ItemDeliveryEventPayload { IsTradeReturn: true }) == (mode == "SelfDelivery" ? 0 : 2), "actual payment refund missing");
        }
    }

    private static AuthoritativeEvent Seed(IAuthoritativeEventLedger ledger, string mode)
    {
        var order = Order(); ledger.Append(order); TradeCompletionTests.SeedCompleted(ledger, order, mode);
        var memo = ledger.FindByIdempotencyKey("watcher-memo")!;
        ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.Trade, memo.Actor, order.Actor, "cancel-receipt", false,
            ((TradeEventPayload)memo.Payload) with { Stage = TradeStage.AcceptanceCancelled, AcceptedMemoEventId = memo.EventId }, DateTimeOffset.UtcNow) with { Status = ServerEventStatus.Cancelled });
        return order;
    }
    private static AuthoritativeEvent Root(IAuthoritativeEventLedger ledger, string mode) => ledger.ListAll().Single(item => item.Payload is ItemDeliveryEventPayload p
        && p.Purpose == (mode == "SelfDelivery" ? ItemDeliveryPurpose.TradeCompletedOwnerDelivery : ItemDeliveryPurpose.TradeCompletedAcceptorDelivery));
    private static void AddReturns(IAuthoritativeEventLedger ledger, AuthoritativeEvent order, AuthoritativeEvent failed, bool legacy, bool consume)
    {
        var exchange = ledger.FindByIdempotencyKey("exchange")!;
        foreach (bool owner in new[] { true, false })
        {
            string key = owner ? "trade-application-failed-owner-return:" + order.EventId + (legacy ? ":" + failed.EventId : "")
                : $"trade-application-failed-acceptor-return:{order.EventId}:{exchange.EventId}";
            var returned = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("server"), owner ? order.Actor : exchange.Actor, key, false,
                new ItemDeliveryEventPayload(owner ? ((TradeEventPayload)order.Payload).OfferedItems : ((TradeEventPayload)exchange.Payload).RequestedItems, null,
                    Purpose: owner ? ItemDeliveryPurpose.TradeApplicationFailedOwnerReturn : ItemDeliveryPurpose.TradeApplicationFailedAcceptorReturn), DateTimeOffset.UtcNow,
                owner ? order.TargetContext : null);
            if (legacy) returned = returned with { EventId = AuthoritativeEventFactory.BuildLegacyEventId(returned.Type, key) };
            ledger.Append(returned);
            if (consume) { ledger.MarkDelivered(returned.EventId, "before", DateTimeOffset.UtcNow); ledger.MarkApplied(returned.EventId, "consumed", DateTimeOffset.UtcNow); }
        }
    }
    private static AuthoritativeEvent Order() => AuthoritativeEventFactory.Create(ServerEventType.Trade, new("seller", "seller-colony"), new("server"), "order", false,
        new TradeEventPayload("order", TradeStage.MarketOrder, [new("steel", "Steel", 10, "seller-base")], [new("silver", "Silver", 20)], 0), DateTimeOffset.UtcNow);
    private static ReportEventApplicationFailureResponse Report(ClashOfRimNetworkState state, AuthoritativeEvent root)
    {
        var party = root.Type == ServerEventType.Trade ? root.Actor : root.Target;
        var token = TradeCommandAuthTests.Token(state, party.UserId, party.ColonyId);
        return state.EventFailureReports.Report(new ReportEventApplicationFailureRequest(
            "failure", root.EventId, party.UserId, party.ColonyId!, "snapshot", "landing failed", null, token));
    }
    private static ClashOfRimNetworkState State(IAuthoritativeEventLedger ledger) => new(ledger: ledger, serverConfiguration: new ClashOfRimServerConfiguration(tradeOrderExpiration: TimeSpan.Zero));
    private static IAuthoritativeEventLedger Open(string storage, string root) => storage switch
    { "file" => new FileAuthoritativeEventLedger(Path.Combine(root, "events.json")), "sqlite" => new SqliteAuthoritativeEventLedger(Path.Combine(root, "events.sqlite")), _ => new InMemoryAuthoritativeEventLedger() };
    private static string Snapshot(IEnumerable<AuthoritativeEvent> items) => JsonSerializer.Serialize(items.OrderBy(item => item.EventId));
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void WithRoot(Action<string> action)
    { string root = Path.Combine(Path.GetTempPath(), "cor-trade-failure-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); try { action(root); } finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); } }
}
