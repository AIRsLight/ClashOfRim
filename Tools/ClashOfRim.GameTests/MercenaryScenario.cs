using System.Diagnostics;
using AIRsLight.ClashOfRim.Network;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class MercenaryScenario(TestRun run, ScenarioPolling polling, ColonyScenarioFixture colonyFixture,
    EconomicScenarioFixture economy, MercenaryContractRegistry mercenaryContracts, MercenaryGuardContractRegistry guards,
    Func<(int PriceSilver, float PointRatio)> getGuardQuote)
{
    internal async Task Hire()
    {
        var bank = await economy.RefreshBank("B"); int before = await colonyFixture.Stock("B", "Silver");
        await run.InvokeClientAsync("B", "StartHireMercenary", new { skillDefName = "Mining", skillLevel = 7, durationDays = bank.Int("mercenaryMinDurationDays") });
        var contracts = await polling.Wait("B", "clash.mercenaries", null, p => Items(p).Any(c => !c.Bool("completed")));
        string id = Items(contracts).Single(c => !c.Bool("completed")).String("contractId");
        await polling.Idle("B"); await polling.Step("B", 600);
        contracts = await polling.Wait("B", "clash.mercenaries", null, p => Items(p).Any(c => c.String("contractId") == id && !c.Bool("completed") && c.Bool("spawned")));
        var contract = Items(contracts).Single(c => c.String("contractId") == id);
        Check(contract.Int("actualSkill") == 7 && contract.Int("priceSilver") > 0, "Wrong mercenary skill/price.");
        Check(await colonyFixture.Stock("B", "Silver") == before - contract.Int("priceSilver"), "Mercenary charge mismatch.");
        await run.CallAsync("B", "pawn.draft", new { pawn = contract.GetProperty("pawn").String("handle"), drafted = true });
    }
    internal async Task Recall()
    {
        var contract = Items(await run.CallAsync("B", "clash.mercenaries")).Single(c => !c.Bool("completed"));
        string id = contract.String("contractId");
        await run.CallAsync("B", "pawn.draft", new { pawn = contract.GetProperty("pawn").String("handle"), drafted = false });
        Check((await run.CallAsync("B", "clash.mercenary.recall", new { contract = H(contract) })).Bool("accepted"), "Recall action rejected.");
        await polling.Step("B", 600);
        contract = Items(await run.CallAsync("B", "clash.mercenaries")).Single(c => c.String("contractId") == id);
        string shuttle = contract.GetProperty("shuttle").String("handle");
        await run.CallAsync("B", "selection.set", new { handle = shuttle });
        string loadLabel = (await run.CallAsync("B", "clash.labels")).String("load");
        var load = Items(await run.CallAsync("B", "gizmos.list", new { handle = shuttle })).Single(g => g.String("label") == loadLabel && !g.Bool("disabled"));
        await run.CallAsync("B", "gizmos.invoke", new { handle = shuttle, type = load.String("type"), label = load.String("label") });
        await polling.RunUntil("B", "clash.mercenaries", null, p => !Items(p).Any(c => c.String("contractId") == id && !c.Bool("completed")));
        await polling.Idle("B");
        var watch = Stopwatch.StartNew();
        while (mercenaryContracts.Find(id)?.Status != MercenaryContractRegistry.StatusCompleted && watch.Elapsed.TotalSeconds < 90)
            await Task.Delay(100);
        Check(mercenaryContracts.Find(id)?.Status == MercenaryContractRegistry.StatusCompleted, "Recall did not close the authoritative contract.");
        await run.RecordAsync(new { serverObservation = "mercenary-recall", contractId = id, status = mercenaryContracts.Find(id)?.Status });
    }
    internal async Task GuardPurchase()
    {
        await polling.Idle("B");
        var before = await run.CallAsync("B", "clash.state");
        var quote = getGuardQuote();
        int price = quote.PriceSilver;
        await run.CallAsync("B", "clash.fixture.bankCash", new { amount = price * 2, acknowledgeFixture = true });
        await run.InvokeClientAsync("B", "StartHireMercenaryGuard", new { tier = "Apprentice" }); await polling.Idle("B");
        var contract = guards.FindActiveForColony(before.String("userId"), before.String("colonyId"));
        Check(contract is not null && contract.PriceSilver == price && contract.Tier == "Apprentice"
            && contract.PointRatio == quote.PointRatio,
            "Guard purchase did not persist the quoted contract.");
        Check(await colonyFixture.Stock("B", "Silver") == price, "Guard purchase did not charge exactly once.");
        await economy.AssertSnapshotAdvanced("B", before.String("currentSnapshotId"));
        await economy.AssertNoMutation("B", async () => { await run.InvokeClientAsync("B", "StartHireMercenaryGuard", new { tier = "Apprentice" }); });
        Check(guards.FindActiveForColony(before.String("userId"), before.String("colonyId"))?.ContractId == contract!.ContractId,
            "Repeated purchase replaced an active guard.");
    }
}
