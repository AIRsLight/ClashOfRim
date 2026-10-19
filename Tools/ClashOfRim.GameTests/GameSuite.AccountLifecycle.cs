using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using RimTest.Protocol;

namespace ClashOfRim.GameTests;

internal sealed partial class GameSuite
{
    private readonly Dictionary<string, string> abandonedColonies = new();
    private string lifecycleOrder = "", lifecycleDebt = "";
    private JsonElement preservedShop;

    async Task AccountLifecycle()
    {
        await Case("account-password-change", PasswordChange);
        await Case("account-achievements-before-abandon", async () =>
        {
            // Two generations each perform ten real shipments, plus a hire. Fund
            // the buyer explicitly so postage cannot mask lifecycle assertions.
            await Call("B", "clash.fixture.bankCash", new { amount = 10000, acknowledgeFixture = true });
            await colonyFixture.StoreStartingResources("A"); await colonyFixture.StoreStartingResources("B");
            await trades.Fulfill(); await Achievements();
            await CheckAchievementRefresh("A"); await CheckAchievementRefresh("B");
            // Re-upload and refresh the same cumulative history: it must not count deliveries twice.
            var before = JsonSerializer.Serialize(ServerState.Achievements.ListAggregates());
            for (int i = 0; i < 2; i++) { await Invoke("A", "StartManualSnapshotUpload"); await Idle("A"); }
            Check(before == JsonSerializer.Serialize(ServerState.Achievements.ListAggregates()), "Uploads replayed achievement accounting.");
        });
        await Case("account-liabilities-before-abandon", async () =>
        {
            await PrivateChat(); await shop.Purchase(preserveListing: true); await Alliance();
            preservedShop = await Call("A", "clash.records", new { kind = "shop" });
            Check(Items(preservedShop).Length == 1, "Expected a surviving global listing.");
            foreach (string client in clients.Keys)
            {
                var bank = await economy.RefreshBank(client);
                await Invoke(client, "StartCreateBankLoan", new { principalSilver = bank.Int("minLoanSilver"), durationDays = bank.Int("minDurationDays") });
                await Idle(client); await Step(client, 600);
                Check((await economy.RefreshBank(client)).GetProperty("activeLoan").ValueKind == JsonValueKind.Object, "Missing lifecycle loan.");
            }
            await mercenaries.Hire();
            var contract = Items(await Call("B", "clash.mercenaries")).Single(c => !c.Bool("completed"));
            await Call("B", "clash.fixture.mercenaryDeath", new { contractId = contract.String("contractId"), acknowledgeFixture = true });
            var debt = await Wait("B", "clash.bank", null, b => b.GetProperty("openDebts").GetArrayLength() == 1);
            lifecycleDebt = debt.GetProperty("openDebts")[0].String("debtId"); await Idle("B");
            await mercenaries.Hire(); // Leave a second contract active when its colony is abandoned.
            var order = await tradeActions.Create(); lifecycleOrder = order.EventId;
            await tradeActions.AcceptMemo(await tradeActions.Refresh("B", lifecycleOrder));
        });
        await Case("account-abandon-owner", () => AbandonAccountColony("A", "B"));
        await Case("account-rebuild-owner", () => RebuildAccountColony("A", "B"));
        await Case("account-achievements-new-generation", async () =>
        {
            await colonyFixture.StoreStartingResources("A");
            await trades.Fulfill();
            var own = await CheckAchievementRefresh("A");
            Check(!Items(own).Any(v => v.GetProperty("data").String("achievementId") == "trades_10"), "New colony inherited old completed trade count.");
            await Achievements(); await CheckAchievementRefresh("A"); await CheckAchievementRefresh("B");
            await colonyFixture.Reload("A", "rimtest-rebuilt-achievements"); await CheckAchievementRefresh("A");
        });
        await Case("account-abandon-guest", () => AbandonAccountColony("B", "A"));
        await Case("account-rebuild-guest", () => RebuildAccountColony("B", "A"));
    }

