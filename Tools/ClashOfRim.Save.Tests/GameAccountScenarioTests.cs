using System.Text.Json;
using AIRsLight.ClashOfRim.Save;
using RimTest.Protocol;
using RimTest.Testing;

internal static class GameAccountScenarioTests
{
    internal static void Colonies(Func<string, SaveSnapshotPackage> factory) => ColoniesAsync(factory("account-unit")).GetAwaiter().GetResult();
    internal static void Workflow(Func<string, SaveSnapshotPackage> factory) => WorkflowAsync(factory("account-workflow-unit")).GetAwaiter().GetResult();
    private static async Task ColoniesAsync(SaveSnapshotPackage template)
    {
        using (var f = await GameAccountTestFixture.Create(template))
        {
            var prepared = f.SeedPreparation();
            foreach (string client in new[] { "A", "B" })
            {
                string survivor = client == "A" ? "B" : "A";
                var old = await f.Colonies.Abandon(client, survivor, prepared);
                f.Address = "http://updated-" + client;
                await f.Colonies.Rebuild(client, survivor, old, prepared);
                Check(old == "old-" + client && f.Colony[client] == "rebuilt-" + client, "Generation result was lost or shared between clients.");
            }
            Check(f.Confirmed == 2 && f.Cancelled == 2 && f.Entries == 2 && f.PlayerReads == 8, "Lifecycle skipped cancellation, delayed player observations or duplicated commands.");
            string trace = File.ReadAllText(Path.Combine(f.Runtime.Run.DirectoryPath, "trace.jsonl"));
            Check(trace.Split("\"accountLifecycle\":\"abandoned\"").Length == 3 && trace.Split("\"accountLifecycle\":\"rebuilt\"").Length == 3,
                "Lifecycle evidence omitted a client or phase.");
        }
        foreach (string fault in new[] { "countdown", "cancel-effect", "snapshot", "tombstone", "site", "loan", "debt", "contract", "credentials", "administrator", "history", "survivor-snapshot", "survivor-stock", "survivor-debt", "order", "memo", "deleted-achievement" })
        {
            using var f = await GameAccountTestFixture.Create(template); var prepared = f.SeedPreparation(); f.Fault = fault;
            await Reject(() => f.Colonies.Abandon("A", "B", prepared), fault);
            Check(f.Confirmed == (fault is "countdown" or "cancel-effect" ? 0 : 1), "Rejected abandonment continued/retried: " + fault);
        }
        foreach (string fault in new[] { "same-colony", "rebuilt-snapshot", "wrong-site-generation", "rebuilt-loan", "old-quest", "rebuilt-achievement", "changed-shop", "rebuilt-role" })
        {
            using var f = await GameAccountTestFixture.Create(template); var prepared = f.SeedPreparation();
            string old = await f.Colonies.Abandon("A", "B", prepared); f.Fault = fault;
            await Reject(() => f.Colonies.Rebuild("A", "B", old, prepared), fault);
            Check(f.Entries == 1, "Rebuild retried account creation: " + fault);
        }
        foreach (string fault in new[] { "abandon-response", "entry-response", "missing-history" })
        {
            using var f = await GameAccountTestFixture.Create(template); var prepared = f.SeedPreparation();
            if (fault == "abandon-response")
            {
                f.Fault = fault; await Interrupted(() => f.Colonies.Abandon("A", "B", prepared));
                Check(f.Confirmed == 1 && f.Entries == 0, "Ambiguous abandonment was retried or followed by rebuilding.");
            }
            else
            {
                string old = await f.Colonies.Abandon("A", "B", prepared); f.Fault = fault;
                await Interrupted(() => f.Colonies.Rebuild("A", "B", old, prepared));
                Check(f.Entries == 1, "Ambiguous rebuild/history was retried.");
            }
        }
        using (var first = await GameAccountTestFixture.Create(template))
        using (var second = await GameAccountTestFixture.Create(template))
        {
            var a = first.SeedPreparation(); var b = second.SeedPreparation();
            await first.Colonies.Abandon("A", "B", a);
            Check(second.Active["A"] && second.Snapshots.GetLatest("rimtest-A", "old-A") is not null && second.Ledger.Find(b.TradeId)!.Status != AIRsLight.ClashOfRim.Events.ServerEventStatus.Cancelled,
                "Lifecycle state leaked between independent fixtures.");
        }
    }
    private static async Task WorkflowAsync(SaveSnapshotPackage template)
    {
        using (var f = await GameAccountTestFixture.Create(template))
        {
            var prepared = await f.Scenario.PrepareLiabilities();
            Check(f.Trade.Creates == 1 && f.Trade.Accepts == 1 && f.Trade.Deliveries == 0 && prepared.TradeId == "order-1",
                "Lifecycle preparation skipped its real order/memo or fulfilled it prematurely.");
            Check(f.Loans.GetActive("rimtest-A", "old-A") is not null && f.Loans.GetActive("rimtest-B", "old-B") is not null
                && f.Loans.GetOpenDebts("rimtest-B", "old-B").Single().DebtId == prepared.DebtId && f.Mercenaries.CountOpenForColony("rimtest-B", "old-B") == 1,
                "Lifecycle preparation lost one loan, the death fine or the surviving contract.");
            Check(prepared.Shop.GetProperty("items").GetArrayLength() == 1 && f.Runtime.Calls.Count(c => c.Method == "clash.fixture.mercenaryDeath") == 1,
                "Preparation lost global shop baseline or repeated the death fixture.");
        }
        using (var f = await GameAccountTestFixture.Create(template))
        {
            await f.Scenario.Run();
            Check(f.Runtime.Run.Cases.Select(c => c.Id).SequenceEqual(GameAccountTestFixture.Cases) && f.Runtime.Run.Cases.All(c => c.Status == "passed"),
                "Account case order/reporting changed.");
            Check(f.Trade.Deliveries == 20 && f.Trade.Creates == 21 && f.Confirmed == 2 && f.Entries == 2,
                "Account workflow skipped a generation's real trade accumulation or colony lifecycle.");
            Check(f.Runtime.Calls.Count(c => c.Method == "clash.invoke" && c.Args.String("action") == "StartManualSnapshotUpload") == 2
                && f.Runtime.Count("game.load") == 1, "Account workflow omitted upload replay or rebuilt-colony reload.");
        }
        using (var f = await GameAccountTestFixture.Create(template))
        {
            f.Fault = "upload-replay"; await Reject(f.Scenario.Run, "upload-replay");
            Check(f.Runtime.Run.Cases.Count == 2 && f.Runtime.Run.Cases.Last().Id == GameAccountTestFixture.Cases[1] && f.Runtime.Run.Cases.Last().Status == "failed"
                && f.Confirmed == 0 && f.Trade.Creates == 10, "Failed history check was hidden or allowed later lifecycle cases to run.");
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action, string fault)
    { try { await action(); } catch (AssertionException) { return; } throw new InvalidOperationException("Expected account assertion: " + fault); }
    private static async Task Interrupted(Func<Task> action)
    { try { await action(); } catch (IOException) { return; } throw new InvalidOperationException("Expected account interruption."); }
}
