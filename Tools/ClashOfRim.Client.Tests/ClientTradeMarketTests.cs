using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.Protocol;

internal static class ClientTradeMarketTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                await PagingAndCopies();
                await SupersededPages();
                await Commands();
                await CancellationDuringSelection();
                await FailuresAndAdmission();
            }
            foreach (string change in new[] { "user", "colony", "auth", "server", "base", "game", "login", "reset", "generation" })
            {
                await LateRead(change);
                await LateCommand(change);
            }
            await LatePreviewsAndActions();
        }
        finally { CultureInfo.CurrentCulture = original; ClientSessionWork.Reset(); }
    }

    private static async Task PagingAndCopies()
    {
        using var f = new Fixture();
        var first = Order("a");
        var initial = Page(first, Order("b"));
        await f.Load(initial);
        int version = f.State.Version;
        first.OfferedThings[0].Metadata["label"] = "external";
        var copy = f.State.CopyOrders(); copy[0].Owner!.UserId = "external"; copy[0].OfferedThings[0].Metadata["label"] = "external"; copy.Clear();
        Require(f.State.Count == 2 && f.State.CopyOrders()[0].Owner!.UserId == "seller" && f.State.CopyOrders()[0].OfferedThings[0].Metadata["label"] == "中文 à", "Market exposes mutable DTOs.");
        Task next = f.Market.RefreshAsync(reset: false);
        var call = await f.Next(); Require(call.Offset == 2 && call.Limit == 10, "Market did not advance its page offset.");
        call.Complete(Ok(Page(Order("b"), Order("c")))); await next;
        Require(f.State.Count == 3 && f.State.Version > version, "Overlap duplicated an order or failed to notify the view.");
        next = f.Market.RefreshAsync(reset: false); call = await f.Next();
        Require(call.Offset == 4, "Duplicate IDs stalled the server cursor.");
        call.Complete(Ok(Page())); await next;
        Require(!f.State.HasMore && !f.State.Loading, "Empty page caused an endless load-more loop.");
        int sent = f.Calls; await f.Market.RefreshAsync(reset: false); Require(f.Calls == sent, "Terminal page sent another request.");
        await f.Load(new() { Result = Accepted(), TradeMarketplaceEnabled = false, HasMore = true, Orders = new() { Order("hidden") } }, "History");
        Require(!f.State.Enabled && !f.State.HasMore && f.State.Count == 0 && f.State.Message == "Disabled", "Disabled market leaked listings.");
        Require(f.Last!.Limit == 5, "History changed its page size.");
    }

    private static async Task SupersededPages()
    {
        using var f = new Fixture();
        Task old = f.Market.RefreshAsync(); var oldCall = await f.Next();
        Task current = f.Market.RefreshAsync("Mine"); var newCall = await f.Next();
        oldCall.Complete(Ok(Page(Order("old")))); await old;
        Require(f.State.Loading && f.State.SelectedScope == "Mine" && f.State.Count == 0, "Old page/finally replaced the selected scope or cleared the current read.");
        newCall.Complete(Ok(Page(Order("new")))); await current;
        Require(f.State.CopyOrders().Single().EventId == "new" && !f.State.Loading, "Current page failed after supersession.");
        int before = f.Calls; await f.Market.RefreshAsync("Open", reset: false); Require(f.Calls == before, "Load-more borrowed a different scope.");
        var refresh = f.Market.CaptureRefresh();
        await f.Load(Page(Order("history")), "History"); before = f.Calls;
        await refresh(); Require(f.Calls == before && f.State.SelectedScope == "History", "Delayed caravan refresh replaced a newer market selection.");
    }

    private static async Task Commands()
    {
        using var f = new Fixture();
        await f.Load(Page(Order("order")));
        int version = f.State.Version;
        var clicked = f.State.CopyOrders().Single();
        Task work = f.Market.AcceptAsync(clicked, true); clicked.EventId = "mutated";
        var call = await f.Next();
        Require(call.Kind == "accept" && call.EventId == "order" && call.Postage && call.Context.UserId == "alice" && call.Context.CurrentSnapshotId == "base"
            && call.Key!.StartsWith("trade-accept:alice:order:", StringComparison.Ordinal) && f.Sync.IsBusy, "Accept did not capture its intent/identity/admission before dispatch.");
        call.Complete(Ok(new ModAcceptTradeOrderResponseDto { Result = Accepted(), TradeEventId = "order", MemoEventId = "memo", MemoCreated = true })); await work;
        var accepted = f.State.CopyOrders().Single();
        Require(f.State.Version > version && accepted.ViewerHasAccepted && accepted.ViewerAcceptedMemoEventId == "memo" && accepted.AcceptedMemoCount == 1
            && f.AcceptedNotices == 1 && !f.Sync.IsBusy, "Acceptance was not published once or did not release admission.");
        work = f.Market.AcceptAsync(accepted, false); call = await f.Next();
        call.Complete(Ok(new ModAcceptTradeOrderResponseDto { Result = Accepted(), TradeEventId = "order", MemoEventId = "memo", MemoCreated = false })); await work;
        Require(f.State.CopyOrders().Single().AcceptedMemoCount == 1 && f.State.Message.StartsWith("StatusAcceptMemoExists", StringComparison.Ordinal), "Repeated accept counted the memo twice.");

        // A read started before a successful decision must not undo that decision.
        Task stale = f.Market.RefreshAsync(reset: false); var staleCall = await f.Next();
        work = f.Market.AcceptAsync(accepted, false); call = await f.Next();
        call.Complete(Ok(new ModAcceptTradeOrderResponseDto { Result = Accepted(), MemoEventId = "memo" })); await work;
        staleCall.Complete(Ok(Page(Order("order")))); await stale;
        Require(f.State.CopyOrders().Single().ViewerHasAccepted, "Late page undid an accepted memo.");
        version = f.State.Version; f.State.Fulfilled("order");
        Require(f.State.Version > version && f.State.CopyOrders().Single().Status == "AppliedToSnapshot", "Fulfillment did not invalidate the view.");

        foreach (bool memo in new[] { false, true })
        {
            var order = Order("cancel");
            order.Owner!.UserId = memo ? "seller" : "alice";
            order.ViewerHasAccepted = memo; order.ViewerAcceptedMemoEventId = memo ? "memo" : null; order.AcceptedMemoCount = memo ? 1 : 0;
            string scope = memo ? "AcceptedByMe" : "Mine";
            await f.Load(Page(order), scope);
            work = f.Market.CancelAsync(order, memo); call = await f.Next();
            Require(call.Kind == "cancel" && call.Reason == (memo ? "AcceptorCancelledMemo" : "OwnerCancelled") && call.EventId == "cancel", "Cancellation paths lost their distinct server intent.");
            call.Complete(Ok(new ModCloseTradeOrderResponseDto { Result = Accepted(), TradeEventId = "cancel", NotifiedAcceptorCount = 2 }));
            var refresh = await f.Next(); Require(refresh.Scope == scope && !f.Sync.IsBusy, "Cancellation refreshed before releasing its exact admission or switched scope.");
            refresh.Complete(Ok(Page())); await work;
            Require(f.State.Count == 0 && !f.Sync.IsBusy, "Cancellation did not clear and refresh its view.");
        }
        Require(f.Ports.Published == 0 && f.Ports.Captures == 0 && !f.Mutations.IsPending, "Market commands entered a local snapshot mutation.");
    }

    private static async Task LateRead(string change)
    {
        using var f = new Fixture();
        Task old = f.Market.RefreshAsync(); var oldCall = await f.Next();
        f.Change(change);
        Task current = f.Market.RefreshAsync("History"); var newCall = await f.Next();
        f.State.Message = "new state";
        oldCall.Complete(Ok(Page(Order("obsolete")))); await old;
        Require(f.State.Loading && f.State.Count == 0 && f.State.Message == "new state", $"Old read/finally crossed {change}.");
        newCall.Complete(Ok(Page(Order("current")))); await current;
        Require(f.State.CopyOrders().Single().EventId == "current", $"Replacement read failed after {change}.");
    }

    private static async Task CancellationDuringSelection()
    {
        using var f = new Fixture();
        var own = Order("own"); own.Owner!.UserId = "alice";
        await f.Load(Page(own), "Mine");
        Task cancel = f.Market.CancelAsync(own, false); var call = await f.Next();
        await f.Load(Page(Order("history")), "History");
        int before = f.Calls;
        call.Complete(Ok(new ModCloseTradeOrderResponseDto { Result = Accepted() })); await cancel;
        Require(f.Calls == before && f.State.SelectedScope == "History" && f.State.CopyOrders().Single().EventId == "history", "Cancellation switched a newer selection back to its original scope.");
        cancel = f.Market.CancelAsync(own, false); call = await f.Next();
        f.Ports.Auth = "new-login"; f.State.Message = "new session";
        call.Complete(Ok(new ModCloseTradeOrderResponseDto { Result = Accepted() })); await cancel;
        Require(f.Calls == before + 1 && f.State.Message == "new session" && !f.Sync.IsBusy, "Late cancellation refreshed or published in a replacement session.");
    }

    private static async Task LateCommand(string change)
    {
        using var f = new Fixture();
        Task old = f.Market.AcceptAsync(Order("old"), false); var oldCall = await f.Next();
        f.Change(change);
        // A base change alone does not end the identity or free an active operation.
        // Its late result is rejected; once it releases, a fresh command can enter.
        if (change == "base")
        {
            f.State.Message = "new base";
            oldCall.Complete(Ok(new ModAcceptTradeOrderResponseDto { Result = Accepted(), MemoEventId = "old" })); await old;
            Require(f.AcceptedNotices == 0 && !f.Sync.IsBusy && f.State.Message == "new base", "Old command published across a new base.");
            return;
        }
        Task current = f.Market.AcceptAsync(Order("current"), true); var newCall = await f.Next();
        f.State.Message = "new command";
        oldCall.Complete(Ok(new ModAcceptTradeOrderResponseDto { Result = Accepted(), MemoEventId = "old" })); await old;
        Require(f.Sync.IsBusy && f.AcceptedNotices == 0 && f.State.Message == "new command", $"Old command/finally crossed {change} or released the new lease.");
        newCall.Complete(Ok(new ModAcceptTradeOrderResponseDto { Result = Accepted(), MemoEventId = "current" })); await current;
        Require(!f.Sync.IsBusy && f.AcceptedNotices == 1, $"Replacement command failed after {change}.");
    }

    private static async Task LatePreviewsAndActions()
    {
        using var f = new Fixture();
        await f.Load(Page(Order("old"))); var preview = await f.Ports.Next();
        int invoked = 0; Action old = f.Market.Bind(() => invoked++);
        f.Ports.Snapshot = "advanced"; old(); Require(invoked == 1, "Normal snapshot progression invalidated a UI choice.");
        f.Ports.GameOwner = new(); preview(); old();
        Require(invoked == 1 && f.Previews == 0, "Old preview or UI callback borrowed a new Game.");
        var refresh = f.Market.CaptureRefresh(); f.Ports.Auth = "replacement";
        int before = f.Calls; await refresh();
        Require(f.Calls == before, "Delayed caravan refresh captured a later login.");
    }

    private static async Task FailuresAndAdmission()
    {
        using var f = new Fixture();
        foreach (string failure in new[] { "transport", "rejected", "exception" })
        foreach (string command in new[] { "read", "accept", "cancel", "memo" })
        {
            var order = Order("order"); order.Owner!.UserId = command == "cancel" ? "alice" : "seller";
            order.ViewerHasAccepted = true; order.ViewerAcceptedMemoEventId = "memo";
            Task work = command == "read" ? f.Market.RefreshAsync() : command == "accept" ? f.Market.AcceptAsync(order, false) : f.Market.CancelAsync(order, command == "memo");
            var call = await f.Next(); int before = f.Calls;
            if (failure == "exception") call.Fail(new InvalidOperationException("injected"));
            else
            {
                var rejected = new ModProtocolResponseDto { Accepted = false, ErrorCode = (int)ProtocolErrorCode.ServerRejected, Message = "rejected" };
                call.Complete(command == "read" ? Response(new ModListTradeOrdersResponseDto { Result = rejected })
                    : command == "accept" ? Response(new ModAcceptTradeOrderResponseDto { Result = rejected })
                    : Response(new ModCloseTradeOrderResponseDto { Result = rejected }));
                object Response<T>(T response) => failure == "transport" ? ClashOfRimClientNetworkResult<T>.Failed("HttpError", "failed") : Ok(response);
            }
            await work;
            Require(!f.Sync.IsBusy && !f.State.Loading && f.AcceptedNotices == 0 && f.Calls == before && !f.Mutations.IsPending, "Failed market request retried, mutated local state or retained admission.");
        }
        int calls = f.Calls;
        f.Mutations.Begin(LocalMutationKind.Trade, "pending");
        await f.Market.RefreshAsync(); await f.Market.AcceptAsync(Order("order"), false);
        Require(f.Calls == calls && f.Mutations.IsPending, "Market crossed pending local mutation."); f.Mutations.Reset();
        f.Ports.Snapshot = ""; await f.Market.RefreshAsync(); await f.Market.AcceptAsync(Order("order"), false);
        Require(f.Calls == calls, "Market accepted missing base snapshot."); f.Ports.Snapshot = "base";
        Require(f.Sync.TryBeginManual(out var lease), "Could not occupy test admission.");
        await f.Market.AcceptAsync(Order("order"), false);
        Require(f.Calls == calls && lease!.IsCurrent && f.State.Message == "busy", "Busy rejection disturbed another operation."); lease!.Dispose();
        await f.Market.AcceptAsync(Order(""), false); await f.Market.CancelAsync(Order("foreign"), false);
        Require(f.Calls == calls, "Invalid trade command reached transport.");
    }

    private static ModProtocolResponseDto Accepted() => new() { Accepted = true };
    private static ClashOfRimClientNetworkResult<T> Ok<T>(T response) => ClashOfRimClientNetworkResult<T>.Ok(response);
    private static ModTradeOrderSummaryDto Order(string id) => new() { EventId = id, Status = "Recorded", Owner = new() { UserId = "seller", ColonyId = "colony" },
        OfferedThings = new() { new() { DefName = "Steel", StackCount = 3, Metadata = new() { ["label"] = "中文 à" } } } };
    private static ModListTradeOrdersResponseDto Page(params ModTradeOrderSummaryDto[] orders) => new() { Result = Accepted(), Orders = orders.ToList(), TotalCount = 20, HasMore = true };

    internal sealed record Call(string Kind, ClashOfRimClientNetworkContext Context, CancellationToken Token, string? Scope = null, int Offset = 0, int Limit = 0,
        string? Key = null, string? EventId = null, bool Postage = false, string? Reason = null)
    {
        internal readonly TaskCompletionSource<object> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Complete(object result) => Completion.SetResult(result);
        internal void Fail(Exception ex) => Completion.SetException(ex);
    }
    internal sealed class Fixture : IDisposable, IClientTradeMarketTransport, IClientTradeMarketPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSynchronizationCoordinator Sync = new(() => DateTime.UtcNow);
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientTradeState State;
        internal readonly ClientTradeMarket Market;
        private readonly ConcurrentQueue<Call> calls = new();
        private readonly SemaphoreSlim ready = new(0);
        internal int Calls, Previews, AcceptedNotices;
        internal Call? Last;
        internal Fixture()
        {
            ClientSessionWork.Reset();
            Session = new(Ports, Ports, new ClientSessionConnection(Ports));
            State = new(Session, Ports, this);
            var transactions = new ClientSnapshotTransaction(Session, Ports, new ClientSnapshotPublication(Ports));
            Market = new(Session, State, Mutations, new ClientSnapshotMutationAdmission(Session, Sync, Mutations, transactions), transactions, this, this);
            Market.Accepted += step => AcceptedNotices++;
        }
        internal async Task Load(ModListTradeOrdersResponseDto response, string scope = "Open")
        { Task work = Market.RefreshAsync(scope); var call = await Next(); call.Complete(Ok(response)); await work; }
        internal async Task<Call> Next()
        {
            if (!await ready.WaitAsync(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Trade did not dispatch an expected request.");
            Require(calls.TryDequeue(out var call), "Trade call queue lost an entry."); return call!;
        }
        private async Task<ClashOfRimClientNetworkResult<T>> Send<T>(Call call)
        { Last = call; Calls++; calls.Enqueue(call); ready.Release(); return (ClashOfRimClientNetworkResult<T>)await call.Completion.Task; }
        public Task<ClashOfRimClientNetworkResult<ModListTradeOrdersResponseDto>> ListAsync(ClashOfRimClientNetworkContext c, string s, int offset, int limit, CancellationToken t) => Send<ModListTradeOrdersResponseDto>(new("read", c, t, s, offset, limit));
        public Task<ClashOfRimClientNetworkResult<ModAcceptTradeOrderResponseDto>> AcceptAsync(ClashOfRimClientNetworkContext c, string key, string id, bool postage, CancellationToken t) => Send<ModAcceptTradeOrderResponseDto>(new("accept", c, t, Key: key, EventId: id, Postage: postage));
        public Task<ClashOfRimClientNetworkResult<ModCloseTradeOrderResponseDto>> CancelAsync(ClashOfRimClientNetworkContext c, string key, string id, string reason, CancellationToken t) => Send<ModCloseTradeOrderResponseDto>(new("cancel", c, t, Key: key, EventId: id, Reason: reason));
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "new"; break; case "colony": Ports.Colony = "new"; break;
                case "auth": Ports.Auth = "new"; break; case "server": Ports.Server = "http://new"; break;
                case "base": Ports.Snapshot = "new"; break; case "game": Ports.GameOwner = new(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth }); break;
                case "reset": State.Reset(); break; case "generation": ClientSessionWork.Reset(); Sync.Reset(); break;
            }
        }
        public string InitialStatus => "initial";
        public string Busy => "busy";
        public string Text(string key, params (string Name, object? Value)[] args) => key + string.Join("", args.Select(a => ":" + a.Name + "=" + a.Value));
        public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => admission.ToString();
        public void PreparePreviews(IReadOnlyList<ModTradeOrderSummaryDto> orders) => Previews++;
        public void Diagnose(Exception exception) { }
        public void Dispose() { ClientSessionWork.Reset(); Sync.Reset(); ready.Dispose(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