    async Task<JsonElement> CheckAchievementRefresh(string client, string? targetClient = null, string? targetColony = null)
    {
        int version = (await Call(client, "clash.achievement.version")).Int("version");
        await Invoke(client, "StartRefreshAchievements", new { targetUserId = targetClient is null ? null : "rimtest-" + targetClient, targetColonyId = targetColony });
        await Wait(client, "clash.achievement.version", null, v => v.Int("version") > version);
        var boards = Items(await Call(client, "clash.records", new { kind = "leaderboards" }));
        var active = ServerState.SnapshotStore.ListLatest().Select(s => (s.Identity.OwnerId, s.Identity.ColonyId)).ToHashSet();
        var expected = ServerState.Achievements.ListAggregates().Where(a => active.Contains((a.UserId, a.ColonyId))).ToArray();
        Check(boards.Length == expected.Select(a => a.AchievementId).Distinct().Count(), "Leaderboard omitted an active achievement category.");
        foreach (var board in boards)
        {
            var entries = board.GetProperty("data").GetProperty("entries").EnumerateArray().ToArray();
            Check(entries.Length == Math.Min(10, expected.Count(a => a.AchievementId == board.GetProperty("data").String("achievementId"))), "Leaderboard omitted active colonies.");
            Check(entries.Length <= 10 && entries.Select(e => e.String("userId") + "/" + e.String("colonyId")).Distinct().Count() == entries.Length, "Leaderboard contains duplicate/excess entries.");
            var sorted = entries.OrderByDescending(e => e.GetProperty("value").GetInt64()).ThenBy(e => e.String("userId"), StringComparer.Ordinal).ThenBy(e => e.String("colonyId"), StringComparer.Ordinal).ToArray();
            Check(entries.Select(e => e.GetRawText()).SequenceEqual(sorted.Select(e => e.GetRawText())), "Leaderboard order is unstable.");
            foreach (var entry in entries)
            {
                string user = entry.String("userId"), colony = entry.String("colonyId");
                Check(!ServerState.Players.IsDeleted(user, colony), "Deleted colony leaked into leaderboard.");
                var aggregate = ServerState.Achievements.ListAggregates().Single(a => a.UserId == user && a.ColonyId == colony && a.AchievementId == board.GetProperty("data").String("achievementId"));
                Check(aggregate.Value == entry.GetProperty("value").GetInt64(), "Leaderboard differs from authoritative value.");
            }
        }
        return await Call(client, "clash.records", new { kind = "achievements" });
    }

    async Task AbandonAccountColony(string client, string survivor)
    {
        await Idle(client); await Idle(survivor);
        string user = "rimtest-" + client;
        var state = await Call(client, "clash.state"); string colony = state.String("colonyId");
        abandonedColonies[client] = colony;
        var other = await Call(survivor, "clash.state"); string otherColony = other.String("colonyId");
        string? survivorSnapshot = ServerState.SnapshotStore.GetLatest("rimtest-" + survivor, otherColony)?.Identity.SnapshotId;
        int silver = await colonyFixture.Stock(survivor, "Silver"), steel = await colonyFixture.Stock(survivor, "Steel");
        var history = JsonSerializer.Serialize(ServerState.Achievements.ListAggregates().Where(a => a.UserId == user));
        await Call(client, "clash.abandon.open");
        Check(!(await Call(client, "clash.abandon")).Bool("ready"), "Abandon confirmation skipped countdown.");
        await Call(client, "clash.abandon.choose", new { confirm = false });
        Check(ServerState.SnapshotStore.GetLatest(user, colony) is not null && !ServerState.Players.IsDeleted(user, colony), "Cancelling abandonment changed colony.");
        await Call(client, "clash.abandon.open"); await Wait(client, "clash.abandon", null, v => v.Bool("ready"));
        await Call(client, "clash.abandon.choose", new { confirm = true });
        await Wait(client, "game.state", null, s => s.Bool("startupReady") && !s.Bool("hasGame") && !s.Bool("playing"));
        await Wait(client, "clash.state", null, s => !s.Bool("isInActiveMultiplayerSession"));
        Check(ServerState.Players.IsDeleted(user, colony) && ServerState.SnapshotStore.GetLatest(user, colony) is null, "Abandonment did not delete snapshot and tombstone colony.");
        Check(!ServerState.WorldConfiguration.Current!.PlayerColonySites.Any(s => s.UserId == user && s.ColonyId == colony), "Abandoned site remained registered.");
        Check(ServerState.BankLoans.GetActive(user, colony) is null && ServerState.BankLoans.GetOpenDebts(user, colony).Count == 0 && ServerState.MercenaryContracts.CountOpenForColony(user, colony) == 0, "Abandoned colony retained liabilities/contracts.");
        Check(history == JsonSerializer.Serialize(ServerState.Achievements.ListAggregates().Where(a => a.UserId == user)), "Abandonment destroyed achievement history.");
        Check(ServerState.OfflineAccounts.Authenticate(user, "local-test-password", DateTimeOffset.UtcNow).Accepted, "Abandonment deleted account credentials.");
        Check(ServerState.WorldConfiguration.IsAdministrator("rimtest-A"), "Abandoning owner removed server administration.");
        Check(ServerState.SnapshotStore.GetLatest("rimtest-" + survivor, otherColony)?.Identity.SnapshotId == survivorSnapshot && await colonyFixture.Stock(survivor, "Silver") == silver && await colonyFixture.Stock(survivor, "Steel") == steel, "Abandonment mutated survivor snapshot or stock.");
        if (client == "A")
        {
            Check(ServerState.Ledger.Find(lifecycleOrder)!.Status == ServerEventStatus.Cancelled, "Abandoned seller's order remained executable.");
            var memos = ServerState.Ledger.ListAll().Where(e => e.Payload is TradeEventPayload p && p.TradeId == lifecycleOrder && p.Stage == TradeStage.AcceptedMemo).ToArray();
            Check(memos.Length == 1 && memos[0].Status == ServerEventStatus.Cancelled, "Abandoned seller's acceptance memo remained executable.");
            var bank = await economy.RefreshBank("B");
            Check(bank.GetProperty("activeLoan").ValueKind == JsonValueKind.Object && bank.GetProperty("openDebts").EnumerateArray().Any(d => d.String("debtId") == lifecycleDebt), "Owner abandonment cleared guest liabilities.");
        }
        var oldOwn = await CheckAchievementRefresh(survivor, client, colony);
        Check(Items(oldOwn).Length == 0, "Deleted colony achievements remain visible through direct target lookup.");
        await Invoke(survivor, "StartRefreshPlayers");
        await Wait(survivor, "clash.records", new { kind = "players" }, p => !Items(p).Any(v => v.GetProperty("data").String("userId") == user));
        await run.RecordAsync(new { accountLifecycle = "abandoned", client, colony, survivorSnapshot });
    }

