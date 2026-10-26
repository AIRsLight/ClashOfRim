using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Trades;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer.Pages;

internal sealed class MultiplayerTradePage
{
    private const float TradeOrderRowHeight = 96f;
    private readonly ClientTradePanel panel;
    private Vector2 tradeOrderScrollPosition;
    private Vector2 tradeDetailScrollPosition;
    private object? owner;
    private string? selected;

    internal MultiplayerTradePage(ClientTradePanel panel) => this.panel = panel;
    internal void Tick(float now) => _ = panel.RefreshAsync(now);
    internal void Refresh() => _ = panel.RefreshAsync(Time.realtimeSinceStartup, manual: true);
    internal void RequestRefresh() => panel.RequestRefresh();
    internal void Close()
    {
        panel.Close(); owner = null; selected = null;
        tradeOrderScrollPosition = tradeDetailScrollPosition = Vector2.zero;
    }
    private void EnsureCurrent()
    {
        var current = panel.Ownership;
        if (!ReferenceEquals(owner, current))
        {
            owner = current; selected = null;
            tradeOrderScrollPosition = tradeDetailScrollPosition = Vector2.zero;
        }
        var currentSelected = panel.SelectedId;
        if (selected != currentSelected) tradeDetailScrollPosition = Vector2.zero;
        selected = currentSelected;
    }

