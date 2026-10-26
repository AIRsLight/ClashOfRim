using System;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;

internal static class ClientTradeCollectionTests
{
    internal static async Task RunAsync()
    {
        await ClaimWithoutSnapshot();
        await FailuresAndAdmission();
        foreach (string change in new[] { "user", "colony", "auth", "server", "base", "game", "login", "reset", "generation" })
            await LateClaim(change);
    }

    private static async Task ClaimWithoutSnapshot()
    {
        using var f = new ClientTradeMarketTests.Fixture();
        var order = Order();
        await f.Load(new() { Result = new() { Accepted = true }, TradeMarketplaceEnabled = false,
            Orders = new() { order }, HasMore = true, TotalCount = 2 }, "Mine");
        Check(!f.State.Enabled && f.State.Count == 1 && f.State.HasMore, "Disabling new trades hid outstanding owner proceeds.");
        Task stale = f.Market.RefreshAsync("Mine", reset: false); var page = await f.Next();
        var clicked = f.State.CopyOrders().Single();
        Task work = f.Market.ClaimAsync(clicked); clicked.EventId = "mutated";
        var call = await f.Next();
        Check(call.Kind == "claim" && call.EventId == "order" && call.Context.UserId == "alice" && call.Context.ColonyId == "colony"
            && call.Context.CurrentSnapshotId == "base" && call.Key!.StartsWith("trade-claim:alice:order:", StringComparison.Ordinal),
            "Claim lost its captured order, identity or base.");
        int calls = f.Calls;
        await f.Market.ClaimAsync(order);
        Check(f.Calls == calls && f.Sync.IsBusy, "Repeated click bypassed the active claim.");
        call.Complete(Reply()); await work;
        page.Complete(ClashOfRimClientNetworkResult<ModListTradeOrdersResponseDto>.Ok(new() { Result = new() { Accepted = true }, Orders = new() { order } }));
        await stale;
        Check(f.State.CopyOrders().Single().Status == "Collecting" && f.State.Message == "StatusClaimSucceeded" && !f.Sync.IsBusy,
            "Claim did not publish delivery state or stale paging undid it.");
        await f.Market.ClaimAsync(f.State.CopyOrders().Single());
        Check(f.Calls == calls && f.State.Message == "StatusClaimNotAllowed", "In-flight proceeds remained claimable.");
        Check(f.Ports.Published == 0 && f.Ports.Captures == 0 && !f.Mutations.IsPending,
            "Releasing a held delivery performed a snapshot mutation before actual item receipt.");
    }

    private static async Task FailuresAndAdmission()
    {
        using var f = new ClientTradeMarketTests.Fixture();
        foreach (string invalid in new[] { "owner", "colony", "open", "delivering", "complete", "id" })
        {
            var order = Order();
            switch (invalid)
            {
                case "owner": order.Owner!.UserId = "other"; break;
                case "colony": order.Owner!.ColonyId = "other"; break;
                case "open": order.Status = "Recorded"; break;
                case "delivering": order.Status = "Collecting"; break;
                case "complete": order.Status = "AppliedToSnapshot"; break;
                case "id": order.EventId = ""; break;
            }
            await f.Market.ClaimAsync(order);
        }
        Check(f.Calls == 0, "Invalid claim reached the server.");
        foreach (string failure in new[] { "transport", "rejected", "exception" })
        {
            await f.Load(new() { Result = new() { Accepted = true }, Orders = new() { Order() } }, "Mine");
            Task work = f.Market.ClaimAsync(Order()); var call = await f.Next(); int calls = f.Calls;
            if (failure == "exception") call.Fail(new InvalidOperationException("injected"));
            else if (failure == "transport") call.Complete(ClashOfRimClientNetworkResult<ModCloseTradeOrderResponseDto>.Failed("HttpError", "failed"));
            else call.Complete(ClashOfRimClientNetworkResult<ModCloseTradeOrderResponseDto>.Ok(new() {
                Result = new() { Accepted = false, ErrorCode = (int)ProtocolErrorCode.Conflict, Message = "rejected" } }));
            await work;
            Check(f.State.CopyOrders().Single().Status == "Traded" && f.Calls == calls && !f.Sync.IsBusy,
                "Failed claim retried itself, hid the items or leaked admission.");
            work = f.Market.ClaimAsync(Order()); call = await f.Next(); call.Complete(Reply()); await work;
            Check(f.State.CopyOrders().Single().Status == "Collecting", "A failed claim could not be retried.");
        }
        int before = f.Calls;
        f.Ports.Snapshot = ""; await f.Market.ClaimAsync(Order()); f.Ports.Snapshot = "base";
        Check(f.Sync.TryBeginManual(out var lease), "Could not occupy admission.");
        await f.Market.ClaimAsync(Order());
        Check(f.Calls == before && lease!.IsCurrent, "Claim bypassed snapshot or busy admission."); lease!.Dispose();
        Check(f.Ports.Published == 0 && f.Ports.Captures == 0 && !f.Mutations.IsPending, "Claim failures modified local inventory or save state.");
    }

    private static async Task LateClaim(string change)
    {
        using var f = new ClientTradeMarketTests.Fixture();
        Task old = f.Market.ClaimAsync(Order()); var oldCall = await f.Next();
        f.Change(change);
        if (change == "base")
        {
            f.State.Message = "new base"; oldCall.Complete(Reply()); await old;
            Check(f.State.Message == "new base" && !f.Sync.IsBusy, "Late claim crossed snapshot advancement.");
            return;
        }
        var current = Order(); current.Owner!.UserId = f.Ports.User; current.Owner.ColonyId = f.Ports.Colony;
        Task work = f.Market.ClaimAsync(current); var call = await f.Next();
        f.State.Message = "new session"; oldCall.Complete(Reply()); await old;
        Check(f.State.Message == "new session" && f.Sync.IsBusy, "Late claim published in a new session or released its admission: " + change);
        call.Complete(Reply()); await work;
        Check(!f.Sync.IsBusy && f.State.Message == "StatusClaimSucceeded", "Replacement claim failed: " + change);
    }

    private static ModTradeOrderSummaryDto Order() => new() { EventId = "order", Status = "Traded", Owner = new() { UserId = "alice", ColonyId = "colony" } };
    private static ClashOfRimClientNetworkResult<ModCloseTradeOrderResponseDto> Reply() => ClashOfRimClientNetworkResult<ModCloseTradeOrderResponseDto>.Ok(
        new() { Result = new() { Accepted = true }, TradeEventId = "order", TerminalStatus = "Collecting" });
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
