using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed record AccountLifecyclePreparation(string TradeId, string DebtId, JsonElement Shop);

internal sealed class AccountLifecycleScenario(TestRun run, ScenarioPolling polling, ColonyScenarioFixture colonyFixture,
    EconomicScenarioFixture economy, MercenaryScenario mercenaries, PasswordScenario passwords, AchievementScenario achievements,
    ChatScenario chat, ShopScenario shop, DiplomacyScenario diplomacy, TradeScenario trades, TradeScenarioFixture tradeActions,
    AccountColonyScenario colonies)
{
    internal async Task Run()
    {
        AccountLifecyclePreparation prepared = null!;
        string ownerColony = "", guestColony = "";
        await Case("account-password-change", passwords.Change);
        await Case("account-achievements-before-abandon", async () =>
        {
            // Two generations each perform ten real shipments, plus a hire. Fund
            // the buyer explicitly so postage cannot mask lifecycle assertions.
            await run.CallAsync("B", "clash.fixture.bankCash", new { amount = 10000, acknowledgeFixture = true });
            await colonyFixture.StoreStartingResources("A"); await colonyFixture.StoreStartingResources("B");
            await trades.Fulfill(); await achievements.ReachTradeThreshold();
            await achievements.Refresh("A"); await achievements.Refresh("B");
            // Re-upload and refresh the same cumulative history: it must not count deliveries twice.
            var before = achievements.CaptureHistory();
            for (int i = 0; i < 2; i++) { await run.InvokeClientAsync("A", "StartManualSnapshotUpload"); await polling.Idle("A"); }
            Check(before == achievements.CaptureHistory(), "Uploads replayed achievement accounting.");
        });
        await Case("account-liabilities-before-abandon", async () => prepared = await PrepareLiabilities());
        await Case("account-abandon-owner", async () => ownerColony = await colonies.Abandon("A", "B", prepared));
        await Case("account-rebuild-owner", () => colonies.Rebuild("A", "B", ownerColony, prepared));
        await Case("account-achievements-new-generation", async () =>
        {
            await colonyFixture.StoreStartingResources("A");
            await trades.Fulfill();
            var own = await achievements.Refresh("A");
            Check(!Items(own).Any(v => v.GetProperty("data").String("achievementId") == "trades_10"), "New colony inherited old completed trade count.");
            await achievements.ReachTradeThreshold(); await achievements.Refresh("A"); await achievements.Refresh("B");
            await colonyFixture.Reload("A", "rimtest-rebuilt-achievements"); await achievements.Refresh("A");
        });
        await Case("account-abandon-guest", async () => guestColony = await colonies.Abandon("B", "A", prepared));
        await Case("account-rebuild-guest", () => colonies.Rebuild("B", "A", guestColony, prepared));
    }

    internal async Task<AccountLifecyclePreparation> PrepareLiabilities()
    {
        await chat.Private(); await shop.Purchase(preserveListing: true); await diplomacy.Alliance();
        var preservedShop = await run.CallAsync("A", "clash.records", new { kind = "shop" });
        Check(Items(preservedShop).Length == 1, "Expected a surviving global listing.");
        foreach (string client in new[] { "A", "B" })
        {
            var bank = await economy.RefreshBank(client);
            await run.InvokeClientAsync(client, "StartCreateBankLoan", new { principalSilver = bank.Int("minLoanSilver"), durationDays = bank.Int("minDurationDays") });
            await polling.Idle(client); await polling.Step(client, 600);
            Check((await economy.RefreshBank(client)).GetProperty("activeLoan").ValueKind == JsonValueKind.Object, "Missing lifecycle loan.");
        }
        await mercenaries.Hire();
        var contract = Items(await run.CallAsync("B", "clash.mercenaries")).Single(c => !c.Bool("completed"));
        await run.CallAsync("B", "clash.fixture.mercenaryDeath", new { contractId = contract.String("contractId"), acknowledgeFixture = true });
        var debt = await polling.Wait("B", "clash.bank", null, b => b.GetProperty("openDebts").GetArrayLength() == 1);
        string lifecycleDebt = debt.GetProperty("openDebts")[0].String("debtId"); await polling.Idle("B");
        await mercenaries.Hire(); // Leave a second contract active when its colony is abandoned.
        var order = await tradeActions.Create(); string lifecycleOrder = order.EventId;
        await tradeActions.AcceptMemo(await tradeActions.Refresh("B", lifecycleOrder));
        return new(lifecycleOrder, lifecycleDebt, preservedShop);
    }
    private Task Case(string id, Func<Task> body) => run.RunCaseAsync(id, body, () => run.CaptureLogsAsync());
}
