using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class BankScenario(TestRun run, ScenarioPolling polling, ColonyScenarioFixture colonyFixture,
    EconomicScenarioFixture economy, MercenaryScenario mercenaries, SessionPersistenceScenario roundtrips)
{
    internal async Task LoanRepayment()
    {
        var bank = await economy.RefreshBank("A");
        Check(bank.GetProperty("result").Bool("accepted") && bank.Bool("bankLoansEnabled"), "Bank unavailable.");
        int principal = Math.Max(bank.Int("minLoanSilver"), 100);
        Check(principal <= bank.Int("maxLoanSilver"), "Fixture colony cannot borrow minimum principal.");
        int silver = await colonyFixture.Stock("A", "Silver"); string previousSnapshot = (await run.CallAsync("A", "clash.state")).String("currentSnapshotId");
        await run.InvokeClientAsync("A", "StartCreateBankLoan", new { principalSilver = principal, durationDays = bank.Int("minDurationDays") });
        await polling.Idle("A");
        bank = await run.CallAsync("A", "clash.bank");
        Check(bank.GetProperty("activeLoan").ValueKind == JsonValueKind.Object, "Loan was not created.");
        Check(bank.GetProperty("activeLoan").Int("principalSilver") == principal, "Wrong loan principal.");
        await polling.Step("A", 600); // Let the original drop pod land and open before counting spawned stock.
        Check(await colonyFixture.Stock("A", "Silver") == silver + principal, "Loan cash did not reach the map exactly once.");
        await economy.AssertSnapshotAdvanced("A", previousSnapshot);
        int due = bank.GetProperty("activeLoan").Int("totalDueSilver");
        await economy.AssertNoMutation("A", () => run.InvokeClientAsync("A", "StartCreateBankLoan", new { principalSilver = principal, durationDays = bank.Int("minDurationDays") }));
        await run.InvokeClientAsync("A", "StartRepayBankLoan"); await polling.Idle("A");
        bank = await run.CallAsync("A", "clash.bank");
        Check(bank.GetProperty("activeLoan").ValueKind == JsonValueKind.Null, "Loan remains active after repayment.");
        Check(await colonyFixture.Stock("A", "Silver") == silver + principal - due, "Repayment cash mismatch.");
        await economy.AssertNoMutation("A", () => run.InvokeClientAsync("A", "StartRepayBankLoan"));
    }
    internal async Task InputBounds()
    {
        var bank = await economy.RefreshBank("A");
        foreach (var (amount, days) in new[] { (0, bank.Int("minDurationDays")), (-1, bank.Int("minDurationDays")),
            (int.MaxValue, bank.Int("minDurationDays")), (bank.Int("minLoanSilver"), bank.Int("minDurationDays") - 1),
            (bank.Int("minLoanSilver"), bank.Int("maxDurationDays") + 1) })
            await economy.AssertNoMutation("A", () => run.InvokeClientAsync("A", "StartCreateBankLoan", new { principalSilver = amount, durationDays = days }));
        Check((await run.CallAsync("A", "clash.bank")).GetProperty("activeLoan").ValueKind == JsonValueKind.Null, "Invalid bank input created a loan.");
    }
    static JsonElement QuestPart(JsonElement quests, string kind, string id) => quests.GetProperty(kind).EnumerateArray()
        .Single(p => p.String(kind == "loans" ? "loanId" : "debtId") == id);
    async Task Penalty(string client, string kind, string id)
    {
        var before = await run.CallAsync(client, "clash.bank.quests"); var part = QuestPart(before, kind, id);
        await run.CallAsync(client, "clash.fixture.bankDeadline", new { id, acknowledgeFixture = true });
        await polling.Step(client, 30);
        var after = await run.CallAsync(client, "clash.bank.quests"); var overdue = QuestPart(after, kind, id);
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
        await polling.Step(client, 30);
        Equal(overdue, QuestPart(await run.CallAsync(client, "clash.bank.quests"), kind, id), "Penalty repeated before its interval");
        await colonyFixture.Reload(client, "rimtest-bank-" + kind);
        Equal(overdue, QuestPart(await run.CallAsync(client, "clash.bank.quests"), kind, id), "Reload changed bank quest state");
    }
    internal async Task OverdueRepayment()
    {
        var bank = await economy.RefreshBank("A"); int cash = await colonyFixture.Stock("A", "Silver");
        await run.InvokeClientAsync("A", "StartCreateBankLoan", new { principalSilver = bank.Int("minLoanSilver"), durationDays = bank.Int("minDurationDays") });
        await polling.Idle("A"); await polling.Step("A", 600); bank = await economy.RefreshBank("A");
        var loan = bank.GetProperty("activeLoan"); string id = loan.String("loanId");
        Check(await colonyFixture.Stock("A", "Silver") == cash + loan.Int("principalSilver"), "Loan delivery count mismatch.");
        await Penalty("A", "loans", id); await Penalty("A", "loans", id);
        // Reload preserves the quest but clears the bank view. Repayment must use
        // a refreshed quote, as it does when the player reopens the bank panel.
        bank = await economy.RefreshBank("A");
        Check(bank.GetProperty("activeLoan").ValueKind == JsonValueKind.Object && bank.GetProperty("activeLoan").String("loanId") == id,
            "Reload did not restore the current unpaid loan quote.");
        var part = QuestPart(await run.CallAsync("A", "clash.bank.quests"), "loans", id);
        await run.CallAsync("A", "clash.fixture.bankCash", new { amount = part.Int("totalDueSilver") - 1, acknowledgeFixture = true });
        await economy.AssertNoMutation("A", () => run.InvokeClientAsync("A", "StartRepayBankLoan"));
        await run.CallAsync("A", "clash.fixture.bankCash", new { amount = part.Int("totalDueSilver"), acknowledgeFixture = true });
        await run.InvokeClientAsync("A", "StartRepayBankLoan"); await polling.Idle("A");
        Check(await colonyFixture.Stock("A", "Silver") == 0, "Overdue repayment did not charge exact local total.");
        Check((await economy.RefreshBank("A")).GetProperty("activeLoan").ValueKind == JsonValueKind.Null, "Repaid loan resurrected on refresh.");
        Check(QuestPart(await run.CallAsync("A", "clash.bank.quests"), "loans", id).Bool("completed"), "Loan quest remains active.");
        await economy.AssertNoMutation("A", () => run.InvokeClientAsync("A", "StartRepayBankLoan"));
    }
    internal async Task MercenaryDebt()
    {
        await mercenaries.Hire();
        var contract = Items(await run.CallAsync("B", "clash.mercenaries")).Single(p => !p.Bool("completed"));
        await run.CallAsync("B", "clash.fixture.mercenaryDeath", new { contractId = contract.String("contractId"), acknowledgeFixture = true });
        var bank = await polling.Wait("B", "clash.bank", null, b => b.GetProperty("openDebts").GetArrayLength() == 1);
        await polling.Idle("B");
        var debt = bank.GetProperty("openDebts")[0]; string id = debt.String("debtId");
        Check(debt.String("reason") == "Mercenary:Death" && debt.Int("amountSilver") > 0, "Wrong death fine.");
        await Penalty("B", "debts", id);
        await roundtrips.Reconnect();
        bank = await economy.RefreshBank("B");
        Check(bank.GetProperty("openDebts").GetArrayLength() == 1 && bank.GetProperty("openDebts")[0].String("debtId") == id, "Reconnect duplicated or lost debt.");
        var record = Items(await run.CallAsync("B", "clash.bank.debts")).Single();
        Check(record.GetProperty("data").String("debtId") == id, "Repayment record belongs to a different debt.");
        await run.CallAsync("B", "clash.fixture.bankCash", new { amount = debt.Int("amountSilver") - 1, acknowledgeFixture = true });
        await economy.AssertNoMutation("B", () => run.InvokeClientAsync("B", "StartRepayBankDebt", new { debt = record.GetProperty("reference") }));
        // Leave enough for a second accidental charge so stale-record rejection is meaningful.
        await run.CallAsync("B", "clash.fixture.bankCash", new { amount = debt.Int("amountSilver") * 2, acknowledgeFixture = true });
        await run.InvokeClientAsync("B", "StartRepayBankDebt", new { debt = record.GetProperty("reference") }); await polling.Idle("B");
        Check(await colonyFixture.Stock("B", "Silver") == debt.Int("amountSilver"), "Fine repayment charged the wrong amount.");
        Check((await economy.RefreshBank("B")).GetProperty("openDebts").GetArrayLength() == 0, "Repaid fine remained open.");
        Check(QuestPart(await run.CallAsync("B", "clash.bank.quests"), "debts", id).Bool("completed"), "Repaid fine quest remained active.");
        await economy.AssertNoMutation("B", () => run.InvokeClientAsync("B", "StartRepayBankDebt", new { debt = record.GetProperty("reference") }));
    }
}
