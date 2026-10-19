using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class AccountColonyScenario(TestRun run, ScenarioPolling polling, ColonyScenarioFixture colonyFixture,
    EconomicScenarioFixture economy, AchievementScenario achievements, ServerEntryScenario entry, ChatScenario chat,
    PlayerScenarioFixture players, ShopScenarioFixture shopActions, AccountLifecycleAuthority authority, Func<string> serverAddress)
{
    internal async Task<string> Abandon(string client, string survivor, AccountLifecyclePreparation prepared)
    {
        await polling.Idle(client); await polling.Idle(survivor);
        string user = "rimtest-" + client;
        var state = await run.CallAsync(client, "clash.state"); string colony = state.String("colonyId");
        var other = await run.CallAsync(survivor, "clash.state"); string otherColony = other.String("colonyId");
        string? survivorSnapshot = authority.SnapshotId("rimtest-" + survivor, otherColony);
        int silver = await colonyFixture.Stock(survivor, "Silver"), steel = await colonyFixture.Stock(survivor, "Steel");
        var history = achievements.CaptureHistory(user);
        await run.CallAsync(client, "clash.abandon.open");
        Check(!(await run.CallAsync(client, "clash.abandon")).Bool("ready"), "Abandon confirmation skipped countdown.");
        await run.CallAsync(client, "clash.abandon.choose", new { confirm = false });
        authority.VerifyCancelled(user, colony);
        await run.CallAsync(client, "clash.abandon.open"); await polling.Wait(client, "clash.abandon", null, v => v.Bool("ready"));
        await run.CallAsync(client, "clash.abandon.choose", new { confirm = true });
        await polling.Wait(client, "game.state", null, s => s.Bool("startupReady") && !s.Bool("hasGame") && !s.Bool("playing"));
        await polling.Wait(client, "clash.state", null, s => !s.Bool("isInActiveMultiplayerSession"));
        authority.VerifyAbandoned(user, colony);
        Check(history == achievements.CaptureHistory(user), "Abandonment destroyed achievement history.");
        Check(authority.SnapshotId("rimtest-" + survivor, otherColony) == survivorSnapshot && await colonyFixture.Stock(survivor, "Silver") == silver && await colonyFixture.Stock(survivor, "Steel") == steel, "Abandonment mutated survivor snapshot or stock.");
        if (client == "A")
        {
            authority.VerifyOrderCancelled(prepared.TradeId);
            var bank = await economy.RefreshBank("B");
            Check(bank.GetProperty("activeLoan").ValueKind == JsonValueKind.Object && bank.GetProperty("openDebts").EnumerateArray().Any(d => d.String("debtId") == prepared.DebtId), "Owner abandonment cleared guest liabilities.");
        }
        var oldOwn = await achievements.Refresh(survivor, client, colony);
        Check(Items(oldOwn).Length == 0, "Deleted colony achievements remain visible through direct target lookup.");
        await players.Refresh(survivor);
        await polling.Wait(survivor, "clash.records", new { kind = "players" }, p => !Items(p).Any(v => v.GetProperty("data").String("userId") == user));
        await run.RecordAsync(new { accountLifecycle = "abandoned", client, colony, survivorSnapshot });
        return colony;
    }

    internal async Task Rebuild(string client, string survivor, string previousColony, AccountLifecyclePreparation prepared)
    {
        // Select beside the surviving colony, so the same server distance constraints apply.
        entry.SetNeighbouringColony((await run.CallAsync(survivor, "clash.inventory")).GetProperty("maps")[0].Int("tileId"));
        await entry.Enter(client, serverAddress(), register: false); await polling.Idle(client);
        var state = await run.CallAsync(client, "clash.state"); string colony = state.String("colonyId"), user = "rimtest-" + client;
        authority.VerifyRebuilt(user, previousColony, colony);
        var bank = await economy.RefreshBank(client);
        Check(bank.GetProperty("activeLoan").ValueKind == JsonValueKind.Null && bank.GetProperty("openDebts").GetArrayLength() == 0, "New colony inherited prior liabilities.");
        var quests = await run.CallAsync(client, "clash.bank.quests");
        Check(quests.GetProperty("loans").GetArrayLength() == 0 && quests.GetProperty("debts").GetArrayLength() == 0 && Items(await run.CallAsync(client, "clash.mercenaries")).Length == 0, "Old quest/mercenary state crossed generation boundary.");
        var own = await achievements.Refresh(client);
        Check(!Items(own).Any(v => v.GetProperty("data").String("achievementId") == "trades_10"), "Rebuild inherited old trade achievement.");
        await chat.VerifyPrivateHistory(client);
        await shopActions.Refresh(client);
        var shop = Items(await run.CallAsync(client, "clash.records", new { kind = "shop" })).Select(v => v.GetProperty("data").GetRawText());
        Check(shop.SequenceEqual(Items(prepared.Shop).Select(v => v.GetProperty("data").GetRawText())), "Abandon/rebuild changed global shop listings.");
        await players.Refresh(survivor);
        await polling.Wait(survivor, "clash.records", new { kind = "players" }, p => Items(p).Any(v => v.GetProperty("data").String("userId") == user && v.GetProperty("data").String("colonyId") == colony && v.GetProperty("data").OptionalString("relationKind") != "Ally"));
        Check(state.Bool("isAdministrator") == (client == "A"), "Rebuild changed account administration role.");
        await run.RecordAsync(new { accountLifecycle = "rebuilt", client, oldColony = previousColony, colony });
    }
}
