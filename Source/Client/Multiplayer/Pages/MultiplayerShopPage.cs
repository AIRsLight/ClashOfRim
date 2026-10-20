using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Trades;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer.Pages;

internal sealed class MultiplayerShopPage
{
    private readonly ClientShopPanel panel;
    private Vector2 serverShopScrollPosition;
    private object? owner;
    internal MultiplayerShopPage(ClientShopPanel panel) => this.panel = panel;
    internal void Tick(float now) => _ = panel.RefreshAsync(now);
    internal void Close()
    {
        panel.Close(); owner = null; serverShopScrollPosition = Vector2.zero;
    }
    private void EnsureCurrent()
    {
        var current = panel.Ownership;
        if (ReferenceEquals(owner, current)) return;
        owner = current; serverShopScrollPosition = Vector2.zero;
    }

    internal void Draw(Rect rect)
    {
        EnsureCurrent();
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(10f);
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width - 132f, 30f), ClashOfRimText.Key("ClashOfRim.Multiplayer.TabShop"));
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new Rect(inner.xMax - 120f, inner.y, 120f, 30f), ClashOfRimText.Key("ClashOfRim.Refresh"), active: panel.CanRefresh))
        {
            _ = panel.RefreshAsync(Time.realtimeSinceStartup, manual: true);
        }

        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(inner.x, inner.y + 32f, inner.width, 20f), panel.Status);
        Text.Font = GameFont.Small;

        var view = panel.Read();
        DrawServerShopModeTabs(new Rect(inner.x, inner.y + 56f, 300f, 30f), view);
        var visibleListings = panel.Mode == ServerShopListingKinds.BuyFromPlayer ? view.BuyFromPlayer : view.SellToPlayer;

        Rect outRect = new(inner.x, inner.y + 96f, inner.width, inner.height - 96f);
        const float tileWidth = 156f;
        const float tileHeight = 180f;
        const float gap = 10f;
        int columns = Math.Max(1, Mathf.FloorToInt((outRect.width - 16f + gap) / (tileWidth + gap)));
        int rows = Math.Max(1, Mathf.CeilToInt(visibleListings.Count / (float)columns));
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, rows * (tileHeight + gap)));
        Widgets.BeginScrollView(outRect, ref serverShopScrollPosition, viewRect);
        if (visibleListings.Count == 0)
        {
            Widgets.Label(new Rect(0f, 0f, viewRect.width, 40f), ClashOfRimText.Key("ClashOfRim.Shop.NoListings"));
        }
        else
        {
            for (int index = 0; index < visibleListings.Count; index++)
            {
                int column = index % columns;
                int row = index / columns;
                Rect tile = new(
                    column * (tileWidth + gap),
                    row * (tileHeight + gap),
                    tileWidth,
                    tileHeight);
                DrawServerShopTile(tile, visibleListings[index]);
            }
        }

        Widgets.EndScrollView();
    }

    private void DrawServerShopModeTabs(Rect rect, ClientShopPanelView view)
    {
        float halfWidth = (rect.width - 8f) * 0.5f;
        DrawServerShopModeButton(
            new Rect(rect.x, rect.y, halfWidth, rect.height),
            ServerShopListingKinds.SellToPlayer,
            ClashOfRimText.Key("ClashOfRim.Shop.Buy") + " (" + view.SellToPlayer.Count + ")");
        DrawServerShopModeButton(
            new Rect(rect.x + halfWidth + 8f, rect.y, halfWidth, rect.height),
            ServerShopListingKinds.BuyFromPlayer,
            ClashOfRimText.Key("ClashOfRim.Shop.Sell") + " (" + view.BuyFromPlayer.Count + ")");
    }

    private void DrawServerShopModeButton(Rect rect, string listingKind, string label)
    {
        bool selected = string.Equals(panel.Mode, listingKind, StringComparison.Ordinal);
        if (selected)
        {
            Widgets.DrawHighlightSelected(rect);
        }

        if (Widgets.ButtonText(rect, label, active: !selected))
        {
            if (panel.SelectMode(listingKind)) serverShopScrollPosition = Vector2.zero;
        }
    }

    private void DrawServerShopTile(Rect rect, ModServerShopListingDto listing)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        ModThingReferenceDto? item = listing.Item;
        var quote = panel.Quote(listing);
        bool buyFromPlayer = quote.BuyFromPlayer;
        Rect iconRect = new(inner.x, inner.y, 42f, 42f);
        if (item is not null)
        {
            TradeUiUtility.DrawThingIconWithInfo(iconRect, item);
            TooltipHandler.TipRegion(iconRect, TradeUiUtility.ThingLabel(
                item,
                asRequirement: buyFromPlayer,
                qualityRequirementMode: buyFromPlayer ? listing.QualityRequirementMode : null,
                hitPointsRequirementMode: buyFromPlayer ? listing.HitPointsRequirementMode : null));
        }

        Text.Font = GameFont.Tiny;
        ClashOfRimUiUtility.DrawTruncatedLabel(new Rect(iconRect.xMax + 8f, inner.y + 12f, inner.width - 50f, 20f), ClashOfRimText.Key(
            buyFromPlayer ? "ClashOfRim.Shop.Payout" : "ClashOfRim.Shop.Price",
            FormatServerShopSilver(listing.PriceSilver).Named("PRICE")));
        Text.Font = GameFont.Small;

        string label = item is null
            ? ClashOfRimText.Key("ClashOfRim.UnknownThing")
            : TradeUiUtility.ThingLabel(
                item,
                asRequirement: buyFromPlayer,
                qualityRequirementMode: buyFromPlayer ? listing.QualityRequirementMode : null,
                hitPointsRequirementMode: buyFromPlayer ? listing.HitPointsRequirementMode : null);
        TradeUiUtility.DrawTruncatedLabel(new Rect(inner.x, inner.y + 50f, inner.width, 24f), label);

        int totalPrice = quote.TotalPrice;
        Text.Font = GameFont.Tiny;
        ClashOfRimUiUtility.DrawTruncatedLabel(new Rect(inner.x, inner.y + 74f, inner.width, 20f), ClashOfRimText.Key(
            buyFromPlayer ? "ClashOfRim.Shop.Demand" : "ClashOfRim.Shop.Stock",
            FormatServerShopStock(listing.StockCount).Named("COUNT")));
        ClashOfRimUiUtility.DrawTruncatedLabel(new Rect(inner.x, inner.y + 98f, inner.width, 20f), quote.Available
            ? ClashOfRimText.Key(buyFromPlayer ? "ClashOfRim.Shop.TotalPayout" : "ClashOfRim.Shop.TotalPrice", FormatServerShopSilver(totalPrice).Named("PRICE"))
            : ClashOfRimText.Key(buyFromPlayer ? "ClashOfRim.Shop.NoDemand" : "ClashOfRim.Shop.OutOfStock"));
        Text.Font = GameFont.Small;

        string actionLabel = ClashOfRimText.Key(buyFromPlayer ? "ClashOfRim.Shop.SellBatch" : "ClashOfRim.Shop.BuyOne");
        if (Widgets.ButtonText(new Rect(inner.x, inner.yMax - 30f, inner.width, 30f), actionLabel, active: quote.CanPurchase))
        {
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                ClashOfRimText.Key(
                    buyFromPlayer ? "ClashOfRim.Shop.ConfirmSell" : "ClashOfRim.Shop.ConfirmBuy",
                    quote.Count.Named("COUNT"),
                    label.Named("THING"),
                    totalPrice.Named("PRICE")),
                panel.PreparePurchase(listing)));
        }
    }

    private static string FormatServerShopStock(int stockCount)
    {
        return ColoredText(stockCount.ToString(), stockCount <= 0 ? "#ff6b6b" : "#d7dadb");
    }

    private static string FormatServerShopSilver(int silver)
    {
        return ColoredText(Math.Max(0, silver).ToString(), silver <= 0 ? "#ff6b6b" : "#f1d07a");
    }

    private static string ColoredText(string text, string color)
    {
        return "<color=" + color + ">" + text + "</color>";
    }

}
