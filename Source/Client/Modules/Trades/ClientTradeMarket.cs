using System;
using System.Globalization;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Trades;

internal sealed class ClientTradeMarket
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientTradeState state;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientSnapshotMutationAdmission admission;
    private readonly ClientSnapshotTransaction transactions;
    private readonly IClientTradeMarketTransport transport;
    private readonly IClientTradeMarketPresentation presentation;
    internal ClientTradeMarket(ClientSessionLifecycle session, ClientTradeState state, ClientLocalMutationCoordinator mutations,
        ClientSnapshotMutationAdmission admission, ClientSnapshotTransaction transactions,
        IClientTradeMarketTransport transport, IClientTradeMarketPresentation presentation)
    { this.session = session; this.state = state; this.mutations = mutations; this.admission = admission;
        this.transactions = transactions; this.transport = transport; this.presentation = presentation; }

    internal event Action? Accepted;
    internal Action Bind(Action action)
    {
        var owner = state.Capture();
        return () => state.TryApply(owner, action);
    }

    internal Func<Task> CaptureRefresh()
    {
        using var p = ClientSessionWork.EnterPublication();
        var owner = state.Capture();
        int version = state.Version;
        return () =>
        {
            Task work = Task.CompletedTask;
            state.TryApply(owner, () =>
            {
                // A delayed world-marker refresh cannot overwrite a newer market
                // selection, page or decision that has already changed the view.
                if (state.Version == version) work = RefreshAsync(owner: owner);
            });
            return work;
        };
    }

    internal Task RefreshAsync(string scope = "Open", bool reset = true, ClientTradeState.Scope? owner = null)
    {
        using var p = ClientSessionWork.EnterPublication();
        Task work = Task.CompletedTask;
        state.TryApply(owner ?? state.Capture(), () =>
        {
            var context = session.ReadContext();
            var check = mutations.CheckAdmission(context.IsConfigured, !string.IsNullOrWhiteSpace(context.CurrentSnapshotId));
            if (check != LocalMutationAdmission.Allowed) { state.Message = presentation.Admission(check, mutations.Current); return; }
            var request = state.BeginPage(owner ?? state.Capture(), scope, reset);
            if (request is null) return;
            state.Message = presentation.Text(reset ? "StatusRefreshingMarket" : "StatusLoadingMoreMarket");
            work = ClientSessionWork.Run(() => ReadAsync(request));
        });
        return work;
    }

    private async Task ReadAsync(ClientTradeState.Page request)
    {
        try
        {
            if (!state.TryApply(request, static () => { })) return;
            var result = await transport.ListAsync(request.Context, request.Filter, request.Offset, request.Limit, request.Scope.Owner.Token).ConfigureAwait(false);
            state.TryApply(request, () =>
            {
                if (Failed(result, result.Response?.Result, "StatusMarketRefreshFailed", "StatusMarketRejected")) return;
                state.PublishPage(request, result.Response!);
                int version = state.Version;
                var orders = state.CopyOrders();
                if (request.Scope.Game.HasGame && orders.Count > 0)
                    request.Scope.Game.Enqueue(() => state.TryApply(request.Scope, () =>
                    {
                        if (state.Version == version) presentation.PreparePreviews(orders);
                    }, request.Context), static () => { });
            });
        }
        catch (Exception ex) { state.TryApply(request, () => Exception(ex, "StatusMarketException")); }
        finally { state.EndPage(request); }
    }

    internal Task AcceptAsync(ModTradeOrderSummaryDto order, bool postage)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!ValidId(order)) return Task.CompletedTask;
        var attempt = Begin();
        if (attempt is null) return Task.CompletedTask;
        var owner = state.Capture();
        string eventId = order.EventId;
        string key = FormattableString.Invariant($"trade-accept:{attempt.Context.UserId}:{eventId}:{DateTime.UtcNow.Ticks}");
        state.Message = presentation.Text("StatusAcceptingOrder", ("EVENTID", eventId));
        return ClientSessionWork.Run(() => ExecuteAsync(attempt, owner,
            () => transport.AcceptAsync(attempt.Context, key, eventId, postage, attempt.Owner.Token), r => r.Result,
            "StatusAccept", response =>
            {
                state.Accepted(eventId, response.MemoEventId);
                state.Message = presentation.Text(response.MemoCreated ? "StatusAcceptedOrder" : "StatusAcceptMemoExists",
                    ("EVENTID", response.TradeEventId), ("MEMOID", response.MemoEventId));
                Accepted?.Invoke();
            }));
    }

    internal Task CancelAsync(ModTradeOrderSummaryDto order, bool acceptedMemo)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!ValidId(order)) return Task.CompletedTask;
        var context = session.ReadContext();
        if (!(acceptedMemo ? ClientTradeRules.CanCancelMemo(order, context) : ClientTradeRules.IsOwnOpen(order, context)))
        { state.Message = presentation.Text(acceptedMemo ? "StatusCancelAcceptedMemoNotAllowed" : "StatusCancelNotAllowed"); return Task.CompletedTask; }
        var attempt = Begin();
        if (attempt is null) return Task.CompletedTask;
        string eventId = order.EventId, scope = state.SelectedScope;
        var owner = state.Capture();
        string key = (acceptedMemo ? "trade-cancel-memo:" + attempt.Context.UserId + ":" : "trade-cancel:")
            + eventId + ":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        state.Message = presentation.Text(acceptedMemo ? "StatusCancellingAcceptedMemo" : "StatusCancellingOrder");
        string prefix = acceptedMemo ? "StatusCancelAcceptedMemo" : "StatusCancel";
        return ClientSessionWork.Run(async () =>
        {
            bool completed = await ExecuteAsync(attempt, owner,
                () => transport.CancelAsync(attempt.Context, key, eventId, acceptedMemo ? "AcceptorCancelledMemo" : "OwnerCancelled", attempt.Owner.Token),
                r => r.Result, prefix, response =>
                {
                    state.Cancelled(eventId, acceptedMemo);
                    state.Message = presentation.Text(prefix + "Succeeded", ("COUNT", response.NotifiedAcceptorCount));
                }).ConfigureAwait(false);
            Task refresh = Task.CompletedTask;
            if (completed) state.TryApply(owner, () =>
            {
                // A newer tab selection owns its own read; do not switch it back.
                if (state.SelectedScope == scope) refresh = RefreshAsync(scope, owner: owner);
            });
            await refresh.ConfigureAwait(false);
        });
    }

    private ClientSnapshotTransaction.Attempt? Begin()
    {
        state.Capture();
        var result = admission.Begin();
        if (result.Attempt is { } attempt) state.Track(attempt.Admission);
        if (result.Attempt is null) state.Message = result.Failure == ClientSnapshotAdmissionFailure.Busy
            ? presentation.Busy : presentation.Admission(result.Admission, mutations.Current);
        return result.Attempt;
    }
    private bool ValidId(ModTradeOrderSummaryDto? order)
    {
        if (order is not null && !string.IsNullOrWhiteSpace(order.EventId)) return true;
        state.Message = presentation.Text("StatusMissingOrderEventId"); return false;
    }

    // Accept and both cancellation intents share one lifecycle. Their effects only
    // update managed market state, so no game-thread queue or snapshot publication is needed.
    private async Task<bool> ExecuteAsync<T>(ClientSnapshotTransaction.Attempt attempt, ClientTradeState.Scope owner,
        Func<Task<ClashOfRimClientNetworkResult<T>>> send, Func<T, ModProtocolResponseDto?> resultOf,
        string prefix, Action<T> accepted)
    {
        bool completed = false;
        try
        {
            if (!TryApply(attempt, owner, static () => { })) return false;
            var result = await send().ConfigureAwait(false);
            TryApply(attempt, owner, () =>
            {
                if (Failed(result, result.Response is null ? null : resultOf(result.Response), prefix + "Failed", prefix + "Rejected")) return;
                accepted(result.Response!); completed = true;
            });
        }
        catch (Exception ex) { TryApply(attempt, owner, () => Exception(ex, prefix + "Exception")); }
        finally { attempt.Admission.Dispose(); }
        return completed;
    }
    private bool TryApply(ClientSnapshotTransaction.Attempt attempt, ClientTradeState.Scope owner, Action action)
    {
        bool applied = false;
        transactions.TryApply(attempt, () => applied = state.TryApply(owner, action));
        return applied;
    }
    private bool Failed<T>(ClashOfRimClientNetworkResult<T> result, ModProtocolResponseDto? protocol, string failed, string rejected)
    {
        if (!result.Success || result.Response is null)
            state.Message = presentation.Text(failed, ("CODE", result.ErrorCode), ("MESSAGE", result.Message));
        else if (protocol is { Accepted: false })
            state.Message = presentation.Text(rejected, ("CODE", protocol.ErrorCode), ("MESSAGE", protocol.Message));
        else return false;
        return true;
    }
    private void Exception(Exception exception, string key)
    {
        state.Message = presentation.Text(key, ("TYPE", exception.GetType().Name), ("MESSAGE", exception.Message));
        presentation.Diagnose(exception);
    }
}
