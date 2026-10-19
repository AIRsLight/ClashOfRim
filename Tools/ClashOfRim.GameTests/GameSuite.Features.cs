using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIRsLight.ClashOfRim.Network;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using RimTest.Client;
using RimTest.Protocol;
using RimTest.Testing;


namespace ClashOfRim.GameTests;

internal sealed partial class GameSuite
{
    async Task Bank()
    {
        await Idle("A"); await Invoke("A", "StartRefreshBankStatus"); await Idle("A");
        var bank = await Call("A", "clash.bank");
        Check(bank.GetProperty("result").Bool("accepted") && bank.Bool("bankLoansEnabled"), "Bank unavailable.");
        int principal = Math.Max(bank.Int("minLoanSilver"), 100);
        Check(principal <= bank.Int("maxLoanSilver"), "Fixture colony cannot borrow minimum principal.");
        int silver = await colonyFixture.Stock("A", "Silver"); string previousSnapshot = (await Call("A", "clash.state")).String("currentSnapshotId");
        await Invoke("A", "StartCreateBankLoan", new { principalSilver = principal, durationDays = bank.Int("minDurationDays") });
        await Idle("A");
        bank = await Call("A", "clash.bank");
        Check(bank.GetProperty("activeLoan").ValueKind == JsonValueKind.Object, "Loan was not created.");
        Check(bank.GetProperty("activeLoan").Int("principalSilver") == principal, "Wrong loan principal.");
        await Step("A", 600); // Let the original drop pod land and open before counting spawned stock.
        Check(await colonyFixture.Stock("A", "Silver") == silver + principal, "Loan cash did not reach the map exactly once.");
        Check((await Call("A", "clash.state")).String("currentSnapshotId") != previousSnapshot, "Loan did not advance authoritative snapshot.");
        int due = bank.GetProperty("activeLoan").Int("totalDueSilver");
        await NoEconomicMutation("A", async () => { await Invoke("A", "StartCreateBankLoan", new { principalSilver = principal, durationDays = bank.Int("minDurationDays") }); });
        await Invoke("A", "StartRepayBankLoan"); await Idle("A");
        bank = await Call("A", "clash.bank");
        Check(bank.GetProperty("activeLoan").ValueKind == JsonValueKind.Null, "Loan remains active after repayment.");
        Check(await colonyFixture.Stock("A", "Silver") == silver + principal - due, "Repayment cash mismatch.");
        await NoEconomicMutation("A", async () => { await Invoke("A", "StartRepayBankLoan"); });
    }
    async Task PrivateChat()
    {
        await Invoke("A", "StartSendChatMessage", new { rawText = "@rimtest-B RimTest private exchange" });
        await Wait("A", "clash.state", null, s => !s.Bool("chatInProgress"));
        await Invoke("B", "StartRefreshChatMessages");
        var messages = await Wait("B", "clash.records", new { kind = "chat" }, p => Items(p).Any(v => v.GetProperty("data").GetRawText().Contains("RimTest private exchange")));
        var message = Items(messages).Single(v => v.GetProperty("data").GetRawText().Contains("RimTest private exchange")).GetProperty("data");
        Check(message.String("channel") == "Private" && message.String("targetUserId") == "rimtest-B", "Private chat routing changed.");
        Check((await Call("B", "clash.state")).Int("unreadPrivateChatCount") > 0, "Incoming private chat was not marked unread.");
        await Invoke("B", "MarkPrivateChatRead");
        Check((await Call("B", "clash.state")).Int("unreadPrivateChatCount") == 0, "Private chat read marker was not cleared.");
    }
    async Task AdminConfiguration()
    {
        await Invoke("A", "StartRefreshAdminStatus"); await Idle("A");
        var status = await Call("A", "clash.admin");
        Check(status.Bool("isAdministrator") && status.GetProperty("result").Bool("accepted"), "Initial owner is not administrator.");
        var original = status.GetProperty("configuration");
        var changed = JsonNode.Parse(original.GetRawText())!;
        changed["giftsEnabled"] = false;
        await Invoke("A", "StartUpdateAdminConfiguration", new { configuration = changed }); await Idle("A");
        await Invoke("A", "StartRefreshAdminStatus"); await Idle("A");
        Check(!(await Call("A", "clash.admin")).GetProperty("configuration").Bool("giftsEnabled"), "Administrator configuration did not persist.");
        await Invoke("B", "StartUpdateAdminConfiguration", new { configuration = original }); await Idle("B");
        await Invoke("A", "StartRefreshAdminStatus"); await Idle("A");
        Check(!(await Call("A", "clash.admin")).GetProperty("configuration").Bool("giftsEnabled"), "Non-administrator changed configuration.");
        await Invoke("A", "StartUpdateAdminConfiguration", new { configuration = original }); await Idle("A");
        await Invoke("A", "StartRefreshAdminStatus"); await Idle("A");
        Check((await Call("A", "clash.admin")).GetProperty("configuration").Bool("giftsEnabled") == original.Bool("giftsEnabled"), "Configuration restore failed.");
    }
    async Task Mercenary()
    {
        await Idle("B"); await Invoke("B", "StartRefreshBankStatus"); await Idle("B");
        var bank = await Call("B", "clash.bank"); int before = await colonyFixture.Stock("B", "Silver");
        await Invoke("B", "StartHireMercenary", new { skillDefName = "Mining", skillLevel = 7, durationDays = bank.Int("mercenaryMinDurationDays") });
        var contracts = await Wait("B", "clash.mercenaries", null, p => Items(p).Any(c => !c.Bool("completed")));
        await Idle("B"); await Step("B", 600);
        contracts = await Wait("B", "clash.mercenaries", null, p => Items(p).Any(c => c.Bool("spawned")));
        var contract = Items(contracts).Single(c => !c.Bool("completed"));
        Check(contract.Int("actualSkill") == 7 && contract.Int("priceSilver") > 0, "Wrong mercenary skill/price.");
        Check(await colonyFixture.Stock("B", "Silver") == before - contract.Int("priceSilver"), "Mercenary charge mismatch.");
        await Call("B", "pawn.draft", new { pawn = contract.GetProperty("pawn").String("handle"), drafted = true });
    }
    async Task PasswordChange()
    {
        var accounts = server.Services.GetRequiredService<ClashOfRimNetworkState>().OfflineAccounts;
        const string initial = "local-test-password", replacement = "rimtest-replacement-password";
        await Invoke("B", "StartChangeOfflinePassword", new { currentPassword = "incorrect-password", newPassword = replacement });
        await Wait("B", "clash.state", null, s => !s.Bool("accountPasswordInProgress"));
        Check(accounts.Authenticate("rimtest-B", initial, DateTimeOffset.UtcNow).Accepted, "Rejected password change altered credentials.");
        await Invoke("B", "StartChangeOfflinePassword", new { currentPassword = initial, newPassword = replacement });
        await Wait("B", "clash.state", null, s => !s.Bool("accountPasswordInProgress"));
        Check(!accounts.Authenticate("rimtest-B", initial, DateTimeOffset.UtcNow).Accepted, "Old password still accepted.");
        Check(accounts.Authenticate("rimtest-B", replacement, DateTimeOffset.UtcNow).Accepted, "New password rejected.");
        await Invoke("B", "StartChangeOfflinePassword", new { currentPassword = replacement, newPassword = initial });
        await Wait("B", "clash.state", null, s => !s.Bool("accountPasswordInProgress"));
        Check(accounts.Authenticate("rimtest-B", initial, DateTimeOffset.UtcNow).Accepted, "Password restore failed.");
        await run.RecordAsync(new { serverObservation = "password-change", wrongCurrentRejected = true, oldRejected = true, replacementAccepted = true, restored = true });
    }
    async Task Appearance()
    {
        var state = server.Services.GetRequiredService<ClashOfRimNetworkState>();
        var original = state.WorldConfiguration.Current!.PlayerColonySites.Single(s => s.UserId == "rimtest-A").Appearance;
        await Invoke("A", "SaveColonyAppearance", new { mode = "Color", iconDefName = (string?)null, colorDefName = (string?)null, colorHex = "38A4E8" });
        var watch = Stopwatch.StartNew();
        while (state.WorldConfiguration.Current!.PlayerColonySites.Single(s => s.UserId == "rimtest-A").Appearance?.ColorHex != "38A4E8" && watch.Elapsed.TotalSeconds < 90)
            await Task.Delay(100);
        Check(state.WorldConfiguration.Current!.PlayerColonySites.Single(s => s.UserId == "rimtest-A").Appearance?.ColorHex == "38A4E8", "Appearance was not registered on server.");
        await Invoke("B", "StartSyncWorldMapMarkers");
        await Wait("B", "clash.records", new { kind = "markers" }, p => Items(p).Any(v =>
            v.GetProperty("data").OptionalString("ownerUserId") == "rimtest-A" &&
            v.GetProperty("data").GetProperty("appearance").OptionalString("colorHex") == "38A4E8"));
        await Invoke("A", "SaveColonyAppearance", new { mode = original?.Mode, iconDefName = original?.IconDefName, colorDefName = original?.ColorDefName, colorHex = original?.ColorHex });
        watch.Restart();
        while (state.WorldConfiguration.Current!.PlayerColonySites.Single(s => s.UserId == "rimtest-A").Appearance?.ColorHex != original?.ColorHex && watch.Elapsed.TotalSeconds < 90)
            await Task.Delay(100);
        Check(state.WorldConfiguration.Current!.PlayerColonySites.Single(s => s.UserId == "rimtest-A").Appearance?.ColorHex == original?.ColorHex, "Appearance restore failed.");
    }
    async Task MercenaryRecall()
    {
        var contract = Items(await Call("B", "clash.mercenaries")).Single(c => !c.Bool("completed"));
        string id = contract.String("contractId");
        await Call("B", "pawn.draft", new { pawn = contract.GetProperty("pawn").String("handle"), drafted = false });
        Check((await Call("B", "clash.mercenary.recall", new { contract = H(contract) })).Bool("accepted"), "Recall action rejected.");
        await Step("B", 600);
        contract = Items(await Call("B", "clash.mercenaries")).Single(c => c.String("contractId") == id);
        string shuttle = contract.GetProperty("shuttle").String("handle");
        await Call("B", "selection.set", new { handle = shuttle });
        string loadLabel = (await Call("B", "clash.labels")).String("load");
        var load = Items(await Call("B", "gizmos.list", new { handle = shuttle })).Single(g => g.String("label") == loadLabel && !g.Bool("disabled"));
        await Call("B", "gizmos.invoke", new { handle = shuttle, type = load.String("type"), label = load.String("label") });
        await Call("B", "time.run", new { tps = 1200 });
        try { await Wait("B", "clash.mercenaries", null, p => !Items(p).Any(c => c.String("contractId") == id && !c.Bool("completed"))); }
        finally { await Call("B", "time.cancel"); }
        await Idle("B");
        var state = server.Services.GetRequiredService<ClashOfRimNetworkState>();
        var watch = Stopwatch.StartNew();
        while (state.MercenaryContracts.Find(id)?.Status != MercenaryContractRegistry.StatusCompleted && watch.Elapsed.TotalSeconds < 90)
            await Task.Delay(100);
        Check(state.MercenaryContracts.Find(id)?.Status == MercenaryContractRegistry.StatusCompleted, "Recall did not close the authoritative contract.");
        await run.RecordAsync(new { serverObservation = "mercenary-recall", contractId = id, status = state.MercenaryContracts.Find(id)?.Status });
    }
    Task Shop() => Shop(preserveListing: false);
    async Task Shop(bool preserveListing)
    {
        await Idle("A");
        var template = await Call("A", "clash.shop.template", new { defName = "Steel", count = 5 });
        await Invoke("A", "StartUpsertServerShopListing", new { listingId = (string?)null, listingKind = "SellToPlayer", item = template.GetProperty("reference"), priceSilver = 10, stockCount = 3 });
        await Wait("A", "clash.state", null, s => !s.Bool("adminInProgress") && !s.Bool("serverShopInProgress"));
        await Invoke("B", "StartRefreshServerShop");
        var shop = await Wait("B", "clash.records", new { kind = "shop" }, s => Items(s).Any(v => v.GetProperty("data").GetProperty("item").String("defName") == "Steel"));
        var listing = Items(shop).Single(v => v.GetProperty("data").GetProperty("item").String("defName") == "Steel");
        int steel = await colonyFixture.Stock("B", "Steel"), silver = await colonyFixture.Stock("B", "Silver");
        Check(silver >= 10, "Purchase fixture lacks landed silver.");
        await Invoke("B", "StartPurchaseServerShopListing", new { listing = listing.GetProperty("reference"), purchaseCount = 1 });
        await Idle("B"); await Step("B", 600);
        int actualSteel = await colonyFixture.Stock("B", "Steel"), actualSilver = await colonyFixture.Stock("B", "Silver");
        Check(actualSteel == steel + 5 && actualSilver == silver - 10, $"Shop stock mismatch: steel {steel} -> {actualSteel}, silver {silver} -> {actualSilver}.");
        await Invoke("A", "StartRefreshServerShop");
        shop = await Wait("A", "clash.records", new { kind = "shop" }, s => Items(s).Any(v => v.GetProperty("data").String("listingId") == listing.GetProperty("data").String("listingId") && v.GetProperty("data").Int("stockCount") == 2));
        var refreshed = Items(shop).Single(v => v.GetProperty("data").String("listingId") == listing.GetProperty("data").String("listingId"));
        if (preserveListing) return;
        await Invoke("A", "StartRemoveServerShopListing", new { listing = refreshed.GetProperty("reference") });
        await Wait("A", "clash.records", new { kind = "shop" }, s => !Items(s).Any(v => v.GetProperty("data").String("listingId") == listing.GetProperty("data").String("listingId")));
    }
    async Task<JsonElement> CreateTrade(IReadOnlyList<string>? offerReferences = null, string client = "A")
    {
        await Idle(client);
        await Invoke(client, "OpenTradeOrderDialog");
        var windows = Items(await Call(client, "ui.windows"));
        string window = H(windows.Single(w => w.GetProperty("reference").String("type").EndsWith(".TradeOrderDialogWindow")));
        await Call(client, "clash.trade.beacon", new { window, required = false });
        if (offerReferences is null)
        {
            var steel = Items(await Call(client, "clash.trade.candidates", new { window, limit = 200 })).First(t => t.String("defName") == "Steel" && t.Int("count") >= 5);
            await Call(client, "clash.trade.offer", new { window, thing = H(steel), count = 5 });
        }
        else foreach (string thing in offerReferences) await Call(client, "clash.trade.offer", new { window, thing, count = 1 });
        await Call(client, "clash.trade.request", new { window, defName = "WoodLog", count = 5, minimumHitPointsPercent = 50 });
        await Call(client, "clash.trade.submit", new { window });
        await Wait(client, "dialogs.messages", null, p => Items(p).Length == 1);
        await Confirm(client); await Idle(client);
        await Invoke(client, "StartRefreshTradeOrders", new { scope = "Mine" });
        var orders = await Wait(client, "clash.records", new { kind = "trades" }, p => Items(p).Length > 0);
        return Items(orders).Single();
    }
    async Task TradeCancel()
    {
        await colonyFixture.StoreStartingResources("A"); await colonyFixture.StoreStartingResources("B");
        int steel = await colonyFixture.Stock("A", "Steel");
        var order = await CreateTrade();
        Check(await colonyFixture.Stock("A", "Steel") == steel - 5, "Trade creation did not reserve offered stock.");
        await Invoke("A", "StartCancelTradeOrder", new { order = order.GetProperty("reference") });
        await Wait("A", "clash.records", new { kind = "trades" }, p => Items(p).Length == 0);
        await colonyFixture.AwaitStock("A", "Steel", steel); await Idle("A");
    }
    async Task TradeFulfill()
    {
        int steelA = await colonyFixture.Stock("A", "Steel"), steelB = await colonyFixture.Stock("B", "Steel"), woodA = await colonyFixture.Stock("A", "WoodLog"), woodB = await colonyFixture.Stock("B", "WoodLog");
        var order = await CreateTrade(); string id = order.GetProperty("data").String("eventId");
        await Invoke("B", "StartRefreshTradeOrders");
        var listed = await Wait("B", "clash.records", new { kind = "trades" }, p => Items(p).Any(v => v.GetProperty("data").String("eventId") == id));
        var buyerOrder = Items(listed).Single(v => v.GetProperty("data").String("eventId") == id);
        Check(buyerOrder.GetProperty("data").GetProperty("serverDropPodPostage").Bool("reachable"), "Trade delivery quote is unreachable.");
        await Invoke("B", "StartFulfillTradeOrderByDropPod", new { order = buyerOrder.GetProperty("reference") });
        await Idle("B"); await colonyFixture.AwaitStock("A", "WoodLog", woodA + 5); await colonyFixture.AwaitStock("B", "Steel", steelB + 5);
        Check(await colonyFixture.Stock("A", "Steel") == steelA - 5 && await colonyFixture.Stock("B", "WoodLog") == woodB - 5, "Trade did not conserve offered/requested items.");
        await Idle("A"); await Idle("B");
    }
    async Task TradeCaravanFulfill()
    {
        await Idle("A"); await Idle("B"); await colonyFixture.StoreStartingResources("B");
        int steel = await caravanFixture.Stock("Steel"), wood = await caravanFixture.Stock("WoodLog");
        int sellerSteel = await colonyFixture.Stock("B", "Steel"), sellerWood = await colonyFixture.Stock("B", "WoodLog");
        string previous = (await Call("A", "clash.state")).String("currentSnapshotId");
        var order = await CreateTrade(client: "B"); string id = order.GetProperty("data").String("eventId");
        await Invoke("A", "StartRefreshTradeOrders"); await Idle("A");
        var listed = await Wait("A", "clash.records", new { kind = "trades" }, p => Items(p).Any(v => v.GetProperty("data").String("eventId") == id));
        var buyer = Items(listed).Single(v => v.GetProperty("data").String("eventId") == id);
        await Invoke("A", "StartAcceptTradeOrder", new { order = buyer.GetProperty("reference"), postagePaidByAcceptor = false }); await Idle("A");
        listed = await Wait("A", "clash.records", new { kind = "trades" }, p => Items(p).Any(v => v.GetProperty("data").String("eventId") == id && v.GetProperty("data").Bool("viewerHasAccepted")));
        buyer = Items(listed).Single(v => v.GetProperty("data").String("eventId") == id);
        Check(await caravanFixture.Stock("WoodLog") == wood && await caravanFixture.Stock("Steel") == steel, "Accepting a memo changed caravan cargo.");
        await Invoke("A", "OpenCaravanTradeFulfillmentMenu", new { caravan = new { handle = caravanFixture.Handle }, orders = new[] { buyer.GetProperty("reference") } });
        await dialogs.ChooseOnlyMenu("A"); await Confirm("A"); await Idle("A");
        Check(await caravanFixture.Stock("WoodLog") == wood - 5 && await caravanFixture.Stock("Steel") == steel + 5,
            "Caravan fulfillment lost or duplicated exchanged cargo.");
        Check((await Call("A", "clash.state")).String("currentSnapshotId") != previous, "Caravan exchange did not commit a snapshot.");
        await colonyFixture.AwaitStock("B", "WoodLog", sellerWood + 5); await Idle("B");
        Check(await colonyFixture.Stock("B", "Steel") == sellerSteel - 5, "Caravan seller escrow was not conserved.");
    }
    async Task Alliance()
    {
        await Idle("A"); await Invoke("A", "StartRefreshPlayers"); await Idle("A");
        var players = await Wait("A", "clash.records", new { kind = "players" }, p => Items(p).Any(v => v.GetProperty("data").String("userId") == "rimtest-B"));
        var target = Items(players).Single(v => v.GetProperty("data").String("userId") == "rimtest-B");
        await Invoke("A", "StartCreateDiplomacyEvent", new { kind = "AllianceRequest", target = target.GetProperty("reference"), customMessage = "RimTest alliance" });
        await Idle("A");
        var events = await Wait("B", "clash.events", new { limit = 200 }, p => Items(p).Any(v => v.GetProperty("data").String("payloadSummary").Contains("AllianceRequest")));
        var request = Items(events).Single(v => v.GetProperty("data").String("payloadSummary").Contains("AllianceRequest"));
        await Invoke("B", "HandleEventLetterAction", new { eventId = request.GetProperty("data").String("eventId"), actionKind = "Accept" });
        await Idle("B");
        await Invoke("A", "StartRefreshPlayers"); await Idle("A");
        await Wait("A", "clash.records", new { kind = "players" }, p => Items(p).Any(v => v.GetProperty("data").String("userId") == "rimtest-B" && v.GetProperty("data").OptionalString("relationKind") == "Ally"));
    }
    async Task Raid()
    {
        await Idle("A"); await Idle("B");
        await Invoke("A", "StartRefreshAdminStatus"); await Idle("A");
        var original = (await Call("A", "clash.admin")).GetProperty("configuration");
        var configuration = JsonNode.Parse(original.GetRawText())!;
        configuration["diplomacyRelationChangeCooldownHours"] = 0;
        configuration["raidMinimumDefenderWealth"] = 0;
        configuration["raidProtectionHours"] = 0;
        configuration["pvpEnabled"] = true;
        if (profile.Stability == "transfers") configuration["raidSettlementLossRatio"] = 1;
        await Invoke("A", "StartUpdateAdminConfiguration", new { configuration }); await Idle("A");
        var state = server.Services.GetRequiredService<ClashOfRimNetworkState>();
        foreach (var (kind, expected) in new[] { ("AllianceCancellation", "Neutral"), ("WarDeclaration", "Hostile") })
        {
            await Invoke("A", "StartRefreshPlayers"); await Idle("A");
            var target = Items(await Call("A", "clash.records", new { kind = "players" })).Single(v => v.GetProperty("data").String("userId") == "rimtest-B");
            await Invoke("A", "StartCreateDiplomacyEvent", new { kind, target = target.GetProperty("reference"), customMessage = "RimTest raid preparation" });
            await Idle("A");
            var watch = Stopwatch.StartNew();
            while (state.DiplomacyRelations.GetRelationKind("rimtest-A", "colony-rimtest-A", "rimtest-B", "colony-rimtest-B") != expected && watch.Elapsed.TotalSeconds < 30)
                await Task.Delay(100);
            Check(state.DiplomacyRelations.GetRelationKind("rimtest-A", "colony-rimtest-A", "rimtest-B", "colony-rimtest-B") == expected, "Diplomacy transition failed: " + kind);
        }
        await Invoke("B", "StartManualSnapshotUpload"); await Idle("B");
        string defenderSnapshot = state.SnapshotStore.GetLatest("rimtest-B", "colony-rimtest-B")!.Identity.SnapshotId ?? throw new Exception("Missing persisted defender snapshot identity.");
        await entry.ExitSession("B");
        // Refresh the same world catalog a player sees until the logout reaches the server.
        JsonElement marker = default;
        for (int i = 0; i < 30; i++)
        {
            await Invoke("A", "StartSyncWorldMapMarkers"); await Task.Delay(500);
            var candidates = Items(await Call("A", "clash.records", new { kind = "markers" })).Where(m => m.GetProperty("data").String("ownerUserId") == "rimtest-B" && m.GetProperty("data").Bool("canRaid")).ToArray();
            if (candidates.Length == 1) { marker = candidates[0]; break; }
        }
        Check(marker.ValueKind == JsonValueKind.Object, "Offline hostile colony did not become raidable.");
        var caravan = Items(await Call("A", "caravans.list")).Single(c => c.GetProperty("pawns").GetArrayLength() == 1);
        caravanFixture.Use(caravan);
        await Call("A", "world.select", new { target = caravanFixture.Handle });
        string raidLabel = (await Call("A", "clash.labels")).String("raid");
        var raid = Items(await Call("A", "world.gizmos", new { target = caravanFixture.Handle })).Single(g => g.String("label") == raidLabel && !g.Bool("disabled"));
        await Call("A", "world.invokeGizmo", new { target = caravanFixture.Handle, type = raid.String("type"), label = raid.String("label") });
        await dialogs.ChooseOnlyMenu("A"); await Confirm("A");
        var remote = await Wait("A", "clash.remote", null, r => r.Bool("active") && r.GetProperty("session").Bool("isRaidBattle"));
        string eventId = remote.GetProperty("session").String("relatedEventId");
        Check(remote.GetProperty("session").String("targetSnapshotId") == defenderSnapshot, "Raid loaded the wrong defender baseline.");
        await Idle("A"); // The map opens before the departure snapshot transaction ends.
        JsonElement loss = default;
        if (profile.Stability == "transfers")
        {
            loss = await Call("A", "clash.fixture.remoteLoss", new { acknowledgeFixture = true });
            await remoteMaps.ReloadBattle(eventId);
        }
        // Explicitly test finishing with the attack force still present (the original warning is retained).
        Check((await Call("A", "clash.remote.close")).Bool("invoked"), "Raid finish action rejected.");
        await Confirm("A");
        await Wait("A", "clash.remote", null, r => !r.Bool("active")); await Idle("A");
        var settlementWait = Stopwatch.StartNew();
        while (state.Ledger.Find(eventId)?.Status.ToString() != "AppliedToSnapshot" && settlementWait.Elapsed.TotalSeconds < 90)
            await Task.Delay(100);
        Check(state.Ledger.Find(eventId)?.Status.ToString() == "AppliedToSnapshot", "Raid event did not settle authoritatively: " + state.Ledger.Find(eventId)?.Status);
        await run.RecordAsync(new { serverObservation = "raid-settlement", eventId, status = state.Ledger.Find(eventId)?.Status.ToString() });
        Check(state.SnapshotStore.GetLatest("rimtest-B", "colony-rimtest-B")!.Identity.SnapshotId != defenderSnapshot, "Defender settlement did not publish a snapshot.");
        Check((await Call("A", "game.state")).Int("mapCount") == 1, "Battle map remained after settlement.");
        if (profile.Stability == "transfers")
        {
            string settled = state.SnapshotStore.GetLatest("rimtest-B", "colony-rimtest-B")!.Identity.SnapshotId!;
            int events = state.Ledger.ListAll().Count;
            Check(!(await Call("A", "clash.remote.close")).Bool("invoked"), "Closed battle accepted a second settlement.");
            await Idle("A");
            Check(state.Ledger.ListAll().Count == events && state.SnapshotStore.GetLatest("rimtest-B", "colony-rimtest-B")!.Identity.SnapshotId == settled, "Repeated finish mutated settled state.");
            raidLoss = loss.GetProperty("before");
        }
        await Invoke("A", "StartUpdateAdminConfiguration", new { configuration = original }); await Idle("A");
    }
}
