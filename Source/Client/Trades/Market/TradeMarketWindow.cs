using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

public sealed class TradeMarketWindow : Window
{
    private readonly ClashOfRimMod mod;
    private Vector2 orderScrollPosition;
    private Vector2 detailScrollPosition;
    private string? selectedEventId;
    private string selectedScope = "Open";
    private int cachedTradeOrdersVersion = -1;
    private IReadOnlyList<ModTradeOrderSummaryDto> cachedTradeOrders = new List<ModTradeOrderSummaryDto>();

    public TradeMarketWindow(ClashOfRimMod mod)
    {
        this.mod = mod;
        doCloseX = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = false;
        forcePause = false;
        draggable = true;
    }

    public override Vector2 InitialSize => new(940f, 700f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 120f, 32f), ClashOfRimText.Key("ClashOfRim.Trade.MarketTitle"));
        Text.Font = GameFont.Small;

        DrawScopeTabs(new Rect(inRect.x, inRect.y + 38f, inRect.width - 120f, 30f));

        if (Widgets.ButtonText(new Rect(inRect.xMax - 112f, inRect.y, 112f, 30f), ClashOfRimText.Key("ClashOfRim.Refresh")))
        {
            mod.StartRefreshTradeOrders(selectedScope);
        }

        Rect listRect = new(inRect.x, inRect.y + 74f, 420f, inRect.height - 74f);
        Rect detailRect = new(listRect.xMax + 12f, listRect.y, inRect.width - listRect.width - 12f, listRect.height);

        IReadOnlyList<ModTradeOrderSummaryDto> orders = TradeOrders();
        DrawOrderList(listRect, orders);
        DrawOrderDetail(detailRect, orders.FirstOrDefault(order => order.EventId == selectedEventId) ?? orders.FirstOrDefault());
    }

    private IReadOnlyList<ModTradeOrderSummaryDto> TradeOrders()
    {
        int version = mod.TradeOrdersSnapshotVersion;
        if (version != cachedTradeOrdersVersion)
        {
            cachedTradeOrders = mod.TradeOrdersSnapshot;
            cachedTradeOrdersVersion = version;
        }

        return cachedTradeOrders;
    }

    private void DrawScopeTabs(Rect rect)
    {
        DrawScopeButton(new Rect(rect.x, rect.y, 86f, rect.height), "Open", ClashOfRimText.Key("ClashOfRim.Trade.ScopeOpen"));
        DrawScopeButton(new Rect(rect.x + 92f, rect.y, 86f, rect.height), "AcceptedByMe", ClashOfRimText.Key("ClashOfRim.Trade.ScopeAcceptedByMe"));
        DrawScopeButton(new Rect(rect.x + 184f, rect.y, 86f, rect.height), "Mine", ClashOfRimText.Key("ClashOfRim.Trade.ScopeMine"));
        DrawScopeButton(new Rect(rect.x + 276f, rect.y, 86f, rect.height), "History", ClashOfRimText.Key("ClashOfRim.Trade.ScopeHistory"));
    }

    private void DrawScopeButton(Rect rect, string scope, string label)
    {
        if (selectedScope == scope)
        {
            Widgets.DrawHighlightSelected(rect);
        }

        if (Widgets.ButtonText(rect, label))
        {
            selectedScope = scope;
            selectedEventId = null;
            mod.StartRefreshTradeOrders(selectedScope);
        }
    }

    private void DrawOrderList(Rect rect, IReadOnlyList<ModTradeOrderSummaryDto> orders)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), ScopeTitle());
        Widgets.Label(new Rect(inner.x, inner.y + 24f, inner.width, 24f), mod.TradeStatus);

        Rect outRect = new(inner.x, inner.y + 54f, inner.width, inner.height - 54f);
        float contentHeight = orders.Count * 96f + (mod.TradeOrdersHasMore || mod.TradeOrdersPageLoadInProgress ? 42f : 0f);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, contentHeight));
        Widgets.BeginScrollView(outRect, ref orderScrollPosition, viewRect);

        if (orders.Count == 0)
        {
            Widgets.Label(new Rect(0f, 0f, viewRect.width, 60f), ClashOfRimText.Key("ClashOfRim.Trade.NoOrdersInScope"));
        }
        else
        {
            for (int index = 0; index < orders.Count; index++)
            {
                Rect row = new(0f, index * 96f, viewRect.width, 92f);
                DrawOrderSummaryRow(row, orders[index]);
            }
        }

        DrawPagingControls(new Rect(0f, orders.Count * 96f + 4f, viewRect.width, 34f), mod);
        Widgets.EndScrollView();

        if (ShouldAutoLoadMore(outRect, viewRect, orderScrollPosition, mod))
        {
            mod.StartLoadMoreTradeOrders(selectedScope);
        }
    }

    private static bool ShouldAutoLoadMore(Rect outRect, Rect viewRect, Vector2 scrollPosition, ClashOfRimMod mod)
    {
        return mod.TradeOrdersHasMore
            && !mod.TradeOrdersPageLoadInProgress
            && viewRect.height > outRect.height
            && scrollPosition.y >= viewRect.height - outRect.height - 80f;
    }

    private void DrawPagingControls(Rect rect, ClashOfRimMod mod)
    {
        if (mod.TradeOrdersPageLoadInProgress)
        {
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(rect, ClashOfRimText.Key("ClashOfRim.Trade.LoadingMore"));
            Text.Anchor = TextAnchor.UpperLeft;
            return;
        }

        if (!mod.TradeOrdersHasMore)
        {
            return;
        }

        if (Widgets.ButtonText(rect, ClashOfRimText.Key("ClashOfRim.Trade.LoadMore")))
        {
            mod.StartLoadMoreTradeOrders(selectedScope);
        }
    }

    private void DrawOrderSummaryRow(Rect row, ModTradeOrderSummaryDto order)
    {
        bool selected = string.Equals(order.EventId, selectedEventId, StringComparison.Ordinal);
        if (selected)
        {
            Widgets.DrawHighlightSelected(row);
        }
        else
        {
            Widgets.DrawHighlightIfMouseover(row);
        }

        bool openedInfo = DrawThingStrip(new Rect(row.x + 4f, row.y + 4f, 120f, 30f), order.OfferedThings, asRequirement: false);
        Text.Anchor = TextAnchor.MiddleCenter;
        Widgets.Label(new Rect(row.x + 130f, row.y + 4f, 22f, 30f), ClashOfRimText.Key("ClashOfRim.Trade.ExchangeVerb"));
        Text.Anchor = TextAnchor.UpperLeft;
        openedInfo |= DrawThingStrip(new Rect(row.x + 158f, row.y + 4f, 120f, 30f), order.RequestedThings, asRequirement: true);

        string memo = order.AcceptedMemoCount > 0
            ? ClashOfRimText.Key("ClashOfRim.Trade.AcceptedMemoSuffix", order.AcceptedMemoCount.Named("COUNT"))
            : string.Empty;
        string accepted = order.ViewerHasAccepted ? ClashOfRimText.Key("ClashOfRim.Trade.ViewerAcceptedSuffix") : string.Empty;
        Widgets.Label(new Rect(row.x + 4f, row.y + 38f, row.width - 8f, 22f), TradePartiesLine(order, includeColony: false) + memo + accepted);
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(row.x + 4f, row.y + 60f, row.width - 8f, 22f), ClashOfRimText.Key(
            "ClashOfRim.Trade.StatusLine",
            TradeUiUtility.FormatOrderStatus(order.Status).Named("STATUS")));
        Text.Font = GameFont.Small;

        if (!openedInfo && Widgets.ButtonInvisible(row))
        {
            selectedEventId = order.EventId;
        }
    }

    private void DrawOrderDetail(Rect rect, ModTradeOrderSummaryDto? order)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        if (order is null)
        {
            Widgets.Label(inner, ClashOfRimText.Key("ClashOfRim.Trade.SelectOrderForDetails"));
            return;
        }

        if (string.IsNullOrWhiteSpace(selectedEventId))
        {
            selectedEventId = order.EventId;
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
        Widgets.Label(new Rect(inner.x, headerY, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Trade.DropPodPostageLine", TradeUiUtility.FormatPostage(order.ServerDropPodPostage).Named("POSTAGE")));
        headerY += 30f;

        Rect contentRect = new(inner.x, headerY, inner.width, inner.yMax - headerY - 54f);
        Rect viewRect = new(0f, 0f, contentRect.width - 16f, Math.Max(contentRect.height, DetailHeight(order)));
        Widgets.BeginScrollView(contentRect, ref detailScrollPosition, viewRect);
        float y = 0f;
        y = DrawThingSection(new Rect(0f, y, viewRect.width, 24f), ClashOfRimText.Key("ClashOfRim.Trade.OtherOffers"), order.OfferedThings, asRequirement: false);
        y = DrawThingSection(new Rect(0f, y + 8f, viewRect.width, 24f), ClashOfRimText.Key("ClashOfRim.Trade.OtherRequests"), order.RequestedThings, asRequirement: true);
        Widgets.EndScrollView();

        Rect acceptRect = new(inner.xMax - 132f, inner.yMax - 46f, 132f, 34f);
        Rect dropPodRect = new(acceptRect.x - 142f, acceptRect.y, 132f, 34f);
        bool canCancel = string.Equals(selectedScope, "Mine", StringComparison.Ordinal)
            && mod.IsOwnOpenTradeOrder(order);
        bool canAct = mod.CanActOnOpenTradeOrder(order);
        bool canAccept = canAct && !order.ViewerHasAccepted && string.Equals(selectedScope, "Open", StringComparison.Ordinal);
        bool canCancelAcceptedMemo = mod.CanCancelAcceptedTradeMemo(order);
        if (canAct && order.AllowServerDropPod)
        {
            bool canDropPod = order.ServerDropPodPostage?.Reachable == true
                && order.ServerDropPodPostage.PostageSilver.HasValue;
            if (canDropPod)
            {
                if (Widgets.ButtonText(dropPodRect, ClashOfRimText.Key("ClashOfRim.Trade.SendByDropPod")))
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
                        () => mod.ConfirmTradeAcceptorWillReceiveGoods(
                            order,
                            () => mod.StartFulfillTradeOrderByDropPod(order))));
                }
            }
            else
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(dropPodRect, ClashOfRimText.Key("ClashOfRim.Trade.DropPodUnreachableShort"));
                Text.Anchor = TextAnchor.UpperLeft;
            }
        }

        if (canAccept && Widgets.ButtonText(acceptRect, ClashOfRimText.Key("ClashOfRim.Trade.AddToAcceptedList")))
        {
            mod.ConfirmTradeAcceptorWillReceiveGoods(
                order,
                () => mod.StartAcceptTradeOrder(order, postagePaidByAcceptor: false));
        }
        else if (canCancelAcceptedMemo)
        {
            if (Widgets.ButtonText(acceptRect, ClashOfRimText.Key("ClashOfRim.Trade.CancelAcceptedMemo")))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    ClashOfRimText.Key("ClashOfRim.Trade.ConfirmCancelAcceptedMemo"),
                    () => mod.StartCancelAcceptedTradeMemo(order)));
            }
        }
        else if (canCancel)
        {
            if (Widgets.ButtonText(acceptRect, ClashOfRimText.Key("ClashOfRim.Trade.CancelOrder")))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    ClashOfRimText.Key("ClashOfRim.Trade.ConfirmCancelOrder"),
                    () => mod.StartCancelTradeOrder(order)));
            }
        }
        else if (!canAccept)
        {
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(inner.x, inner.yMax - 44f, inner.width, 30f), order.ViewerHasAccepted ? ClashOfRimText.Key("ClashOfRim.Trade.Accepted") : ClashOfRimText.Key("ClashOfRim.Trade.ViewOnly"));
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }

    private static float DetailHeight(ModTradeOrderSummaryDto order)
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

    private string ScopeTitle()
    {
        return selectedScope switch
        {
            "AcceptedByMe" => ClashOfRimText.Key("ClashOfRim.Trade.ScopeTitleAcceptedByMe"),
            "Mine" => ClashOfRimText.Key("ClashOfRim.Trade.ScopeTitleMine"),
            "History" => ClashOfRimText.Key("ClashOfRim.Trade.ScopeTitleHistory"),
            _ => ClashOfRimText.Key("ClashOfRim.Trade.ScopeTitleOpen")
        };
    }

}
