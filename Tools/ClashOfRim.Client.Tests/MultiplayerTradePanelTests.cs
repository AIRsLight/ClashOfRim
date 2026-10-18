using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Modules.Trades;

internal static class MultiplayerTradePanelTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                await ViewsAndRefresh(); await FiltersAndPaging(); await Capabilities(); await Commands();
            }
            foreach (string change in new[] { "close", "filter", "user", "colony", "auth", "server", "game", "login", "reset", "generation" })
            foreach (bool afterGoodsConfirmation in new[] { false, true })
                await ObsoleteCommands(change, afterGoodsConfirmation);
            await LateReads();
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static async Task ViewsAndRefresh()
    {
        using var f = new ClientTradeMarketTests.Fixture();
        var interaction = new Interaction(); var panel = Panel(f, interaction);
        Check(panel.Scope == "Open" && panel.SelectedId is null && panel.ReadOrders().Count == 0, "Initial trade page leaked state.");
        Check(f.Sync.TryBeginManual(out var busy), "Cannot occupy manual operation.");
        await panel.RefreshAsync(10); Check(f.Calls == 0, "Automatic refresh crossed manual synchronization.");
        await Reply(f, panel.RefreshAsync(10, manual: true), Order("I"), Order("i"));
        Check(busy!.IsCurrent, "Read released another operation's admission."); busy.Dispose();
        Check(panel.SelectedId == "I" && panel.SelectedOrder!.EventId == "I", "Initial selection did not match details.");
        var view = panel.ReadOrders();
        Check(ReferenceEquals(view, panel.ReadOrders()), "Unchanged view rebuilt its cache.");
        view[0].OfferedThings[0].Metadata["label"] = "edited";
        Check(f.State.CopyOrders()[0].OfferedThings[0].Metadata["label"] == "中文 à", "Page cache mutated business state.");
        panel.SelectOrder("i");
        await panel.RefreshAsync(39.9f); Check(f.Calls == 1, "Refresh ignored its deadline.");
        var work = panel.RefreshAsync(40);
        Check(panel.SelectedId == "i" && panel.SelectedOrder is null, "Refresh lost selection before replacement arrived.");
        await Reply(f, work, Order("I"), Order("i"));
        Check(panel.SelectedId == "i" && panel.SelectedOrder!.EventId == "i", "Refresh changed a still-valid selection.");
        panel.RequestRefresh(); await Reply(f, panel.RefreshAsync(40), Order("İ"));
        Check(panel.SelectedId == "İ" && panel.SelectedOrder!.EventId == "İ", "Missing selection left highlight and detail inconsistent.");
        await Reply(f, panel.RefreshAsync(40, manual: true));
        Check(panel.SelectedId is null && panel.SelectedOrder is null && !panel.HasMore, "Empty page retained selected details or load-more.");
        panel.OpenCreate(); Check(interaction.Creates == 1, "Create did not reach the existing editor.");
        await f.Load(Page(Order("retained")));
        object owner = panel.Ownership; panel.Close();
        Check(!ReferenceEquals(owner, panel.Ownership) && f.State.Count == 1 && panel.SelectedId == "retained", "Closing reset shared market data or retained ownership.");
        await Reply(f, panel.RefreshAsync(41), Order("reopened"));
        Check(panel.SelectedId == "reopened" && panel.Status == f.State.Message, "Reopening failed to refresh immediately or expose status.");
        f.Change("base"); Check(panel.SelectedId == "reopened", "Snapshot advancement cleared the page.");
        Task failed = panel.RefreshAsync(42, manual: true); var request = await f.Next();
        Check(panel.SelectedId == "reopened" && panel.ReadOrders().Count == 0, "Pending refresh discarded its selection.");
        request.Complete(ClashOfRimClientNetworkResult<ModListTradeOrdersResponseDto>.Failed("test", "failed")); await failed;
        Check(panel.SelectedId is null && panel.SelectedOrder is null, "Failed refresh retained a ghost selection after loading stopped.");
        await Reply(f, panel.RefreshAsync(43, manual: true), Order("before-auth"));
        f.Change("auth"); Check(panel.ReadOrders().Count == 0 && panel.SelectedId is null && panel.Scope == "Open", "Replacement session retained cached rows.");
    }

    private static async Task FiltersAndPaging()
    {
        using var f = new ClientTradeMarketTests.Fixture();
        var first = Panel(f); var second = Panel(f);
        await Reply(f, first.RefreshAsync(1), Order("open"));
        Check(second.ReadOrders().Single().EventId == "open", "Windows did not share the market query.");
        await Reply(f, second.SelectScopeAsync("History", 2), Order("history"));
        Check(f.Last!.Scope == "History" && f.Last.Limit == 5, "History paging was replaced in the page model.");
        Check(first.Scope == "Open" && first.ReadOrders().Count == 0 && !first.HasMore && !first.Loading, "Other window's filter leaked into this view.");
        Check(second.SelectedId == "history" && second.HasMore, "History selection or paging disappeared.");
        Check(!second.ShouldLoadMore(100, 100, 0) && !second.ShouldLoadMore(100, 300, 119.9f)
            && second.ShouldLoadMore(100, 300, 120), "Paging threshold changed or paged a non-scrolling view.");
        int calls = f.Calls; await first.LoadMoreAsync(); Check(f.Calls == calls, "Load-more borrowed the other filter's cursor.");
        Task more = second.LoadMoreAsync(); var request = await f.Next();
        Check(request.Offset == 1 && request.Limit == 5 && !second.ShouldLoadMore(100, 300, 200), "Load-more bypassed market cursor or in-flight gate.");
        await second.LoadMoreAsync(); Check(f.Calls == calls + 1, "Page duplicated an in-flight request.");
        request.Complete(Ok(Page(Order("history"), Order("next")))); await more;
        Check(second.ReadOrders().Select(o => o.EventId).SequenceEqual(new[] { "history", "next" }), "Page replaced market deduplication.");
        await Reply(f, second.LoadMoreAsync());
        Check(!second.HasMore && !second.ShouldLoadMore(100, 300, 200), "Terminal page still auto-paged.");
        await Reply(f, first.RefreshAsync(3, manual: true), Order("restored"));
        Check(second.Scope == "History" && second.ReadOrders().Count == 0 && first.SelectedId == "restored", "Independent scope did not survive another window refresh.");
        second.Close(); Check(second.Scope == "Open" && second.SelectedId == "restored", "Reopened window did not return to default filter.");
        Task disabled = first.RefreshAsync(4, manual: true); request = await f.Next();
        request.Complete(Ok(new ModListTradeOrdersResponseDto { Result = new() { Accepted = true }, TradeMarketplaceEnabled = false, Orders = new() { Order("hidden") }, HasMore = true })); await disabled;
        Check(!first.Enabled && first.ReadOrders().Count == 0 && !first.HasMore, "Disabled market leaked actionable listings.");
    }

    private static async Task Capabilities()
    {
        using var f = new ClientTradeMarketTests.Fixture(); var panel = Panel(f);
        var foreign = Order("foreign");
        Check(panel.ActionsFor(foreign) == new TradePanelActions(true, false, false, true, true), "Open foreign order actions changed.");
        foreign.ServerDropPodPostage!.PostageSilver = null;
        Check(panel.ActionsFor(foreign).ShowDropPod && !panel.ActionsFor(foreign).DropPod, "Missing price enabled drop-pod fulfillment.");
        foreign.ServerDropPodPostage.PostageSilver = 0; foreign.ServerDropPodPostage.Reachable = false;
        Check(!panel.ActionsFor(foreign).DropPod, "Unreachable postage enabled fulfillment.");
        foreign.AllowServerDropPod = false; Check(!panel.ActionsFor(foreign).ShowDropPod, "Disabled delivery option remained visible.");
        foreign.ViewerHasAccepted = true; foreign.ViewerAcceptedMemoEventId = "memo";
        Check(!panel.ActionsFor(foreign).Accept && panel.ActionsFor(foreign).CancelMemo, "Existing memo remained acceptable or could not be cancelled.");
        foreign.ViewerAcceptedMemoEventId = " "; Check(!panel.ActionsFor(foreign).CancelMemo, "Missing memo ID enabled cancellation.");
        var own = Order("own"); own.Owner!.UserId = "alice";
        Check(!panel.ActionsFor(own).Accept && !panel.ActionsFor(own).CancelOrder && !panel.ActionsFor(own).ShowDropPod, "Own Open order gained foreign or Mine actions.");
        await Reply(f, panel.SelectScopeAsync("Mine", 1), own);
        Check(panel.ActionsFor(own).CancelOrder, "Own Mine order cannot be cancelled.");
        own.Owner.ColonyId = "other";
        Check(panel.ActionsFor(own) == new TradePanelActions(false, false, false, false, false), "Another colony of same user gained actions.");
        await Reply(f, panel.SelectScopeAsync("History", 2), foreign);
        foreign.ViewerHasAccepted = false;
        Check(!panel.ActionsFor(foreign).Accept, "History gained the Open acceptance button.");
        foreign.Status = "AppliedToSnapshot";
        Check(panel.ActionsFor(foreign) == new TradePanelActions(false, false, false, false, false), "Closed order gained actions.");
    }

    private static async Task Commands()
    {
        using var f = new ClientTradeMarketTests.Fixture(); var interaction = new Interaction(); var panel = Panel(f, interaction);
        foreach (var command in new[] { TradePanelCommand.Accept, TradePanelCommand.CancelOrder, TradePanelCommand.CancelMemo, TradePanelCommand.DropPod })
        {
            var order = Order("captured");
            if (command == TradePanelCommand.CancelOrder) order.Owner!.UserId = "alice";
            if (command == TradePanelCommand.CancelMemo) { order.ViewerHasAccepted = true; order.ViewerAcceptedMemoEventId = "memo"; }
            Action apply = panel.Prepare(command, order);
            order.EventId = "mutated"; order.Owner!.UserId = "changed";
            order.OfferedThings[0].Metadata["label"] = "changed"; order.ServerDropPodPostage!.PostageSilver = 999;
            f.Change("base"); // An unsubmitted UI intent survives ordinary snapshot advancement.
            int calls = f.Calls; int confirmations = interaction.Confirmations;
            apply();
            if (command is TradePanelCommand.Accept or TradePanelCommand.DropPod)
            {
                Check(f.Calls == calls && interaction.Confirmations == confirmations + 1, "Goods confirmation was bypassed.");
                Check(interaction.Order!.EventId == "captured" && interaction.Order.OfferedThings[0].Metadata["label"] == "中文 à"
                    && interaction.Order.ServerDropPodPostage!.PostageSilver == 7, "Confirmation borrowed mutable DTO data.");
                interaction.Pending!();
            }
            if (command == TradePanelCommand.DropPod)
            {
                Check(interaction.Fulfilled == 1 && interaction.Order!.Owner!.UserId == "seller" && f.Calls == calls, "Drop-pod command lost its captured order or used market acceptance.");
                continue;
            }
            var sent = await f.Next();
            Check(sent.EventId == "captured" && sent.Context.CurrentSnapshotId == "new", "Prepared command lost its original order or used obsolete current snapshot.");
            if (command == TradePanelCommand.Accept)
            {
                Check(sent.Kind == "accept" && !sent.Postage, "Accepted-list command purchased postage.");
                sent.Complete(ClashOfRimClientNetworkResult<ModAcceptTradeOrderResponseDto>.Failed("test", "rejected"));
            }
            else
            {
                Check(sent.Kind == "cancel" && sent.Reason == (command == TradePanelCommand.CancelMemo ? "AcceptorCancelledMemo" : "OwnerCancelled"), "Cancellation intent changed.");
                sent.Complete(ClashOfRimClientNetworkResult<ModCloseTradeOrderResponseDto>.Failed("test", "rejected"));
            }
            // The UI intentionally exposes an Action; the existing market owns completion.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (f.Sync.IsBusy && DateTime.UtcNow < deadline) await Task.Delay(1);
            Check(!f.Sync.IsBusy && f.Calls == calls + 1, "Failed UI command retained admission or retried.");
        }
    }

    private static async Task ObsoleteCommands(string change, bool afterGoodsConfirmation)
    {
        using var f = new ClientTradeMarketTests.Fixture(); var interaction = new Interaction(); var panel = Panel(f, interaction);
        Action drop = panel.Prepare(TradePanelCommand.DropPod, Order("old"));
        var memo = Order("memo"); memo.ViewerHasAccepted = true; memo.ViewerAcceptedMemoEventId = "accepted";
        Action cancel = panel.Prepare(TradePanelCommand.CancelMemo, memo);
        Action accept = panel.Prepare(TradePanelCommand.Accept, Order("old"));
        Action? pendingDrop = null, pendingAccept = null;
        if (afterGoodsConfirmation)
        {
            drop(); pendingDrop = interaction.Pending;
            accept(); pendingAccept = interaction.Pending;
        }
        if (change == "close") panel.Close();
        else if (change == "filter")
        {
            await Reply(f, panel.SelectScopeAsync("Mine", 1));
            await Reply(f, panel.SelectScopeAsync("Open", 2));
        }
        else f.Change(change);
        int calls = f.Calls; int confirmations = interaction.Confirmations;
        if (afterGoodsConfirmation) { pendingDrop!(); pendingAccept!(); }
        else { drop(); accept(); }
        cancel();
        Check(f.Calls == calls && interaction.Fulfilled == 0 && interaction.Confirmations == confirmations,
            $"Old command crossed {change} ({(afterGoodsConfirmation ? "deferred" : "outer")} confirmation).");
    }

    private static async Task LateReads()
    {
        using var f = new ClientTradeMarketTests.Fixture(); var panel = Panel(f);
        Task old = panel.SelectScopeAsync("Mine", 1); var stale = await f.Next();
        Task current = panel.SelectScopeAsync("History", 2); var fresh = await f.Next();
        stale.Complete(Ok(Page(Order("obsolete")))); await old;
        Check(panel.Scope == "History" && panel.Loading && panel.SelectedId is null && panel.ReadOrders().Count == 0, "Late filter response replaced the current view.");
        fresh.Complete(Ok(Page(Order("current")))); await current;
        old = panel.RefreshAsync(3, manual: true); stale = await f.Next(); panel.Close();
        current = panel.RefreshAsync(4); fresh = await f.Next();
        stale.Complete(Ok(Page(Order("closed")))); await old;
        Check(panel.Scope == "Open" && panel.Loading && panel.ReadOrders().Count == 0, "Closed page's response replaced reopening.");
        fresh.Complete(Ok(Page(Order("reopened")))); await current;
        Check(panel.SelectedId == "reopened", "Reopened page did not receive its replacement request.");
    }

    private static ClientTradePanel Panel(ClientTradeMarketTests.Fixture f, Interaction? interaction = null) =>
        new(f.Session, f.Ports, f.State, f.Market, f.Sync, interaction ?? new());
    private static ModTradeOrderSummaryDto Order(string id) => new()
    {
        EventId = id, Status = "Recorded", Owner = new() { UserId = "seller", ColonyId = "colony" },
        AllowServerDropPod = true, ServerDropPodPostage = new() { Reachable = true, PostageSilver = 7 },
        OfferedThings = new() { new() { DefName = "Steel", StackCount = 3, Metadata = new() { ["label"] = "中文 à" } } }
    };
    private static ModListTradeOrdersResponseDto Page(params ModTradeOrderSummaryDto[] orders) =>
        new() { Result = new() { Accepted = true }, Orders = orders.ToList(), TotalCount = 20, HasMore = true };
    private static ClashOfRimClientNetworkResult<T> Ok<T>(T response) => ClashOfRimClientNetworkResult<T>.Ok(response);
    private static async Task Reply(ClientTradeMarketTests.Fixture f, Task work, params ModTradeOrderSummaryDto[] orders)
    { (await f.Next()).Complete(Ok(Page(orders))); await work; }
    private sealed class Interaction : IClientTradeInteraction
    {
        internal int Creates, Confirmations, Fulfilled;
        internal Action? Pending;
        internal ModTradeOrderSummaryDto? Order;
        public void OpenCreate() => Creates++;
        public void ConfirmGoods(ModTradeOrderSummaryDto order, Action confirmed) { Order = order; Pending = confirmed; Confirmations++; }
        public void FulfillByDropPod(ModTradeOrderSummaryDto order) { Order = order; Fulfilled++; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
