using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientOperationFollowupTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var previous = CultureInfo.CurrentCulture; int cases = 0;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (string operation in new[] { "shop", "create", "self", "drop", "accept", "baseline", "diplomacy", "failure" })
                foreach (bool deferred in new[] { false, true })
                {
                    var f = New(deferred); await f.Ports.PumpUntil(Dispatch(f, operation, f.Step));
                    string[] expected = operation switch
                    {
                        "shop" => deferred ? new[] { "Events", "Shop" } : new[] { "Shop" },
                        "create" => deferred ? new[] { "Events" } : Array.Empty<string>(),
                        "baseline" => new[] { "Markers" }, "diplomacy" => new[] { "Players", "Markers" },
                        _ => new[] { "Events" }
                    };
                    Require(f.Calls.OrderBy(x => x).SequenceEqual(expected.OrderBy(x => x)) && !f.Sync.IsBusy, "Wrong completion destinations: " + operation);
                    string? key = operation switch
                    {
                        "self" or "drop" => "ClashOfRim.Trade.ReasonFulfillment", "failure" => "ClashOfRim.Events.ReasonFailureReport",
                        "baseline" => "ClashOfRim.Admin.WorldBaselineRefreshReason", "diplomacy" => "ClashOfRim.Diplomacy.RefreshReasonRelationChanged", _ => null
                    };
                    Require(f.Keys.SequenceEqual(key is null ? Array.Empty<string>() : new[] { key }), "Wrong completion reason: " + operation);
                    if (operation is "self" or "drop" or "failure") Require(f.Reason == culture + ":" + key, "Lost localized refresh reason.");
                    if (operation == "accept") Require(f.Reason is null, "Trade acceptance started automatic application instead of reading queue metadata.");
                    cases++;
                    foreach (string change in new[] { "user", "server", "colony", "auth", "snapshot", "game", "world", "login", "generation" })
                    {
                        f = New(deferred); f.Change(change); await f.Ports.PumpUntil(Dispatch(f, operation, f.Step));
                        Require(f.Calls.IsEmpty && f.Keys.Count == 0, "Completion borrowed a new source: " + operation + "/" + change); cases++;
                        if (key is null) continue;
                        f = New(deferred); bool reached = false;
                        f.Hook = point => { if (point == "text") { f.Hook = null; reached = true; f.Change(change); } };
                        await f.Ports.PumpUntil(Dispatch(f, operation, f.Step));
                        Require(reached && f.Calls.IsEmpty, "Translation rebound completion: " + operation + "/" + change); cases++;
                    }
                }
                foreach (string change in new[] { "login", "game", "world", "snapshot", "generation" })
                {
                    var f = New(true); bool reached = false;
                    f.Hook = point => { if (point == "event-start") { f.Hook = null; reached = true; f.Change(change); } };
                    await f.Ports.PumpUntil(f.Operations.ShopCompleted(f.Step));
                    Require(reached && !f.Calls.Contains("Shop"), "Event resume rebound the following shop refresh."); cases++;
                    f = New(false); reached = false;
                    f.Hook = point => { if (point == "domain-text") { f.Hook = null; reached = true; f.Change(change); } };
                    await f.Ports.PumpUntil(f.Operations.DiplomacyFinished(f.Step));
                    Require(reached && !f.Calls.Contains("Markers"), "Player refresh rebound the following marker refresh."); cases++;
                }
                {
                    var f = New(false); int steps = 0;
                    bool Step(Action action) { bool applied = f.Step(action); if (++steps == 1) f.Change("login"); return applied; }
                    await f.Ports.PumpUntil(f.Operations.TradeAccepted(Step));
                    Require(f.Calls.IsEmpty && steps == 2, "Trade queue captured from one source but scheduled from another."); cases++;
                }
                cases += CompletionReceipts();
            }
            Console.WriteLine($"PASS: operation followups and transaction completion receipts ({cases} scenarios)");
        }
        finally { CultureInfo.CurrentCulture = previous; ClientSessionWork.Reset(); }
    }
    private static ClientFollowupTestPorts New(bool deferred)
    {
        var f = new ClientFollowupTestPorts(ClientSessionFollowupSource.MapLogin);
        if (deferred) f.Events.Defer("pending", "waiting");
        return f;
    }
    private static Task Dispatch(ClientFollowupTestPorts f, string operation, Func<Action, bool> step) => operation switch
    {
        "shop" => f.Operations.ShopCompleted(step), "create" => f.Operations.TradeCompleted(ClientTradeOperation.Create, step),
        "self" => f.Operations.TradeCompleted(ClientTradeOperation.SelfDelivery, step), "drop" => f.Operations.TradeCompleted(ClientTradeOperation.ServerDropPod, step),
        "accept" => f.Operations.TradeAccepted(step), "baseline" => f.Operations.BaselineUpdated(step),
        "diplomacy" => f.Operations.DiplomacyFinished(step), _ => f.Operations.FailureReported(step)
    };
    private static int CompletionReceipts()
    {
        int cases = 0;
        foreach (bool published in new[] { false, true })
        foreach (string change in new[] { "none", "user", "server", "colony", "auth", "snapshot", "game", "login", "generation", "scope", "unrelated-admission" })
        {
            var f = New(false); bool scope = true;
            var transaction = new ClientSnapshotTransaction(f.Session, f.Ports, new(f.Ports, f.Ports));
            Require(f.Sync.TryBeginManual(out var lease), "Missing source admission.");
            var attempt = transaction.Capture(lease!);
            if (published) transaction.Publish(attempt, "saved", "lineage");
            var step = transaction.CaptureCompletion(attempt, published ? "saved" : "base", () => scope);
            lease!.Dispose(); ClientSynchronizationCoordinator.Lease? next = null;
            if (change == "scope") scope = false;
            else if (change == "unrelated-admission") Require(f.Sync.TryBeginManual(out next), "Missing successor admission.");
            else f.Change(change);
            int calls = 0; bool allowed = change is "none" or "unrelated-admission";
            Require(step(() => calls++) == allowed && calls == (allowed ? 1 : 0), "Transaction completion rebound: " + published + "/" + change);
            if (allowed)
            {
                Require(!step(() => { calls++; f.Change("login"); }) && calls == 2 && !step(() => calls++), "Completion did not check after a reentrant action.");
            }
            next?.Dispose(); f.Sync.Reset(); cases++;
        }
        foreach (string invalid in new[] { "released", "wrong-snapshot", "scope" })
        {
            var f = New(false); var tx = new ClientSnapshotTransaction(f.Session, f.Ports, new(f.Ports, f.Ports));
            f.Sync.TryBeginManual(out var lease); var attempt = tx.Capture(lease!);
            if (invalid == "released") lease!.Dispose();
            var step = tx.CaptureCompletion(attempt, invalid == "wrong-snapshot" ? "other" : "base", () => invalid != "scope");
            Require(!step(() => throw new InvalidOperationException("Unowned completion was minted.")), "Invalid completion accepted.");
            lease!.Dispose(); cases++;
        }
        return cases;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
