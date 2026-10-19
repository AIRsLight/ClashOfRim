using ClashOfRim.GameTests;
using RimTest.Protocol;
using RimTest.Testing;

internal static class GameColonyMoveScenarioTests
{
    internal static void Relocation() => RelocationAsync().GetAwaiter().GetResult();
    internal static void Gravship() => GravshipAsync().GetAwaiter().GetResult();
    internal static void State() => StateAsync().GetAwaiter().GetResult();
    private static async Task RelocationAsync()
    {
        // This must reject before settleMap even when the original pre-threat observation was enabled.
        using (var stale = await GameColonyMoveTestFixture.Create(kind: "site"))
        {
            stale.Fault = "still-disabled";
            var failure = await Reject<AssertionException>(stale.Run, stale.Fault);
            Check(failure.Message.Contains("Existing quest-map relocation is disabled", StringComparison.Ordinal), "Site failed for an unrelated reason.");
            Check(stale.Runtime.Count("clash.relocation.settleMap") == 0, "Stale site observation allowed settlement.");
        }
        foreach (string kind in new[] { "caravan", "site" })
        foreach (string label in new[] { "Relocate", "搬家", "Déménager", "Taşın" })
        {
            using var f = await GameColonyMoveTestFixture.Create(label == "搬家" ? "B" : "A", kind, label);
            f.Delayed = label == "搬家";
            await f.Run(); Completed(f);
            Check(f.Runtime.Count("clash.relocation.confirm") == 1, "Move confirmation repeated.");
            Check(kind == "site" ? f.RelocationReads == 3 && f.Runtime.Count("clash.fixture.relocationEnemy") == 2
                : f.Runtime.Count("world.invokeGizmo") == 1, "Relocation omitted current site observations or original gizmo.");
        }
        foreach (string fault in new[] { "not-site", "already-home", "threat-ignored", "disabled" })
        {
            using var f = await GameColonyMoveTestFixture.Create(kind: fault == "disabled" ? "caravan" : "site");
            f.Fault = fault; await Reject<AssertionException>(f.Run, fault);
            Check(!f.Moved && f.Repayments == 0, "Invalid relocation continued to commit/repay: " + fault);
        }
    }
    private static async Task GravshipAsync()
    {
        foreach (string client in new[] { "A", "B" })
        foreach (bool marker in new[] { false, true })
        {
            using var f = await GameColonyMoveTestFixture.Create(client, "flight", "起飞");
            f.ExistingMarker = marker; f.Delayed = client == "B" && marker;
            await f.Run(); Completed(f);
            Check(f.Starts == 2 && f.Stops == 2 && !f.Ticking, "Flight did not release both manual tick periods.");
            Check(f.Runtime.Count("clash.gravship.placeLanding") == (marker ? 0 : 1) && f.Runtime.Count("clash.gravship.land") == 1,
                "Flight replaced an existing marker or repeated landing.");
            Check(f.Runtime.Count("dialogs.choose") == 2, "Flight skipped the original warnings.");
        }
        foreach (string fault in new[] { "start-failed", "poll-failed", "second-poll-failed" })
        {
            using var f = await GameColonyMoveTestFixture.Create(kind: "flight"); f.Fault = fault;
            await Reject<IOException>(f.Run, fault);
            Check(!f.Ticking && f.Starts == f.Stops && !f.Moved && f.Repayments == 0, "Flight leaked ticking or continued after " + fault);
        }
        foreach (string fault in new[] { "engine-count", "engine-id", "fuel" })
        {
            using var f = await GameColonyMoveTestFixture.Create(kind: "flight"); f.Fault = fault;
            await Reject<AssertionException>(f.Run, fault);
            Check(f.Moved && !f.Reloaded, "Invalid engine state passed validation: " + fault);
        }
    }
    private static async Task StateAsync()
    {
        foreach (string kind in new[] { "caravan", "flight" })
        foreach (bool afterReload in new[] { false, true })
        foreach (string fault in new[] { "silver", "steel", "tile", "extra-home", "no-home", "pawn-missing", "pawn-duplicate",
            "colony", "site-tile", "site-generation", "site-duplicate", "loan", "dlc" })
        {
            using var f = await GameColonyMoveTestFixture.Create(kind: kind);
            f.Fault = afterReload ? "reload-" + fault : fault;
            var failure = await Reject<AssertionException>(f.Run, f.Fault);
            string reason = fault switch
            {
                "silver" or "steel" => "cargo", "tile" or "extra-home" or "no-home" => "exactly one home",
                "pawn-missing" or "pawn-duplicate" => "pawn identities", "colony" => "colony account",
                "site-tile" or "site-generation" or "site-duplicate" => "Server retained", "loan" => "bank loan", _ => "DLC pawn state"
            };
            Check(failure.Message.Contains(reason, StringComparison.Ordinal), "Fault failed for an unrelated reason: " + f.Fault + ": " + failure.Message);
            Check(f.Reloaded == afterReload && f.Repayments == 0, "State corruption passed the wrong validation phase: " + f.Fault);
        }
        foreach (string fault in new[] { "empty-snapshot", "blank-snapshot", "stale-snapshot", "repayment" })
        {
            using var f = await GameColonyMoveTestFixture.Create(); f.Fault = fault;
            if (fault == "stale-snapshot") await Reject<IOException>(f.Run, fault);
            else await Reject<AssertionException>(f.Run, fault);
            Check(f.Repayments == (fault == "repayment" ? 1 : 0), "Invalid commit continued or repayment repeated.");
        }
        // Prepared baselines are values belonging to their own fixture/client, not static or host state.
        using var a = await GameColonyMoveTestFixture.Create("A");
        using var b = await GameColonyMoveTestFixture.Create("B");
        var first = await a.Moves.Prepare("A"); var second = await b.Moves.Prepare("B");
        Check(first.ColonyId != second.ColonyId && first.Silver != second.Silver && first.Steel != second.Steel && first.LoanId != second.LoanId,
            "Prepared move baselines were shared between clients.");
    }
    private static void Completed(GameColonyMoveTestFixture f)
    {
        Check(f.Moved && f.Reloaded && f.Repayments == 1, "Move skipped destination, reload or repayment.");
        Check(f.Runtime.Count("game.save") == 1 && f.Runtime.Count("game.load") == 1 && f.Runtime.Count("clash.dlc.pawns") == 2,
            "Move did not verify DLC before and after a single reload.");
        Check(f.Runtime.Calls.All(c => c.Client == f.Client), "Move crossed client sessions.");
        if (f.Delayed) Check(f.CommitReads >= 3 && f.ReloadReads >= 4, "Move accepted pending commit or old/uncontrolled Game epoch.");
        string prefix = f.Kind == "flight" ? "rimtest-gravship-" : "rimtest-relocation-" + f.Kind + "-";
        Check(f.Runtime.Calls.Single(c => c.Method == "game.save").Args.String("name").StartsWith(prefix, StringComparison.Ordinal), "Reload lost scenario save identity.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<T> Reject<T>(Func<Task> action, string context) where T : Exception
    {
        try { await action(); } catch (T failure) { return failure; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + context);
    }
}
