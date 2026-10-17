using System.Text.Json;
using RimTest.Protocol;

namespace ClashOfRim.GameTests;

internal sealed partial class GameSuite
{
    async Task<JsonElement> RefreshBank(string client)
    {
        await Idle(client); await Invoke(client, "StartRefreshBankStatus"); await Idle(client);
        return await Call(client, "clash.bank");
    }
    async Task ReloadLocal(string client, string name)
    {
        name += "-" + Guid.NewGuid().ToString("N")[..8];
        await Idle(client); await Call(client, "time.set", new { speed = "Paused" });
        string epoch = (await Call(client, "game.state")).String("epoch");
        await Call(client, "game.save", new { name }); await Call(client, "game.load", new { name });
        await Wait(client, "game.state", null, s => s.String("epoch") != epoch && s.Bool("playerHasControl"), 180);
        await Call(client, "time.set", new { speed = "Paused" });
    }
    static JsonElement BankPart(JsonElement quests, string kind, string id) => quests.GetProperty(kind).EnumerateArray()
        .Single(p => p.String(kind == "loans" ? "loanId" : "debtId") == id);
    async Task BankPenalty(string client, string kind, string id)
    {
        var before = await Call(client, "clash.bank.quests"); var part = BankPart(before, kind, id);
        await Call(client, "clash.fixture.bankDeadline", new { id, acknowledgeFixture = true });
        await Step(client, 30);
        var after = await Call(client, "clash.bank.quests"); var overdue = BankPart(after, kind, id);
        Check(overdue.Int("triggeredPenaltyCount") == part.Int("triggeredPenaltyCount") + 1, "Penalty must execute exactly once at deadline.");
        Check(overdue.Bool("dueWarningSent"), "Missing due warning.");
        Check(overdue.GetProperty("nextPenaltyGameTicks").GetInt64() > after.Int("ticks"), "Penalty failed to schedule its next interval.");
        Check(after.Int("mechanoids") > before.Int("mechanoids"), "Overdue penalty did not spawn a real mech raid. " + after.GetProperty("raids").GetRawText());
        if (kind == "loans")
        {
            int fee = Math.Max(1, (int)Math.Ceiling(part.Int("totalDueSilver") * part.GetProperty("annualInterestRate").GetSingle() * 2d * Math.Max(1, part.Int("penaltyIntervalTicks") / 60000f) / 60d));
            Check(overdue.Int("lateFeeSilver") == part.Int("lateFeeSilver") + fee && overdue.Int("totalDueSilver") == part.Int("totalDueSilver") + fee, "Wrong compounding late fee.");
        }
        else Check(overdue.Int("amountSilver") == part.Int("amountSilver"), "Fixed fine accrued an undocumented fee.");
        await Step(client, 30);
        Equal(overdue, BankPart(await Call(client, "clash.bank.quests"), kind, id), "Penalty repeated before its interval");
        await ReloadLocal(client, "rimtest-bank-" + kind);
        Equal(overdue, BankPart(await Call(client, "clash.bank.quests"), kind, id), "Reload changed bank quest state");
    }
    async Task BankOverdue()
    {
        var bank = await RefreshBank("A"); int cash = await Stock("A", "Silver");
        await Invoke("A", "StartCreateBankLoan", new { principalSilver = bank.Int("minLoanSilver"), durationDays = bank.Int("minDurationDays") });
        await Idle("A"); await Step("A", 600); bank = await RefreshBank("A");
        var loan = bank.GetProperty("activeLoan"); string id = loan.String("loanId");
        Check(await Stock("A", "Silver") == cash + loan.Int("principalSilver"), "Loan delivery count mismatch.");
        await BankPenalty("A", "loans", id); await BankPenalty("A", "loans", id);
        var part = BankPart(await Call("A", "clash.bank.quests"), "loans", id);
        await Call("A", "clash.fixture.bankCash", new { amount = part.Int("totalDueSilver") - 1, acknowledgeFixture = true });
        await NoEconomicMutation("A", async () => { await Invoke("A", "StartRepayBankLoan"); });
        await Call("A", "clash.fixture.bankCash", new { amount = part.Int("totalDueSilver"), acknowledgeFixture = true });
        await Invoke("A", "StartRepayBankLoan"); await Idle("A");
        Check(await Stock("A", "Silver") == 0, "Overdue repayment did not charge exact local total.");
        Check((await RefreshBank("A")).GetProperty("activeLoan").ValueKind == JsonValueKind.Null, "Repaid loan resurrected on refresh.");
        Check(BankPart(await Call("A", "clash.bank.quests"), "loans", id).Bool("completed"), "Loan quest remains active.");
        await NoEconomicMutation("A", async () => { await Invoke("A", "StartRepayBankLoan"); });
    }
    async Task BankDebt()
    {
        await Mercenary();
        var contract = Items(await Call("B", "clash.mercenaries")).Single(p => !p.Bool("completed"));
        await Call("B", "clash.fixture.mercenaryDeath", new { contractId = contract.String("contractId"), acknowledgeFixture = true });
        var bank = await Wait("B", "clash.bank", null, b => b.GetProperty("openDebts").GetArrayLength() == 1);
        await Idle("B");
        var debt = bank.GetProperty("openDebts")[0]; string id = debt.String("debtId");
        Check(debt.String("reason") == "Mercenary:Death" && debt.Int("amountSilver") > 0, "Wrong death fine.");
        await BankPenalty("B", "debts", id);
        await Reconnect();
        bank = await RefreshBank("B");
        Check(bank.GetProperty("openDebts").GetArrayLength() == 1 && bank.GetProperty("openDebts")[0].String("debtId") == id, "Reconnect duplicated or lost debt.");
        var record = Items(await Call("B", "clash.bank.debts")).Single();
        await Call("B", "clash.fixture.bankCash", new { amount = debt.Int("amountSilver") - 1, acknowledgeFixture = true });
        await NoEconomicMutation("B", async () => { await Invoke("B", "StartRepayBankDebt", new { debt = record.GetProperty("reference") }); });
        // Leave enough for a second accidental charge so stale-record rejection is meaningful.
        await Call("B", "clash.fixture.bankCash", new { amount = debt.Int("amountSilver") * 2, acknowledgeFixture = true });
        await Invoke("B", "StartRepayBankDebt", new { debt = record.GetProperty("reference") }); await Idle("B");
        Check(await Stock("B", "Silver") == debt.Int("amountSilver"), "Fine repayment charged the wrong amount.");
        Check((await RefreshBank("B")).GetProperty("openDebts").GetArrayLength() == 0, "Repaid fine remained open.");
        Check(BankPart(await Call("B", "clash.bank.quests"), "debts", id).Bool("completed"), "Repaid fine quest remained active.");
        await NoEconomicMutation("B", async () => { await Invoke("B", "StartRepayBankDebt", new { debt = record.GetProperty("reference") }); });
    }