    async Task RebuildAccountColony(string client, string survivor)
    {
        // Select beside the surviving colony, so the same server distance constraints apply.
        entry.SetNeighbouringColony((await Call(survivor, "clash.inventory")).GetProperty("maps")[0].Int("tileId"));
        await entry.Enter(client, url, register: false); await Idle(client);
        var state = await Call(client, "clash.state"); string colony = state.String("colonyId"), user = "rimtest-" + client;
        Check(colony != abandonedColonies[client] && ServerState.Players.IsDeleted(user, abandonedColonies[client]), "Rebuild reused/deleted old generation tombstone.");
        Check(ServerState.SnapshotStore.GetLatest(user, colony) is not null && ServerState.WorldConfiguration.Current!.PlayerColonySites.Count(s => s.UserId == user) == 1, "Rebuild did not publish exactly one active colony.");
        var bank = await economy.RefreshBank(client);
        Check(bank.GetProperty("activeLoan").ValueKind == JsonValueKind.Null && bank.GetProperty("openDebts").GetArrayLength() == 0, "New colony inherited prior liabilities.");
        var quests = await Call(client, "clash.bank.quests");
        Check(quests.GetProperty("loans").GetArrayLength() == 0 && quests.GetProperty("debts").GetArrayLength() == 0 && Items(await Call(client, "clash.mercenaries")).Length == 0, "Old quest/mercenary state crossed generation boundary.");
        var own = await CheckAchievementRefresh(client);
        Check(!Items(own).Any(v => v.GetProperty("data").String("achievementId") == "trades_10"), "Rebuild inherited old trade achievement.");
        await Invoke(client, "StartRefreshChatMessages");
        await Wait(client, "clash.records", new { kind = "chat" }, p => Items(p).Any(v => v.GetProperty("data").GetRawText().Contains("RimTest private exchange")));
        await Invoke(client, "StartRefreshServerShop"); await Idle(client);
        var shop = Items(await Call(client, "clash.records", new { kind = "shop" })).Select(v => v.GetProperty("data").GetRawText());
        Check(shop.SequenceEqual(Items(preservedShop).Select(v => v.GetProperty("data").GetRawText())), "Abandon/rebuild changed global shop listings.");
        await Invoke(survivor, "StartRefreshPlayers");
        await Wait(survivor, "clash.records", new { kind = "players" }, p => Items(p).Any(v => v.GetProperty("data").String("userId") == user && v.GetProperty("data").String("colonyId") == colony && v.GetProperty("data").OptionalString("relationKind") != "Ally"));
        Check(state.Bool("isAdministrator") == (client == "A"), "Rebuild changed account administration role.");
        await run.RecordAsync(new { accountLifecycle = "rebuilt", client, oldColony = abandonedColonies[client], colony });
    }
}
