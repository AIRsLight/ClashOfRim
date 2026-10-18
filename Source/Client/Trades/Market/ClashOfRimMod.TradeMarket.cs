using AIRsLight.ClashOfRim.Modules.Trades;
using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Trades;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void StartRefreshTradeOrders(string scope = "Open") => _ = tradeMarket.RefreshAsync(scope);

    internal void StartLoadMoreTradeOrders(string scope = "Open") => _ = tradeMarket.RefreshAsync(scope, reset: false);

    internal void StartCancelTradeOrder(ModTradeOrderSummaryDto order) => _ = tradeMarket.CancelAsync(order, acceptedMemo: false);

    internal void StartCancelAcceptedTradeMemo(ModTradeOrderSummaryDto order) => _ = tradeMarket.CancelAsync(order, acceptedMemo: true);

    internal bool IsOwnOpenTradeOrder(ModTradeOrderSummaryDto order) => ClientTradeRules.IsOwnOpen(order, sessionLifecycle.ReadContext());

    internal bool CanActOnOpenTradeOrder(ModTradeOrderSummaryDto order) => ClientTradeRules.CanAct(order, sessionLifecycle.ReadContext());

    internal bool CanCancelAcceptedTradeMemo(ModTradeOrderSummaryDto order) => ClientTradeRules.CanCancelMemo(order, sessionLifecycle.ReadContext());

    internal Action BindTradeAction(Action action) => tradeMarket.Bind(action);

    internal void OpenTradeOrderDialog() => tradeActions.OpenCreate();

    internal void OpenTradeMarketWindow()
    {
        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(settings.CurrentSnapshotId))
        {
            tradeState.Message = ClashOfRimText.Key("ClashOfRim.Trade.StatusConfigMissingMarket");
            return;
        }

        Find.WindowStack.Add(new TradeMarketWindow(this));
        StartRefreshTradeOrders();
    }
}