    async Task MercenaryGuardPurchase()
    {
        await Idle("B");
        var before = await Call("B", "clash.state");
        int price = ServerState.ServerConfiguration.MercenaryGuardApprenticeSilver;
        await Call("B", "clash.fixture.bankCash", new { amount = price * 2, acknowledgeFixture = true });
        await Invoke("B", "StartHireMercenaryGuard", new { tier = "Apprentice" }); await Idle("B");
        var contract = ServerState.MercenaryGuards.FindActiveForColony(before.String("userId"), before.String("colonyId"));
        Check(contract is not null && contract.PriceSilver == price && contract.Tier == "Apprentice"
            && contract.PointRatio == ServerState.ServerConfiguration.MercenaryGuardApprenticePointsRatio,
            "Guard purchase did not persist the quoted contract.");
        Check(await Stock("B", "Silver") == price, "Guard purchase did not charge exactly once.");
        Check((await Call("B", "clash.state")).String("currentSnapshotId") != before.String("currentSnapshotId"), "Guard purchase did not commit a snapshot.");
        await NoEconomicMutation("B", async () => { await Invoke("B", "StartHireMercenaryGuard", new { tier = "Apprentice" }); });
        Check(ServerState.MercenaryGuards.FindActiveForColony(before.String("userId"), before.String("colonyId"))?.ContractId == contract!.ContractId,
            "Repeated purchase replaced an active guard.");
    }
}
