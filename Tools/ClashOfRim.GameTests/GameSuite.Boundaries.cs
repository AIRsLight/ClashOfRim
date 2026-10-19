using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using Microsoft.Extensions.DependencyInjection;
using RimTest.Protocol;

namespace ClashOfRim.GameTests;

internal sealed partial class GameSuite
{
    ClashOfRimNetworkState ServerState => server.Services.GetRequiredService<ClashOfRimNetworkState>();
    async Task NoEconomicMutation(string client, Func<Task> action)
    {
        await Idle(client);
        int silver = await colonyFixture.Stock(client, "Silver"), steel = await colonyFixture.Stock(client, "Steel");
        string snapshot = (await Call(client, "clash.state")).String("currentSnapshotId");
        await action(); await Idle(client);
        Check(await colonyFixture.Stock(client, "Silver") == silver && await colonyFixture.Stock(client, "Steel") == steel, "Rejected operation changed stock.");
        Check((await Call(client, "clash.state")).String("currentSnapshotId") == snapshot, "Rejected operation published a snapshot.");
    }
    async Task BankBounds()
    {
        await Invoke("A", "StartRefreshBankStatus"); await Idle("A");
        var bank = await Call("A", "clash.bank");
        foreach (var (amount, days) in new[] { (0, bank.Int("minDurationDays")), (-1, bank.Int("minDurationDays")),
            (int.MaxValue, bank.Int("minDurationDays")), (bank.Int("minLoanSilver"), bank.Int("minDurationDays") - 1),
            (bank.Int("minLoanSilver"), bank.Int("maxDurationDays") + 1) })
            await NoEconomicMutation("A", async () => { await Invoke("A", "StartCreateBankLoan", new { principalSilver = amount, durationDays = days }); });
        Check((await Call("A", "clash.bank")).GetProperty("activeLoan").ValueKind == JsonValueKind.Null, "Invalid bank input created a loan.");
    }
    async Task UnicodeChat()
    {
        const string message = "RimTest 中文 Français Türkçe İı 日本語 한국어 😀 1,25 / 1.25";
        await Invoke("A", "StartSendChatMessage", new { rawText = message });
        await Wait("A", "clash.state", null, s => !s.Bool("chatInProgress"));
        await Invoke("B", "StartRefreshChatMessages");
        var received = await Wait("B", "clash.records", new { kind = "chat", limit = 200 }, p => Items(p).Any(v => v.GetProperty("data").OptionalString("text") == message));
        Check(Items(received).Count(v => v.GetProperty("data").OptionalString("text") == message) == 1, "Unicode message duplicated.");
        string snapshot = (await Call("A", "clash.state")).String("currentSnapshotId");
        await Invoke("A", "StartSendChatMessage", new { rawText = " \t " });
        await Wait("A", "clash.state", null, s => !s.Bool("chatInProgress"));
        await Invoke("B", "StartRefreshChatMessages");
        var after = await Call("B", "clash.records", new { kind = "chat", limit = 200 });
        Check(!Items(after).Any(v => string.IsNullOrWhiteSpace(v.GetProperty("data").OptionalString("text"))), "Blank chat entered the feed.");
        Check((await Call("A", "clash.state")).String("currentSnapshotId") == snapshot, "Chat mutated colony snapshot.");
    }
    async Task AdminGuards()
    {
        var state = ServerState; int audits = state.AdminControl.ListAudit().Count;
        foreach (string kind in new[] { "LockMaintenance", "PromoteAdmin", "Ban", "Kick", "DeletePlayerSave", "ResetOfflinePassword" })
        {
            await Invoke("B", "StartAdminAction", new { actionKind = kind, targetUserId = "rimtest-A", message = "unauthorized" }); await Idle("B");
        }
        Check(state.AdminControl.ListAudit().Count == audits && !state.AdminControl.MaintenanceLoginLocked && !state.AdminControl.IsBanned("rimtest-A"), "Unauthorized admin action changed server state.");
        Check((await Call("A", "clash.state")).Bool("isInActiveMultiplayerSession"), "Unauthorized admin action kicked owner.");
        foreach (string kind in new[] { "Kick", "Ban", "RevokeAdmin", "DeletePlayerSave" })
        {
            await Invoke("A", "StartAdminAction", new { actionKind = kind, targetUserId = "rimtest-A" }); await Idle("A");
        }
        Check(state.AdminControl.ListAudit().Count == audits && state.WorldConfiguration.IsAdministrator("rimtest-A"), "Self-target protection failed.");
    }
    async Task AdminRoles()
    {
        await Invoke("A", "StartAdminAction", new { actionKind = "PromoteAdmin", targetUserId = "rimtest-B" }); await Idle("A");
        try
        {
            await Wait("B", "clash.state", null, s => s.Bool("isAdministrator"));
            await Invoke("B", "StartRefreshAdminStatus"); await Idle("B");
            await Wait("B", "clash.admin", null, s => s.ValueKind == JsonValueKind.Object && s.Bool("isAdministrator"));
            Check(ServerState.WorldConfiguration.IsAdministrator("rimtest-B"), "Promotion was not authoritative.");
        }
        finally { await Invoke("A", "StartAdminAction", new { actionKind = "RevokeAdmin", targetUserId = "rimtest-B" }); await Idle("A"); }
        await Wait("B", "clash.state", null, s => !s.Bool("isAdministrator"));
        Check(!ServerState.WorldConfiguration.IsAdministrator("rimtest-B"), "Revocation was not authoritative.");
    }
    async Task AdminBroadcast()
    {
        const string message = "RimTest notice 中文 Français 1,25";
        await Invoke("A", "StartAdminAction", new { actionKind = "Broadcast", targetUserId = "rimtest-B", message, persistentNotification = true }); await Idle("A");
        await Invoke("B", "StartRefreshEventsForTest");
        await Wait("B", "clash.events", new { limit = 200 }, p => Items(p).Any(v => NotificationScenario.HasMessage(v, message)));
        Check(ServerState.AdminControl.ListAudit().Any(a => a.ActionKind == "Broadcast" && a.Message == message), "Broadcast audit missing.");
    }
    async Task ShopBuyback()
    {
        await colonyFixture.StoreStartingResources("B");
        var template = await Call("A", "clash.shop.template", new { defName = "Steel", count = 5 });
        await Invoke("A", "StartUpsertServerShopListing", new { listingId = (string?)null, listingKind = "BuyFromPlayer", item = template.GetProperty("reference"), priceSilver = 10, stockCount = 1 }); await Idle("A");
        await Invoke("B", "StartRefreshServerShop");
        var shop = await Wait("B", "clash.records", new { kind = "shop" }, s => Items(s).Any(v => v.GetProperty("data").OptionalString("listingKind") == "BuyFromPlayer"));
        var listing = Items(shop).Single(v => v.GetProperty("data").OptionalString("listingKind") == "BuyFromPlayer");
        string id = listing.GetProperty("data").String("listingId");
        await NoEconomicMutation("B", async () => { await Invoke("B", "StartPurchaseServerShopListing", new { listing = listing.GetProperty("reference"), purchaseCount = 2 }); });
        int silver = await colonyFixture.Stock("B", "Silver"), steel = await colonyFixture.Stock("B", "Steel");
        await Invoke("B", "StartPurchaseServerShopListing", new { listing = listing.GetProperty("reference"), purchaseCount = 1 }); await Idle("B");
        await colonyFixture.AwaitStock("B", "Silver", silver + 10);
        Check(await colonyFixture.Stock("B", "Steel") == steel - 5, "Buyback did not remove exact items.");
        await Invoke("B", "StartRefreshServerShop");
        var exhausted = await Wait("B", "clash.records", new { kind = "shop" }, p => Items(p).Any(v => v.GetProperty("data").String("listingId") == id && v.GetProperty("data").Int("stockCount") == 0));
        listing = Items(exhausted).Single(v => v.GetProperty("data").String("listingId") == id);
        await NoEconomicMutation("B", async () => { await Invoke("B", "StartPurchaseServerShopListing", new { listing = listing.GetProperty("reference"), purchaseCount = 1 }); });
        await Invoke("A", "StartRefreshServerShop"); await Idle("A");
        var current = Items(await Call("A", "clash.records", new { kind = "shop" })).Single(v => v.GetProperty("data").String("listingId") == id);
        Check(current.GetProperty("data").Int("stockCount") == 0, "Buyback stock not exhausted.");
        await Invoke("A", "StartRemoveServerShopListing", new { listing = current.GetProperty("reference") }); await Idle("A");
    }
    async Task TradeMemo()
    {
        int steel = await colonyFixture.Stock("A", "Steel"), buyerSteel = await colonyFixture.Stock("B", "Steel"), buyerWood = await colonyFixture.Stock("B", "WoodLog");
        var order = await CreateTrade(); string id = order.GetProperty("data").String("eventId");
        await Invoke("B", "StartRefreshTradeOrders");
        var orders = await Wait("B", "clash.records", new { kind = "trades" }, p => Items(p).Any(v => v.GetProperty("data").String("eventId") == id));
        var buyer = Items(orders).Single(v => v.GetProperty("data").String("eventId") == id);
        await Invoke("B", "StartAcceptTradeOrder", new { order = buyer.GetProperty("reference"), postagePaidByAcceptor = false }); await Idle("B");
        orders = await Wait("B", "clash.records", new { kind = "trades" }, p => Items(p).Any(v => v.GetProperty("data").String("eventId") == id && v.GetProperty("data").Bool("viewerHasAccepted")));
        buyer = Items(orders).Single(v => v.GetProperty("data").String("eventId") == id);
        Check(await colonyFixture.Stock("B", "Steel") == buyerSteel && await colonyFixture.Stock("B", "WoodLog") == buyerWood, "Accepting memo prematurely delivered trade goods.");
        await Invoke("B", "StartCancelAcceptedTradeMemo", new { order = buyer.GetProperty("reference") }); await Idle("B");
        await Wait("B", "clash.records", new { kind = "trades" }, p => Items(p).Any(v => v.GetProperty("data").String("eventId") == id && !v.GetProperty("data").Bool("viewerHasAccepted")));
        await Idle("A");
        await Invoke("A", "StartRefreshTradeOrders", new { scope = "Mine" });
        var remaining = await Wait("A", "clash.records", new { kind = "trades" }, p => Items(p).Any(v => v.GetProperty("data").String("eventId") == id && v.GetProperty("data").Int("acceptedMemoCount") == 0));
        order = Items(remaining).Single(v => v.GetProperty("data").String("eventId") == id);
        Check(order.GetProperty("data").Int("acceptedMemoCount") == 0 && await colonyFixture.Stock("A", "Steel") == steel - 5, "Cancelling memo changed seller escrow/order.");
        await Invoke("A", "StartCancelTradeOrder", new { order = order.GetProperty("reference") }); await Idle("A"); await colonyFixture.AwaitStock("A", "Steel", steel);
    }
    async Task Achievements()
    {
        // The first fulfilled order was tested above. Reach the shipped threshold by
        // nine further real deliveries; never insert achievement rows directly.
        for (int i = 1; i < 10; i++) await TradeFulfill();
        await Invoke("A", "StartRefreshAchievements");
        var own = await Wait("A", "clash.records", new { kind = "achievements" }, p => Items(p).Any(v => v.GetProperty("data").String("achievementId") == "trades_10"));
        var achievement = Items(own).Single(v => v.GetProperty("data").String("achievementId") == "trades_10").GetProperty("data");
        Check(achievement.Int("value") >= 10, "Trade achievement has wrong progress.");
        string colony = (await Call("A", "clash.state")).String("colonyId");
        var records = ServerState.Achievements.ListAggregates().Where(a => a.UserId == "rimtest-A" && a.ColonyId == colony && a.AchievementId == "trades_10").ToArray();
        Check(records.Length == 1 && records[0].Value == achievement.Int("value"), "Achievement UI differs from authoritative aggregate.");
        await Invoke("B", "StartRefreshAchievements");
        await Wait("B", "clash.records", new { kind = "leaderboards" }, p => Items(p).Any(v => v.GetProperty("data").String("achievementId") == "trades_10" &&
            v.GetProperty("data").GetProperty("entries").EnumerateArray().Any(e => e.OptionalString("userId") == "rimtest-A")));
    }
}
