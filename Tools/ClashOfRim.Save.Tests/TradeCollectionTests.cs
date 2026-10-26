using System.Reflection;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Trades;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class TradeCollectionTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (string backend in new[] { "memory", "file", "sqlite" })
        foreach (bool pods in new[] { false, true })
        {
            string root = Path.Combine(Path.GetTempPath(), "cor-trade-collection-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                IAuthoritativeEventLedger Open() => backend switch
                {
                    "file" => new FileAuthoritativeEventLedger(Path.Combine(root, "events.json")),
                    "sqlite" => new SqliteAuthoritativeEventLedger(Path.Combine(root, "events.sqlite")),
                    _ => new InMemoryAuthoritativeEventLedger()
                };
                var f = Fixture(factory, Open()); var order = Order(); f.Ledger.Append(order);
                string memo = "";
                if (!pods) memo = f.Trades.AcceptTradeOrder(new("accept", order.EventId, new("userA", "colonyA", "before"), false, f.Token)).MemoEventId!;
                var request = new FulfillTradeOrderWithSnapshotRequest(new("fulfill", order.EventId, memo,
                    new("userA", "colonyA", "before"), [new("paid-silver", "Silver", 10)], pods ? "ServerDropPod" : "SelfDelivery"),
                    ProtocolDtoMapper.ToMetadataDto(f.Package));
                var result = f.Trades.FulfillTradeOrderWithSnapshot(request, f.Package.Payload, f.Token);
                Check(result.Result.Accepted && result.TradeStatus == "Traded", "Fulfillment did not create a traded order.");
                string deliveryId = result.OwnerDeliveryEventId!;
                var held = f.Ledger.Find(deliveryId)!;
                var info = ((ItemDeliveryEventPayload)held.Payload).TradeCollection!;
                Check(held.Status == ServerEventStatus.AwaitingCollection && info.TradeOrderId == order.EventId
                    && info.ExpiresAtUtc == held.CreatedAtUtc + TimeSpan.FromDays(7)
                    && info.ExpiresAtUtc > order.CreatedAtUtc + TimeSpan.FromDays(7), "Collection deadline did not restart at fulfillment.");
                var queue = EventQueueSummaryBuilder.BuildForTarget("seller", f.Ledger.ListAll());
                Check(queue.AllItems.Count == 1 && EventSemanticClassifier.IsSnapshotlessNotification(f.Ledger.Find(queue.AllItems[0].EventId)!),
                    "Fulfillment immediately queued a snapshot-changing owner event.");
                int commits = f.Recovery.Commits; string before = JsonSerializer.Serialize(f.Ledger.ListAll());
                var premature = new ItemDeliveryApplicationConfirmationConsumer(f.Ledger).Consume(
                    new(deliveryId, "seller", "seller-colony", "seller-base", f.Snapshots.GetLatest("userA", "colonyA")!, "Applied"), DateTimeOffset.UtcNow);
                Check(!premature.Accepted && JsonSerializer.Serialize(f.Ledger.ListAll()) == before,
                    "A premature receipt confirmation consumed or poisoned held proceeds.");
                f.Configuration = new(tradeMarketplaceEnabled: false, tradeOrderExpiration: TimeSpan.FromDays(1));
                var retry = f.Trades.FulfillTradeOrderWithSnapshot(request, f.Package.Payload, f.Token);
                Check(retry.Result.Accepted && JsonSerializer.Serialize(f.Ledger.ListAll()) == before && f.Recovery.Commits == commits,
                    "Fulfillment retry or configuration change reset held proceeds.");
                if (backend != "memory") f = Fixture(factory, Open());
                string ownerToken = f.Issue("seller", "seller-colony");
                ModView(f, ownerToken, order.EventId, "Traded", mine: true);
                Check(Count(f) == 1, "Unclaimed trade released the listing slot.");
                Check(!f.Trades.ClaimTradeOrder(new("forged", order.EventId, new("seller", "seller-colony", "seller-base"), null, f.Token)).Result.Accepted,
                    "Another account claimed owner proceeds.");
                Check(!f.Trades.ClaimTradeOrder(new("other", order.EventId, new("userA", "colonyA", "paid"), null, f.Token)).Result.Accepted,
                    "Buyer claimed owner proceeds.");
                string otherColony = ownerToken;
                Check(!f.Trades.ClaimTradeOrder(new("other-colony", order.EventId, new("seller", "other-colony", "other-base"), null, otherColony)).Result.Accepted,
                    "Another colony claimed proceeds.");
                // Expiry and user requests race under the same mutation gate.
                f.TradeLifecycle.ApplyTradeOrderExpirations(info.ExpiresAtUtc!.Value.AddTicks(-1));
                Check(f.Ledger.Find(deliveryId)!.Status == ServerEventStatus.AwaitingCollection, "Collection expired early.");
                f.Configuration = new(tradeMarketplaceEnabled: false, tradeOrderExpiration: TimeSpan.Zero);
                Parallel.For(0, 12, i =>
                {
                    if (i % 2 == 0) f.TradeLifecycle.ApplyTradeOrderExpirations(info.ExpiresAtUtc.Value);
                    else Check(Claim(f, ownerToken, order.EventId).Result.Accepted, "Manual/timed collection race rejected the existing delivery.");
                });
                Check(f.Ledger.Find(deliveryId)!.Status == ServerEventStatus.PendingOfflineDelivery
                    && f.Ledger.ListByType(ServerEventType.ItemDelivery).Count == (pods ? 2 : 1), "Claim/timeout duplicated or failed to release the original delivery.");
                ModView(f, ownerToken, order.EventId, "Collecting", mine: true);
                Check(Count(f) == 1, "Queued but unapplied proceeds released the listing slot.");
                f.Ledger.MarkDelivered(deliveryId, "seller-base", DateTimeOffset.UtcNow);
                Check(Count(f) == 1 && Claim(f, ownerToken, order.EventId).Result.Accepted, "Downloading or retrying consumed the slot or reset delivery.");
                f.Ledger.MarkApplied(deliveryId, "received", DateTimeOffset.UtcNow);
                Check(Count(f) == 0 && Claim(f, ownerToken, order.EventId).Result.Accepted
                    && f.Ledger.Find(deliveryId)!.Status == ServerEventStatus.AppliedToSnapshot, "Final confirmation failed to free the slot or a repeated claim resurrected delivery.");
                ModView(f, ownerToken, order.EventId, "AppliedToSnapshot", mine: false);
            }
            finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        }
        NoDeadline(factory);
    }

    private static void NoDeadline(Func<string, SaveSnapshotPackage> factory)
    {
        var f = Fixture(factory, new InMemoryAuthoritativeEventLedger());
        f.Configuration = new(tradeOrderExpiration: TimeSpan.Zero);
        var order = Order(); f.Ledger.Append(order);
        var response = f.Trades.FulfillTradeOrderWithSnapshot(new(new("no-deadline", order.EventId, "", new("userA", "colonyA", "before"),
            [new("paid", "Silver", 10)], "ServerDropPod"), ProtocolDtoMapper.ToMetadataDto(f.Package)), f.Package.Payload, f.Token);
        Check(response.Result.Accepted, "No-deadline fulfillment failed.");
        var held = f.Ledger.Find(response.OwnerDeliveryEventId!)!;
        Check(((ItemDeliveryEventPayload)held.Payload).TradeCollection!.ExpiresAtUtc is null, "Disabled expiry became an immediate deadline.");
        f.TradeLifecycle.ApplyTradeOrderExpirations(DateTimeOffset.UtcNow.AddYears(10));
        Check(f.Ledger.Find(held.EventId)!.Status == ServerEventStatus.AwaitingCollection
            && Claim(f, f.Issue("seller", "seller-colony"), order.EventId).Result.Accepted, "No-deadline order auto-delivered or could not be claimed.");
    }

    private static void ModView(EconomicModuleTestContext f, string token, string id, string status, bool mine)
    {
        var minePage = f.Market.ListTradeOrders(new("seller", "seller-colony", "seller-base", "Mine", authToken: token));
        Check(minePage.Result.Accepted && minePage.Orders.Any(o => o.EventId == id) == mine, "Order missing from the correct owner scope.");
        var page = mine ? minePage : f.Market.ListTradeOrders(new("seller", "seller-colony", "seller-base", "History", authToken: token));
        Check(page.Orders.Single(o => o.EventId == id).Status == status, "Market reported the wrong collection status.");
        Check(!f.Market.ListTradeOrders(new("userA", "colonyA", "paid", authToken: f.Token)).Orders.Any(o => o.EventId == id), "Traded order remained purchasable.");
    }
    private static int Count(EconomicModuleTestContext f) => (int)typeof(TradeService)
        .GetMethod("CountOpenMarketTradeOrdersForOwner", BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(f.Trades, new object[] { new ProtocolIdentity("seller", "seller-colony", null) })!;
    private static CloseTradeOrderResponse Claim(EconomicModuleTestContext f, string token, string id) =>
        f.Trades.ClaimTradeOrder(new("claim:" + id, id, new("seller", "seller-colony", "seller-base"), null, token));
    private static EconomicModuleTestContext Fixture(Func<string, SaveSnapshotPackage> factory, IAuthoritativeEventLedger ledger) => new(factory, ledger: ledger)
    { Configuration = new(tradeOrderExpiration: TimeSpan.FromDays(7), maxOpenTradeOrdersPerOwner: 1) };
    private static AuthoritativeEvent Order() => AuthoritativeEventFactory.Create(ServerEventType.Trade, new("seller", "seller-colony"), new("server"), "order", false,
        new TradeEventPayload("order", TradeStage.MarketOrder, [new("steel", "Steel", 4, "seller-base")], [new("silver", "Silver", 10)], 0), DateTimeOffset.UtcNow.AddDays(-5));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
