using System.Text.Json;
using ClashOfRim.GameTests;
using RimTest.Protocol;
using RimTest.Testing;

internal static class GameBankScenarioTests
{
    internal static void Loans() => LoansAsync().GetAwaiter().GetResult();
    internal static void PenaltiesAndDebts() => PenaltiesAndDebtsAsync().GetAwaiter().GetResult();
    private static async Task LoansAsync()
    {
        foreach (string fault in new[] { "none", "unavailable", "disabled", "limit", "no-loan", "principal", "delivery", "snapshot", "empty-snapshot", "blank-snapshot",
            "duplicate-loan", "repayment-cash", "open-loan", "stale-cash" })
        {
            using var f = await Fixture.Create("loan", fault);
            if (fault == "none")
            {
                await f.Scenario.LoanRepayment();
                Check(f.Silver == 980 && f.Creates == 2 && f.Repayments == 2, "Basic loan did not verify duplicate creation and repayment.");
                Check(f.Runtime.Calls.Single(c => c.Method == "time.step").Args.Int("ticks") == 600, "Loan skipped the landing budget.");
            }
            else await Reject(f.Scenario.LoanRepayment, fault);
            Check(f.Runtime.Calls.All(c => c.Client == "A"), "Loan touched another client.");
        }
        foreach (string fault in new[] { "none", "invalid-cash", "invalid-snapshot", "invalid-loan" })
        {
            using var f = await Fixture.Create("bounds", fault);
            if (fault != "none") { await Reject(f.Scenario.InputBounds, fault); continue; }
            await f.Scenario.InputBounds();
            Check(f.Requests.SequenceEqual(new[] { (0, 2), (-1, 2), (int.MaxValue, 2), (50, 1), (50, 7) }), "Bank rejection matrix lost a boundary or changed bank-provided limits.");
            Check(f.Runtime.Count("time.step") == 0 && f.Silver == 1000, "Invalid loans changed cash or advanced the game.");
        }
    }
    private static async Task PenaltiesAndDebtsAsync()
    {
        foreach (string mode in new[] { "overdue", "debt" })
        foreach (string fault in new[] { "none", "count", "warning", "interval", "raid", "repeated-penalty", "reload-penalty", "insufficient-cash", "quest", "stale-cash" }
            .Concat(mode == "overdue" ? new[] { "delivery", "fee", "total", "repayment-cash", "open-loan" }
                : new[] { "reason", "amount", "debt-fee", "reconnect-loss", "reconnect-duplicate", "reconnect-identity", "record-identity", "repayment-cash", "open-debt" }))
        {
            using var f = await Fixture.Create(mode, fault);
            Func<Task> action = mode == "overdue" ? f.Scenario.OverdueRepayment : f.Scenario.MercenaryDebt;
            if (fault != "none") { await Reject(action, mode + ":" + fault); continue; }
            await action();
            Check(f.Repayments == 3 && f.Silver == (mode == "overdue" ? 0 : 30), "Bank omitted insufficient/exact/stale repayment or charged the wrong amount.");
            int penalties = mode == "overdue" ? 2 : 1;
            Check(f.Deadlines == penalties && f.Runtime.Count("game.save") == penalties && f.Runtime.Count("game.load") == penalties, "Penalty did not exercise each persisted quest roundtrip exactly once.");
            Check(f.Runtime.Calls.Where(c => c.Method == "time.step" && c.Args.Int("ticks") == 30).Count() == penalties * 2, "Penalty skipped the second non-deadline tick window.");
            var saves = f.Runtime.Calls.Where(c => c.Method == "game.save").Select(c => c.Args.String("name")).ToArray();
            Check(saves.Distinct().Count() == penalties && saves.SequenceEqual(f.Runtime.Calls.Where(c => c.Method == "game.load").Select(c => c.Args.String("name"))), "Penalty reused a save name or loaded another save.");
            if (mode == "debt")
                Check(f.Hires == 1 && f.Deaths == 1 && f.Logins == 1 && f.Runtime.Count("game.returnToMenu") == 1 && f.Uploads == 1,
                    "Debt bypassed real scenario hire, death fixture or upload/logout/login ordering.");
        }
    }
    // Scripted observations exercise the scenario, not a second implementation of banking.
    // Penalty amounts below are fixed examples (120 -> 124 -> 129); no product fee policy is called here.
    private sealed class Fixture : IDisposable
    {
        internal readonly GameScenarioTestRuntime Runtime;
        internal readonly BankScenario Scenario;
        private readonly string mode, fault;
        internal readonly List<(int, int)> Requests = [];
        internal int Silver = 1000, Creates, Repayments, Deadlines, Hires, Deaths, Logins, Uploads;
        private int epoch, shortSteps;
        private bool activeLoan, debtOpen, repaid, active = true;
        private string snapshot = "baseline";
        private bool IsDebt => mode == "debt";
        private string Id => IsDebt ? "death-fine" : "loan";
        private int TotalDue => new[] { 120, 124, 129 }[Deadlines];
        private Fixture(GameScenarioTestRuntime runtime, string mode, string fault)
        {
            Runtime = runtime; this.mode = mode; this.fault = fault;
            var colony = new ColonyScenarioFixture(runtime.Run, runtime.Polling);
            var economy = new EconomicScenarioFixture(runtime.Run, runtime.Polling, colony);
            var mercenaries = new MercenaryScenario(runtime.Run, runtime.Polling, colony, economy, new(), new(), () => (30, 0.4f));
            var sessions = new SessionPersistenceScenario(runtime.Run, runtime.Polling, colony, runtime.CreateEntry(isUserOnline: _ => active));
            Scenario = new(runtime.Run, runtime.Polling, colony, economy, mercenaries, sessions);
            Runtime.Script = Reply;
        }
        internal static async Task<Fixture> Create(string mode, string fault) => new(await GameScenarioTestRuntime.Create(), mode, fault);
        private object? Reply(string client, string method, JsonElement args)
        {
            Check(client == (IsDebt ? "B" : "A"), "Bank scenario crossed clients.");
            switch (method)
            {
                case "clash.state": return new { currentSnapshotId = snapshot, colonyId = "rebuilt-colony", isInActiveMultiplayerSession = active };
                case "clash.inventory": return new { maps = new[] { new { tileId = 8, items = new Dictionary<string, int> { ["Silver"] = Silver, ["Steel"] = 80 }, colonists = Array.Empty<object>() } } };
                case "clash.bank": return Bank();
                case "clash.bank.quests": return Quests();
                case "clash.bank.debts": return new { items = new[] { new { reference = new { handle = "fine-record" }, data = new { debtId = fault == "record-identity" ? "other-debt" : Id } } } };
                case "clash.invoke": return Invoke(args);
                case "clash.fixture.bankCash": Check(args.Bool("acknowledgeFixture"), "Bank cash fixture was not acknowledged."); Silver = args.Int("amount"); return new { };
                case "clash.fixture.bankDeadline":
                    Check(args.Bool("acknowledgeFixture") && args.String("id") == Id, "Penalty targeted another obligation."); Deadlines++; shortSteps = 0; return new { };
                case "time.step": if (args.Int("ticks") == 30) shortSteps++; return null;
                case "game.state": return new { epoch = epoch.ToString(), playing = active, playerHasControl = active, mapCount = active ? 1 : 0, startupReady = !active, hasGame = active };
                case "game.load": epoch++; return new { };
                case "game.returnToMenu": Check(Uploads == 1, "Debt reconnect discarded an unuploaded colony."); active = false; return new { };
                case "clash.world.details": return new[] { "worldConfigurationId", "info", "layers", "storyteller", "difficulty", "difficultyValues", "features", "factions", "ideologies" }.ToDictionary(key => key, key => key);
                case "clash.mercenaries": return new { items = new[] { new { contractId = "hired", completed = Deaths > 0, spawned = true, actualSkill = 7, priceSilver = 20, pawn = new { handle = "hired-pawn" } } } };
                case "clash.fixture.mercenaryDeath":
                    Check(Hires == 1 && args.String("contractId") == "hired" && args.Bool("acknowledgeFixture"), "Debt killed another contract or skipped hire."); Deaths++; debtOpen = true; return new { };
                default: return null;
            }
        }
        private object Bank()
        {
            object loan = new { loanId = Id, principalSilver = fault == "principal" ? 99 : mode == "overdue" ? 50 : 100, totalDueSilver = TotalDue };
            object debt = new { debtId = Logins > 0 && fault == "reconnect-identity" ? "other" : Id, reason = fault == "reason" ? "Other" : "Mercenary:Death", amountSilver = fault == "amount" ? 0 : 30 };
            var debts = debtOpen ? (Logins > 0 && fault == "reconnect-loss" ? Array.Empty<object>() : Logins > 0 && fault == "reconnect-duplicate" ? new[] { debt, debt } : new[] { debt }) : Array.Empty<object>();
            return new { result = new { accepted = fault != "unavailable" }, bankLoansEnabled = fault != "disabled", minLoanSilver = 50,
                maxLoanSilver = fault == "limit" ? 99 : 500, minDurationDays = 2, maxDurationDays = 6, mercenaryMinDurationDays = 4,
                activeLoan = activeLoan ? loan : null, openDebts = debts };
        }
        private object Quests()
        {
            int count = Deadlines + (Deadlines > 0 && (fault == "count" || fault == "repeated-penalty" && shortSteps >= 2 || fault == "reload-penalty" && epoch > 0) ? 1 : 0);
            object part = new { loanId = Id, debtId = Id, triggeredPenaltyCount = count, dueWarningSent = Deadlines > 0 && fault != "warning",
                nextPenaltyGameTicks = fault == "interval" && Deadlines > 0 ? 99 : 60000, penaltyIntervalTicks = 60000, annualInterestRate = 1f,
                lateFeeSilver = TotalDue - 120 + (fault == "fee" && Deadlines > 0 ? 1 : 0), totalDueSilver = TotalDue + (fault == "total" && Deadlines > 0 ? 1 : 0),
                amountSilver = 30 + (fault == "debt-fee" && Deadlines > 0 ? 1 : 0), completed = repaid && fault != "quest" };
            return new { ticks = 100, mechanoids = fault == "raid" ? 0 : Deadlines, raids = new[] { "mechanoid" }, loans = IsDebt ? Array.Empty<object>() : new[] { part }, debts = IsDebt ? new[] { part } : Array.Empty<object>() };
        }
        private object Invoke(JsonElement args)
        {
            var input = args.GetProperty("arguments");
            switch (args.String("action"))
            {
                case "StartRefreshBankStatus": break;
                case "StartCreateBankLoan":
                    Creates++; Requests.Add((input.Int("principalSilver"), input.Int("durationDays")));
                    if (mode == "bounds")
                    {
                        if (fault == "invalid-cash") Silver++;
                        if (fault == "invalid-snapshot") snapshot = "unexpected";
                        if (fault == "invalid-loan") activeLoan = true;
                        break;
                    }
                    Check(Requests[^1] == (mode == "overdue" ? 50 : 100, 2), "Wrong loan input.");
                    if (Creates > 1) { if (fault == "duplicate-loan") Silver++; break; }
                    activeLoan = fault != "no-loan";
                    Silver += (mode == "overdue" ? 50 : 100) + (fault == "delivery" ? 1 : 0);
                    snapshot = fault switch { "snapshot" => snapshot, "empty-snapshot" => "", "blank-snapshot" => " ", _ => "created" };
                    break;
                case "StartRepayBankLoan":
                case "StartRepayBankDebt":
                    Repayments++;
                    if (IsDebt) Check(input.GetProperty("debt").String("handle") == "fine-record", "Repayment ignored original debt record reference.");
                    // Fixed transcript: one insufficient payment, one successful payment, one stale replay.
                    if (mode != "loan" && Repayments == 1) { if (fault == "insufficient-cash") Silver--; break; }
                    if (repaid) { if (fault == "stale-cash") Silver--; break; }
                    repaid = true; Silver -= (IsDebt ? 30 : TotalDue) + (fault == "repayment-cash" ? 1 : 0);
                    activeLoan = !IsDebt && fault == "open-loan"; debtOpen = IsDebt && fault == "open-debt";
                    snapshot = "repaid"; break;
                case "StartHireMercenary": Hires++; Silver -= 20; break;
                case "StartManualSnapshotUpload": Uploads++; break;
                case "StartMainMenuServerEntryFlow":
                    Check(!active && !input.Bool("createAccountIfMissing"), "Debt reconnect registered a fresh account or retained old session."); active = true; epoch++; Logins++; break;
                default: throw new InvalidOperationException("Unexpected bank action: " + args.String("action"));
            }
            return new { returnValue = true };
        }
        public void Dispose() => Runtime.Dispose();
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action, string context)
    {
        try { await action(); } catch (AssertionException) { return; }
        throw new InvalidOperationException("Expected bank assertion: " + context);
    }
}
