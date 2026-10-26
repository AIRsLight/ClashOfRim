using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

internal enum TradePanelCommand { Accept, CancelOrder, CancelMemo, DropPod, Claim }
internal sealed record TradePanelActions(bool Accept, bool CancelOrder, bool CancelMemo, bool ShowDropPod, bool DropPod, bool Claim = false);

internal sealed class ClientTradePanel
{
    private readonly ClientTradeState state;
    private readonly ClientTradeMarket market;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly IClientTradeInteraction interaction;
    private readonly ClientGameRequestTracker lifetime;
    private readonly PanelRefreshSchedule refresh = new();
    private string scope = "Open";
    private string? selected;
    private int cachedVersion = -1;
    private IReadOnlyList<ModTradeOrderSummaryDto> cached = Array.Empty<ModTradeOrderSummaryDto>();

    internal ClientTradePanel(ClientSessionLifecycle session, IClientSnapshotGame game, ClientTradeState state,
        ClientTradeMarket market, ClientSynchronizationCoordinator synchronization, IClientTradeInteraction interaction)
    {
        this.state = state; this.market = market; this.synchronization = synchronization; this.interaction = interaction;
        lifetime = new(session, game, () =>
        { scope = "Open"; selected = null; cachedVersion = -1; cached = Array.Empty<ModTradeOrderSummaryDto>(); refresh.Request(); });
    }
    internal object Ownership => lifetime.Capture();
    internal string Scope => lifetime.Read(() => scope);
    internal string Status => state.Message;
    internal bool Enabled => state.Enabled;
    internal bool HasMore => lifetime.Read(() => state.SelectedScope == scope && state.HasMore);
    internal bool Loading => lifetime.Read(() => state.SelectedScope == scope && state.Loading);
    internal string? SelectedId => lifetime.Read(() => { ReadOrders(); return selected; });
    internal ModTradeOrderSummaryDto? SelectedOrder => lifetime.Read(() => ReadOrders().FirstOrDefault(order => order.EventId == selected));
    internal void Close() => lifetime.Reset();
    internal void RequestRefresh() => lifetime.Read(() => { refresh.Request(); return true; });
    internal Task RefreshAsync(float now, bool manual = false) => lifetime.Read(() =>
    {
        if (!refresh.TryStart(now, !manual && synchronization.ManualInProgress, manual)) return Task.CompletedTask;
        return market.RefreshAsync(scope);
    });
    internal Task SelectScopeAsync(string requested, float now)
    {
        using var publication = ClientSessionWork.EnterPublication();
        // Choosing a filter starts a new page view, not a new session. Old
        // confirmations must not revive if the user later returns to this filter.
        lifetime.Reset();
        return lifetime.Read(() => { scope = requested; refresh.Started(now); return market.RefreshAsync(scope); });
    }
    internal Task LoadMoreAsync() => lifetime.Read(() => market.RefreshAsync(scope, reset: false));
    internal bool ShouldLoadMore(float viewportHeight, float contentHeight, float scrollY) =>
        HasMore && !Loading && contentHeight > viewportHeight && scrollY >= contentHeight - viewportHeight - 80f;
    internal void SelectOrder(string eventId) => lifetime.Read(() => selected = eventId);

    internal IReadOnlyList<ModTradeOrderSummaryDto> ReadOrders() => lifetime.Read(() =>
    {
        int version = state.Version;
        if (cachedVersion != version)
        {
            // The two window hosts share one market query, but never label the
            // other window's filter results as their own.
            cached = state.SelectedScope == scope ? state.CopyOrders().AsReadOnly() : Array.Empty<ModTradeOrderSummaryDto>();
            cachedVersion = version;
            // Reconcile once per update, not once for every row drawn.
            if (cached.Count > 0 && !cached.Any(order => order.EventId == selected)) selected = cached[0].EventId;
        }
        if (cached.Count == 0 && !state.Loading) selected = null;
        return cached;
    });

    internal TradePanelActions ActionsFor(ModTradeOrderSummaryDto order) => lifetime.Read(() =>
    {
        var context = lifetime.Capture().Context;
        bool act = ClientTradeRules.CanAct(order, context);
        bool dropPod = act && order.AllowServerDropPod;
        return new TradePanelActions(act && !order.ViewerHasAccepted && scope == "Open",
            scope == "Mine" && ClientTradeRules.IsOwnOpen(order, context), ClientTradeRules.CanCancelMemo(order, context),
            dropPod, dropPod && order.ServerDropPodPostage?.Reachable == true && order.ServerDropPodPostage.PostageSilver.HasValue,
            scope == "Mine" && ClientTradeRules.CanClaim(order, context));
    });

    internal void OpenCreate() => interaction.OpenCreate();

    internal Action Prepare(TradePanelCommand command, ModTradeOrderSummaryDto order)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var owner = lifetime.Capture();
        var captured = ClientProtocolJson.Copy(order);
        Action Guard(Action action)
        {
            var bound = market.Bind(action);
            return () => lifetime.TryApply(owner, bound);
        }
        Action execute = Guard(() =>
        {
            switch (command)
            {
                case TradePanelCommand.Accept: _ = market.AcceptAsync(captured, postage: false); break;
                case TradePanelCommand.CancelOrder: _ = market.CancelAsync(captured, acceptedMemo: false); break;
                case TradePanelCommand.CancelMemo: _ = market.CancelAsync(captured, acceptedMemo: true); break;
                case TradePanelCommand.DropPod: interaction.FulfillByDropPod(captured); break;
                case TradePanelCommand.Claim: _ = market.ClaimAsync(captured); break;
            }
        });
        return command is TradePanelCommand.Accept or TradePanelCommand.DropPod
            ? Guard(() => interaction.ConfirmGoods(captured, execute)) : execute;
    }
}
