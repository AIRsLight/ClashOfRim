using System.Globalization;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Trades;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;

internal static class TradeModuleTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                CreateAndReplay(factory);
                foreach (bool pods in new[] { false, true }) FulfillAndReplay(factory, pods);
                foreach (string command in new[] { "order", "fulfill" })
                foreach (int phase in new[] { 1, 2 }) RevokedDuringRecovery(factory, command, phase);
                MarketAndPostage(factory);
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
        var retiredOrder = (EventCreationResponse)((IValueHttpResult)TradeEndpoints.CreateTradeOrder()).Value!;
        var retiredFulfill = (FulfillTradeOrderResponse)((IValueHttpResult)TradeEndpoints.FulfillTradeOrder()).Value!;
        Check(!retiredOrder.Result.Accepted && !retiredFulfill.Result.Accepted, "Retired requests mutated the trade contract.");
        ConfigurationChangesDuringPreparation(factory);
        TradeCollectionTests.Run(factory);
    }

    private static EconomicModuleTestContext Fixture(Func<string, SaveSnapshotPackage> factory) => new(factory)
    {
        Configuration = new(tradeFeePolicy: new TradeFeePolicy(baseFeeRate: 0,
            standardMarketValuePerThing: new Dictionary<string, float> { ["Steel"] = 2, ["Silver"] = 1 }),
            tradeOrderExpiration: TimeSpan.Zero, tradePostageBaseSilver: 3, tradePostageSilverPerTile: 2,
            tradePostageCrossLayerOverheadDistanceTiles: 7)
    };

    private static CreateTradeOrderWithSnapshotRequest OrderRequest(EconomicModuleTestContext f, string key = "order-İ") =>
        new(new CreateTradeOrderRequest(key, new("userA", "colonyA", "before"),
            [new("Steel_1", "Steel", 4)], [new("Silver_1", "Silver", 10)], 1, true, true),
            ProtocolDtoMapper.ToMetadataDto(f.Package));

    private static void CreateAndReplay(Func<string, SaveSnapshotPackage> factory)
    {
        var f = Fixture(factory); var request = OrderRequest(f);
        Check(f.Trades.QuoteTradeOrderFee(new(request.TradeOrder.Owner, request.TradeOrder.OfferedThings,
            request.TradeOrder.RequestedThings)).Result.Accepted, "Independent fee quote failed.");
        foreach (string? token in new[] { null, "forged", f.Issue("other", "other-colony") })
            Check(!f.Trades.CreateTradeOrderWithSnapshot(request, f.Package.Payload, token).Result.Accepted
                && f.Recovery.Replays == 0 && f.Ledger.ListAll().Count == 0, "Invalid order principal triggered recovery.");
        var first = f.Trades.CreateTradeOrderWithSnapshot(request, f.Package.Payload, f.Token);
        Check(first.Result.Accepted && first.AppliedSnapshotId == "paid" && f.Recovery.Commits == 1, "Order commit failed: " + first.Result.Message);
        string evidence = Evidence(f);
        f.Configuration = new(tradeMarketplaceEnabled: false);
        var retry = f.Trades.CreateTradeOrderWithSnapshot(request, f.Package.Payload, f.Token);
        Check(retry.Result.Accepted && retry.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
            && retry.EventId == first.EventId && retry.NextLineageToken == first.NextLineageToken && Evidence(f) == evidence,
            "Configuration change altered an order replay.");
        Check(!f.Trades.CreateTradeOrderWithSnapshot(OrderRequest(f, "new"), f.Package.Payload, f.Token).Result.Accepted
            && Evidence(f) == evidence, "Disabled market accepted a new order.");
        var changed = f.Package.Payload.ToArray(); changed[^1] ^= 1;
        Check(f.Trades.CreateTradeOrderWithSnapshot(request, changed, f.Token).Result.ErrorCode == ProtocolErrorCode.Conflict
            && Evidence(f) == evidence, "Changed snapshot bytes were replayed as the original order.");
    }

    private static AuthoritativeEvent SeedOrder(EconomicModuleTestContext f, string key = "seller-order")
    {
        var order = AuthoritativeEventFactory.Create(ServerEventType.Trade, new("seller", "seller-colony"), new("server"), key, false,
            new TradeEventPayload(key, TradeStage.MarketOrder, [new("steel", "Steel", 4, "seller-base")],
                [new("silver", "Silver", 10)], 0), DateTimeOffset.UtcNow, new("seller-world", "seller-map", 10, EventLandingMode.DropPod));
        f.Ledger.Append(order); return order;
    }

    private static FulfillTradeOrderWithSnapshotRequest Fulfillment(EconomicModuleTestContext f, AuthoritativeEvent order, bool pods)
    {
        string memo = "";
        if (!pods)
        {
            var accepted = f.Trades.AcceptTradeOrder(new("accept", order.EventId, new("userA", "colonyA", "before"), false, f.Token));
            Check(accepted.Result.Accepted, "Independent acceptance failed."); memo = accepted.MemoEventId!;
        }
        return new(new FulfillTradeOrderRequest("fulfill-İ", order.EventId, memo, new("userA", "colonyA", "before"),
            [new("payment", "Silver", 10)], pods ? "ServerDropPod" : "SelfDelivery"), ProtocolDtoMapper.ToMetadataDto(f.Package));
    }

    private static void FulfillAndReplay(Func<string, SaveSnapshotPackage> factory, bool pods)
    {
        var f = Fixture(factory); var order = SeedOrder(f); var request = Fulfillment(f, order, pods);
        var first = f.Trades.FulfillTradeOrderWithSnapshot(request, f.Package.Payload, f.Token);
        Check(first.Result.Accepted && first.ExchangeCreated && first.OwnerDeliveryEventId is not null
            && (first.AcceptorDeliveryEventId is not null) == pods && f.Recovery.Commits == 1,
            "Fulfillment lost its delivery plan: " + first.Result.Message);
        var exchange = f.Ledger.Find(first.ExchangeEventId!)!;
        Check(f.Recovery.AchievementEvents.Count(id => id == exchange.EventId) == 1
            && f.Ledger.Find(order.EventId)!.Status == ServerEventStatus.AppliedToSnapshot, "Completion effects missing.");
        string evidence = Evidence(f); f.Configuration = new(tradeMarketplaceEnabled: false);
        var retry = f.Trades.FulfillTradeOrderWithSnapshot(request, f.Package.Payload, f.Token);
        Check(retry.Result.Accepted && !retry.ExchangeCreated && retry.ExchangeEventId == first.ExchangeEventId
            && retry.OwnerDeliveryEventId == first.OwnerDeliveryEventId && retry.AcceptorDeliveryEventId == first.AcceptorDeliveryEventId
            && Evidence(f) == evidence, "Fulfillment replay re-applied the journal effects.");
        string sellerToken = f.Issue("seller", "seller-colony");
        Check(f.Trades.CompleteTradeOrder(new("complete", order.EventId, new("seller", "seller-colony", "seller-base"), null, sellerToken))
            .Result.ErrorCode == ProtocolErrorCode.DuplicateRequest, "Legacy completion could not verify the committed fulfillment.");
        f.Tokens.RevokeForUser("userA");
        Check(!f.Trades.FulfillTradeOrderWithSnapshot(request, f.Package.Payload, f.Token).Result.Accepted,
            "Committed fulfillment replay bypassed credential revocation.");
    }

    private static void RevokedDuringRecovery(Func<string, SaveSnapshotPackage> factory, string command, int phase)
    {
        var f = Fixture(factory); var order = command == "fulfill" ? SeedOrder(f) : null;
        var request = order is null ? null : Fulfillment(f, order, true);
        string before = Evidence(f); int replay = 0;
        f.Recovery.OnReplay = () => { if (++replay == phase) f.Tokens.RevokeForUser("userA"); };
        var result = command == "order"
            ? f.Trades.CreateTradeOrderWithSnapshot(OrderRequest(f), f.Package.Payload, f.Token).Result
            : f.Trades.FulfillTradeOrderWithSnapshot(request!, f.Package.Payload, f.Token).Result;
        Check(!result.Accepted && replay == phase && Evidence(f) == before,
            "Recovery revoked the principal but trade mutated the ledger or snapshot.");
    }

    private static void ConfigurationChangesDuringPreparation(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (bool increaseFee in new[] { false, true })
        {
            var f = Fixture(factory); int phase = 0; string before = Evidence(f);
            f.Recovery.OnReplay = () =>
            {
                if (++phase == 2) f.Configuration = increaseFee
                    ? new(tradeFeePolicy: new(baseFeeRate: 1, standardMarketValuePerThing: new Dictionary<string, float> { ["Steel"] = 10, ["Silver"] = 1 }))
                    : new(tradeMarketplaceEnabled: false);
            };
            Check(!f.Trades.CreateTradeOrderWithSnapshot(OrderRequest(f), f.Package.Payload, f.Token).Result.Accepted
                && phase == 2 && Evidence(f) == before, "Final order admission ignored current fee or enablement.");
        }
    }

    private static void MarketAndPostage(Func<string, SaveSnapshotPackage> factory)
    {
        var f = Fixture(factory); var order = SeedOrder(f);
        foreach (int index in Enumerable.Range(0, 3)) SeedOrder(f, "other-" + index);
        int before = f.Recovery.Replays;
        Check(!f.Market.ListTradeOrders(new("userA", "colonyA", "before", authToken: "forged")).Result.Accepted
            && f.Recovery.Replays == before, "Market query recovered before authorization.");
        var page = f.Market.ListTradeOrders(new("userA", "colonyA", "before", limit: 2, authToken: f.Token));
        Check(page.Result.Accepted && page.TotalCount == 4 && page.Orders.Count == 2 && page.HasMore, "Market pagination changed.");
        var accepted = f.Trades.AcceptTradeOrder(new("accept", order.EventId, new("userA", "colonyA", "before"), false, f.Token));
        Check(accepted.Result.Accepted && f.Market.ListTradeOrders(new("userA", "colonyA", "before", "AcceptedByMe", authToken: f.Token)).TotalCount == 1,
            "Market did not project the current acceptance.");
        void Place(string user, string colony, string snapshot, int layer)
        {
            string world = user == "seller" ? "seller-world" : "viewer-world";
            var package = f.Package with { Envelope = f.Package.Envelope with { Identity = new(user, colony, snapshot) } };
            var index = package.Index with
            {
                Maps = [new(user == "seller" ? "seller-map" : "viewer-map", null, world, "100,1,100", false, false, false, false, 0, 0)],
                WorldObjects = [new(world, world, "Settlement", "Settlement", "10," + layer.ToString(CultureInfo.InvariantCulture), null, user, false)]
            };
            f.Snapshots.StoreLatest(new(package.Envelope.Identity, package.Envelope, index, DateTimeOffset.UtcNow));
        }
        Place("seller", "seller-colony", "seller-base", 0); Place("userA", "colonyA", "before", 0);
        var same = f.TradeDelivery.BuildTradePostageQuote(order, "userA", "colonyA", "before");
        Check(same.Reachable && same.DistanceTiles == 0 && same.PostageSilver == 3, "Same-layer postage changed.");
        Place("userA", "colonyA", "before", 1);
        Check(!f.TradeDelivery.BuildTradePostageQuote(order, "userA", "colonyA", "before").Reachable,
            "Different layers reused the same tile number without geometry.");
        var geometry = new WorldTileGeometryDto(new[] { 0, 1 }.Select(layer =>
            new WorldTileLayerGeometryDto(layer, "layer", 1, [new(10, 1, 0, 0)])).ToArray());
        f.World.Submit("admin", new("world", "admin", "admin-colony", DateTimeOffset.UtcNow, "seed", "0.3", "Normal", "Normal", "Normal", "Normal",
            "20", ["OutlanderCivil"], [], [], [], [], []));
        var payload = WorldSubstratePackageCodec.Encode(new WorldSubstratePackage(123, "<grid />", "<features />", "<landmarks />",
            WorldTileGeometryBinaryCodec.Encode(geometry)!));
        Check(f.World.StoreWorldSubstrate("admin", "admin-colony", "world", payload).Accepted, "Test geometry could not be installed.");
        var cross = f.TradeDelivery.BuildTradePostageQuote(order, "userA", "colonyA", "before");
        Check(cross.Reachable && cross.DistanceTiles == 7 && cross.PostageSilver == 17, "Cross-layer postage lost overhead.");
        Check(!f.TradeDelivery.BuildTradePostageQuote(order, "userA", "colonyA", "stale").Reachable, "Postage accepted a stale source snapshot.");
    }

    private static string Evidence(EconomicModuleTestContext f) => JsonSerializer.Serialize(f.Ledger.ListAll())
        + "/" + f.Snapshots.GetLatest("userA", "colonyA")!.Identity.SnapshotId + "/" + f.Recovery.Commits
        + "/" + string.Join(",", f.Recovery.AchievementEvents);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