    internal void Draw(Rect rect, bool showCreate = true)
    {
        EnsureCurrent();
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(10f);
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width - 150f, 30f), ClashOfRimText.Key("ClashOfRim.Trade.MarketTitle"));
        Text.Font = GameFont.Small;
        string actionLabel = ClashOfRimText.Key(showCreate ? "ClashOfRim.Trade.CreateOrderTitle" : "ClashOfRim.Refresh");
        if (Widgets.ButtonText(new Rect(inner.xMax - 140f, inner.y, 140f, 30f), actionLabel, active: !showCreate || panel.Enabled))
        {
            if (showCreate) panel.OpenCreate();
            else Refresh();
        }

        DrawTradeScopeTabs(new Rect(inner.x, inner.y + 38f, inner.width, 30f));

        Rect listRect = new(inner.x, inner.y + 78f, Mathf.Min(430f, inner.width * 0.46f), inner.height - 78f);
        Rect detailRect = new(listRect.xMax + 10f, listRect.y, inner.xMax - listRect.xMax - 10f, listRect.height);
        IReadOnlyList<ModTradeOrderSummaryDto> orders = panel.ReadOrders();
        DrawTradeOrderList(listRect, orders);
        ModTradeOrderSummaryDto? selected = panel.SelectedOrder;
        DrawTradeOrderDetail(detailRect, selected);
    }

    private void DrawTradeScopeTabs(Rect rect)
    {
        const float width = 86f;
        DrawTradeScopeButton(new Rect(rect.x, rect.y, width, rect.height), "Open", ClashOfRimText.Key("ClashOfRim.Trade.ScopeOpen"));
        DrawTradeScopeButton(new Rect(rect.x + 92f, rect.y, width, rect.height), "AcceptedByMe", ClashOfRimText.Key("ClashOfRim.Trade.ScopeAcceptedByMe"));
        DrawTradeScopeButton(new Rect(rect.x + 184f, rect.y, width, rect.height), "Mine", ClashOfRimText.Key("ClashOfRim.Trade.ScopeMine"));
        DrawTradeScopeButton(new Rect(rect.x + 276f, rect.y, width, rect.height), "History", ClashOfRimText.Key("ClashOfRim.Trade.ScopeHistory"));
    }

    private void DrawTradeScopeButton(Rect rect, string scope, string label)
    {
        if (string.Equals(panel.Scope, scope, StringComparison.Ordinal))
        {
            Widgets.DrawHighlightSelected(rect);
        }

        if (Widgets.ButtonText(rect, label))
        {
            _ = panel.SelectScopeAsync(scope, Time.realtimeSinceStartup);
        }
    }

    private void DrawTradeOrderList(Rect rect, IReadOnlyList<ModTradeOrderSummaryDto> orders)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), TradeScopeTitle());
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(inner.x, inner.y + 24f, inner.width, 20f), panel.Status);
        Text.Font = GameFont.Small;

        Rect outRect = new(inner.x, inner.y + 50f, inner.width, inner.height - 50f);
        float contentHeight = orders.Count * TradeOrderRowHeight + (panel.HasMore || panel.Loading ? 40f : 0f);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, contentHeight));
        Widgets.BeginScrollView(outRect, ref tradeOrderScrollPosition, viewRect);
        if (orders.Count == 0)
        {
            Widgets.Label(new Rect(0f, 0f, viewRect.width, 40f), ClashOfRimText.Key("ClashOfRim.Trade.NoOrdersInScope"));
        }
        else
        {
            for (int index = 0; index < orders.Count; index++)
            {
                DrawTradeOrderSummaryRow(new Rect(0f, index * TradeOrderRowHeight, viewRect.width, TradeOrderRowHeight - 4f), orders[index]);
            }
        }

        DrawTradePagingControls(new Rect(0f, orders.Count * TradeOrderRowHeight + 4f, viewRect.width, 32f));
        Widgets.EndScrollView();

        if (panel.ShouldLoadMore(outRect.height, viewRect.height, tradeOrderScrollPosition.y))
        {
            _ = panel.LoadMoreAsync();
        }
    }

    private void DrawTradePagingControls(Rect rect)
    {
        if (panel.Loading)
        {
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(rect, ClashOfRimText.Key("ClashOfRim.Trade.LoadingMore"));
            Text.Anchor = TextAnchor.UpperLeft;
            return;
        }

        if (!panel.HasMore)
        {
            return;
        }

        if (Widgets.ButtonText(rect, ClashOfRimText.Key("ClashOfRim.Trade.LoadMore")))
        {
            _ = panel.LoadMoreAsync();
        }
    }

    private void DrawTradeOrderSummaryRow(Rect row, ModTradeOrderSummaryDto order)
    {
        bool selected = string.Equals(order.EventId, panel.SelectedId, StringComparison.Ordinal);
        if (selected)
        {
            Widgets.DrawHighlightSelected(row);
        }
        else
        {
            Widgets.DrawHighlightIfMouseover(row);
        }

        bool openedInfo = DrawThingStrip(new Rect(row.x + 6f, row.y + 5f, 112f, 30f), order.OfferedThings, asRequirement: false);
        Text.Anchor = TextAnchor.MiddleCenter;
        Widgets.Label(new Rect(row.x + 122f, row.y + 5f, 24f, 30f), ClashOfRimText.Key("ClashOfRim.Trade.ExchangeVerb"));
        Text.Anchor = TextAnchor.UpperLeft;
        openedInfo |= DrawThingStrip(new Rect(row.x + 150f, row.y + 5f, 112f, 30f), order.RequestedThings, asRequirement: true);

        string memo = order.AcceptedMemoCount > 0
            ? ClashOfRimText.Key("ClashOfRim.Trade.AcceptedMemoSuffix", order.AcceptedMemoCount.Named("COUNT"))
            : string.Empty;
        string accepted = order.ViewerHasAccepted ? ClashOfRimText.Key("ClashOfRim.Trade.ViewerAcceptedSuffix") : string.Empty;
        Widgets.Label(new Rect(row.x + 6f, row.y + 40f, row.width - 12f, 22f), TradePartiesLine(order, includeColony: false) + memo + accepted);
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(row.x + 6f, row.y + 63f, row.width - 12f, 18f), ClashOfRimText.Key(
            "ClashOfRim.Trade.StatusLine",
            TradeUiUtility.FormatOrderStatus(order.Status).Named("STATUS")));
        Text.Font = GameFont.Small;

        if (!openedInfo && Widgets.ButtonInvisible(row))
        {
            panel.SelectOrder(order.EventId);
            tradeDetailScrollPosition = Vector2.zero;
        }
    }

    private void DrawTradeOrderDetail(Rect rect, ModTradeOrderSummaryDto? order)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        if (order is null)
        {
            Widgets.Label(inner, ClashOfRimText.Key("ClashOfRim.Trade.SelectOrderForDetails"));
            return;
        }

        float headerY = inner.y;
        Widgets.Label(new Rect(inner.x, headerY, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Trade.OwnerLine", FormatTradeParty(order.Owner, includeColony: true).Named("OWNER")));
        headerY += 24f;
        if (HasTradeCounterparty(order))
        {
            Widgets.Label(new Rect(inner.x, headerY, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Trade.CounterpartyLine", FormatTradeParty(order.Counterparty, includeColony: true).Named("COUNTERPARTY")));
            headerY += 24f;
        }

        Widgets.Label(new Rect(inner.x, headerY, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Trade.StatusLine", TradeUiUtility.FormatOrderStatus(order.Status).Named("STATUS")));
        headerY += 24f;
        Widgets.Label(new Rect(inner.x, headerY, inner.width, 24f), ClashOfRimText.Key(
            "ClashOfRim.Trade.FeeAndMemoLine",
            order.FeeSilver.Named("FEE"),
            order.AcceptedMemoCount.Named("COUNT")));
        headerY += 24f;
        string deliveryLine = order.Status is "Traded" or "Collecting" ? TradeUiUtility.FormatCollectionDeadline(order)
            : ClashOfRimText.Key("ClashOfRim.Trade.DropPodPostageLine", TradeUiUtility.FormatPostage(order.ServerDropPodPostage).Named("POSTAGE"));
        float deliveryHeight = Math.Max(24f, Text.CalcHeight(deliveryLine, inner.width));
        Widgets.Label(new Rect(inner.x, headerY, inner.width, deliveryHeight), deliveryLine);
        headerY += deliveryHeight + 6f;

        Rect contentRect = new(inner.x, headerY, inner.width, inner.yMax - headerY - 54f);
        Rect viewRect = new(0f, 0f, contentRect.width - 16f, Math.Max(contentRect.height, TradeDetailHeight(order)));
        Widgets.BeginScrollView(contentRect, ref tradeDetailScrollPosition, viewRect);
        float y = 0f;
        y = DrawThingSection(new Rect(0f, y, viewRect.width, 24f), ClashOfRimText.Key("ClashOfRim.Trade.OtherOffers"), order.OfferedThings, asRequirement: false);
        DrawThingSection(new Rect(0f, y + 8f, viewRect.width, 24f), ClashOfRimText.Key("ClashOfRim.Trade.OtherRequests"), order.RequestedThings, asRequirement: true);
        Widgets.EndScrollView();

        Rect acceptRect = new(inner.xMax - 132f, inner.yMax - 42f, 132f, 34f);
        Rect dropPodRect = new(acceptRect.x - 142f, acceptRect.y, 132f, 34f);
        var available = panel.ActionsFor(order);
        bool canCancel = available.CancelOrder;
        bool canAccept = available.Accept;
        bool canCancelAcceptedMemo = available.CancelMemo;
        if (available.ShowDropPod)
        {
            bool canDropPod = available.DropPod;
            if (canDropPod && Widgets.ButtonText(dropPodRect, ClashOfRimText.Key("ClashOfRim.Trade.SendByDropPod")))
            {
                string summary = ClashOfRimText.Key("ClashOfRim.Trade.ConfirmDropPodFulfillTitle")
                    + "\n\n"
                    + ClashOfRimText.Key("ClashOfRim.Trade.DeliverLine", TradeUiUtility.FormatThingList(order.RequestedThings, asRequirement: true).Named("THINGS"))
                    + "\n"
                    + ClashOfRimText.Key("ClashOfRim.Trade.ReceiveLine", TradeUiUtility.FormatThingList(order.OfferedThings, asRequirement: false).Named("THINGS"))
                    + "\n"
                    + ClashOfRimText.Key("ClashOfRim.Trade.DropPodPostageLine", TradeUiUtility.FormatPostage(order.ServerDropPodPostage).Named("POSTAGE"));
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    summary,
                    panel.Prepare(TradePanelCommand.DropPod, order)));
            }
            else if (!canDropPod)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(dropPodRect, ClashOfRimText.Key("ClashOfRim.Trade.DropPodUnreachableShort"));
                Text.Anchor = TextAnchor.UpperLeft;
            }
        }

        if (available.Claim)
        {
            TooltipHandler.TipRegion(acceptRect, ClashOfRimText.Key("ClashOfRim.Trade.ClaimDescription"));
            if (Widgets.ButtonText(acceptRect, ClashOfRimText.Key("ClashOfRim.Trade.ClaimItems")))
                panel.Prepare(TradePanelCommand.Claim, order)();
        }
        else if (canAccept && Widgets.ButtonText(acceptRect, ClashOfRimText.Key("ClashOfRim.Trade.AddToAcceptedList")))
        {
            panel.Prepare(TradePanelCommand.Accept, order)();
        }
        else if (canCancelAcceptedMemo)
        {
            if (Widgets.ButtonText(acceptRect, ClashOfRimText.Key("ClashOfRim.Trade.CancelAcceptedMemo")))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    ClashOfRimText.Key("ClashOfRim.Trade.ConfirmCancelAcceptedMemo"),
                    panel.Prepare(TradePanelCommand.CancelMemo, order)));
            }
        }
        else if (canCancel)
        {
            if (Widgets.ButtonText(acceptRect, ClashOfRimText.Key("ClashOfRim.Trade.CancelOrder")))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    ClashOfRimText.Key("ClashOfRim.Trade.ConfirmCancelOrder"),
                    panel.Prepare(TradePanelCommand.CancelOrder, order)));
            }
        }
        else if (!canAccept)
        {
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(inner.x, inner.yMax - 40f, inner.width, 30f), order.ViewerHasAccepted ? ClashOfRimText.Key("ClashOfRim.Trade.Accepted") : ClashOfRimText.Key("ClashOfRim.Trade.ViewOnly"));
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }

    private string TradeScopeTitle()
    {
        return panel.Scope switch
        {
            "AcceptedByMe" => ClashOfRimText.Key("ClashOfRim.Trade.ScopeTitleAcceptedByMe"),
            "Mine" => ClashOfRimText.Key("ClashOfRim.Trade.ScopeTitleMine"),
            "History" => ClashOfRimText.Key("ClashOfRim.Trade.ScopeTitleHistory"),
            _ => ClashOfRimText.Key("ClashOfRim.Trade.ScopeTitleOpen")
        };
    }

    private static float TradeDetailHeight(ModTradeOrderSummaryDto order)
    {
        int offeredRows = TradeUiUtility.BuildDisplayGroups(order.OfferedThings).Count;
        int requestedRows = TradeUiUtility.BuildDisplayGroups(order.RequestedThings).Count;
        return 56f
            + Math.Max(1, offeredRows) * TradeUiUtility.RowHeight
            + Math.Max(1, requestedRows) * TradeUiUtility.RowHeight;
    }

    private static string TradePartiesLine(ModTradeOrderSummaryDto order, bool includeColony)
    {
        string owner = FormatTradeParty(order.Owner, includeColony);
        return HasTradeCounterparty(order)
            ? ClashOfRimText.Key(
                "ClashOfRim.Trade.PartiesLine",
                owner.Named("OWNER"),
                FormatTradeParty(order.Counterparty, includeColony).Named("COUNTERPARTY"))
            : owner;
    }

    private static bool HasTradeCounterparty(ModTradeOrderSummaryDto order)
    {
        return !string.IsNullOrWhiteSpace(order.Counterparty?.UserId);
    }

    private static string FormatTradeParty(ModProtocolIdentityDto? identity, bool includeColony)
    {
        string? userId = identity?.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return ClashOfRimText.Key("ClashOfRim.UnknownPlayer");
        }

        string? colonyId = identity?.ColonyId;
        return includeColony && !string.IsNullOrWhiteSpace(colonyId)
            ? userId! + "/" + colonyId
            : userId!;
    }

    private static float DrawThingSection(
        Rect headerRect,
        string title,
        IReadOnlyList<ModThingReferenceDto> things,
        bool asRequirement)
    {
        Widgets.Label(headerRect, title);
        float y = headerRect.yMax + 4f;
        if (things.Count == 0)
        {
            Widgets.Label(new Rect(headerRect.x, y, headerRect.width, 28f), ClashOfRimText.Key("ClashOfRim.None"));
            return y + 32f;
        }

        foreach (TradeUiUtility.TradeThingDisplayGroup group in TradeUiUtility.BuildDisplayGroups(things))
        {
            Rect row = new(headerRect.x, y, headerRect.width, TradeUiUtility.RowHeight);
            DrawThingReferenceRow(row, group, asRequirement);
            y += TradeUiUtility.RowHeight;
        }

        return y;
    }

    private static void DrawThingReferenceRow(Rect row, TradeUiUtility.TradeThingDisplayGroup group, bool asRequirement)
    {
        Widgets.DrawHighlightIfMouseover(row);
        Rect iconRect = new(row.x + 3f, row.y + 3f, TradeUiUtility.IconSize, TradeUiUtility.IconSize);
        TradeUiUtility.DrawThingDisplayGroupIconWithInfo(iconRect, group);
        Rect labelRect = new(iconRect.xMax + 8f, row.y, row.width - iconRect.width - 12f, row.height);
        TradeUiUtility.DrawTruncatedLabel(labelRect, TradeUiUtility.ThingDisplayGroupLabel(group, asRequirement));
    }

    private static bool DrawThingStrip(Rect rect, IReadOnlyList<ModThingReferenceDto> things, bool asRequirement)
    {
        bool openedInfo = false;
        IReadOnlyList<TradeUiUtility.TradeThingDisplayGroup> groups = TradeUiUtility.BuildDisplayGroups(things);
        int max = Math.Min(3, groups.Count);
        for (int index = 0; index < max; index++)
        {
            Rect iconRect = new(rect.x + index * 34f, rect.y, 30f, 30f);
            TradeUiUtility.TradeThingDisplayGroup group = groups[index];
            openedInfo |= TradeUiUtility.DrawThingDisplayGroupIconWithInfo(iconRect, group);
            TooltipHandler.TipRegion(iconRect, TradeUiUtility.ThingDisplayGroupLabel(group, asRequirement));
        }

        if (groups.Count > max)
        {
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(rect.x + max * 34f, rect.y, 30f, 30f), "+" + (groups.Count - max));
            Text.Anchor = TextAnchor.UpperLeft;
        }

        return openedInfo;
    }

}
