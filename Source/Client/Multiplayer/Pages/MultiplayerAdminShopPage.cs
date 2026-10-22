using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using UnityEngine;
using Verse;
using static AIRsLight.ClashOfRim.Multiplayer.Pages.MultiplayerAdminLayout;

namespace AIRsLight.ClashOfRim.Multiplayer.Pages;

internal sealed class MultiplayerAdminShopPage
{
    private readonly ClientAdminShopPanel model;
    private Vector2 scroll;
    private object? owner;
    internal MultiplayerAdminShopPage(ClientAdminShopPanel model) => this.model = model;
    internal void Close() { model.Close(); scroll = default; owner = null; }

    internal void Draw(Rect rect)
    {
        var current = model.Ownership;
        if (!ReferenceEquals(owner, current)) { owner = current; scroll = default; }
        var listings = model.Read();
        Rect view = BeginAdminScroll(rect, ref scroll, Math.Max(rect.height, 80f + listings.Count * 74f));
        float y = 0f;
        DrawAdminSectionTitle(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.SectionShop"));
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(0f, y, view.width - 260f, 20f), model.Status);
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new Rect(view.width - 250f, y, 110f, 28f), ClashOfRimText.Key("ClashOfRim.Refresh"), active: model.CanRefresh))
        {
            _ = model.RefreshAsync(Time.realtimeSinceStartup);
        }

        if (Widgets.ButtonText(new Rect(view.width - 130f, y, 120f, 28f), ClashOfRimText.Key("ClashOfRim.Shop.AddListing"), active: model.CanChange()))
        {
            Find.WindowStack.Add(new ServerShopListingDialogWindow(model.PrepareEdit()));
        }

        y += 38f;
        if (listings.Count == 0)
        {
            Widgets.Label(new Rect(0f, y, view.width, 30f), ClashOfRimText.Key("ClashOfRim.Shop.NoListings"));
            Widgets.EndScrollView();
            return;
        }

        foreach (var draft in listings)
        {
            DrawRow(new Rect(0f, y, view.width, 68f), draft);
            y += 74f;
        }

        Widgets.EndScrollView();
    }

    private void DrawRow(Rect row, ClientAdminShopPanel.Row draft)
    {
        var listing = draft.Listing;
        Widgets.DrawMenuSection(row);
        Rect inner = row.ContractedBy(5f);
        ModThingReferenceDto? item = listing.Item;
        bool buyFromPlayer = ClientShopRules.IsBuyOrder(listing);
        Rect iconRect = new(inner.x, inner.y + 3f, 32f, 32f);
        if (item is not null)
        {
            TradeUiUtility.DrawThingIconWithInfo(iconRect, item);
        }

        string label = item is null
            ? ClashOfRimText.Key("ClashOfRim.UnknownThing")
            : TradeUiUtility.ThingLabel(
                item,
                asRequirement: buyFromPlayer,
                qualityRequirementMode: buyFromPlayer ? listing.QualityRequirementMode : null,
                hitPointsRequirementMode: buyFromPlayer ? listing.HitPointsRequirementMode : null);
        string kindLabel = ClashOfRimText.Key(buyFromPlayer ? "ClashOfRim.Shop.KindBuy" : "ClashOfRim.Shop.KindSell");
        TradeUiUtility.DrawTruncatedLabel(new Rect(iconRect.xMax + 8f, inner.y + 7f, inner.width - 520f, 24f), kindLabel + " - " + label);
        TradeUiUtility.DrawTruncatedLabel(new Rect(iconRect.xMax + 8f, inner.y + 31f, inner.width - 50f, 24f),
            ClashOfRimText.Key(buyFromPlayer ? "ClashOfRim.Shop.Payout" : "ClashOfRim.Shop.Price", (draft.LotPrice?.ToString() ?? "—").Named("PRICE")));

        Widgets.Label(new Rect(inner.xMax - 510f, inner.y + 7f, 42f, 24f), ClashOfRimText.Key(buyFromPlayer ? "ClashOfRim.Shop.DemandShort" : "ClashOfRim.Shop.StockShort"));
        draft.StockText = Widgets.TextField(new Rect(inner.xMax - 468f, inner.y + 5f, 70f, 26f), draft.StockText);
        Widgets.Label(new Rect(inner.xMax - 388f, inner.y + 7f, 42f, 24f), ClashOfRimText.Key(buyFromPlayer ? "ClashOfRim.Shop.PayoutShort" : "ClashOfRim.Shop.PriceShort"));
        draft.UnitPriceText = Widgets.TextField(new Rect(inner.xMax - 346f, inner.y + 5f, 70f, 26f), draft.UnitPriceText);
        TooltipHandler.TipRegion(new Rect(inner.xMax - 388f, inner.y + 5f, 112f, 26f), ClashOfRimText.Key("ClashOfRim.Shop.LotPriceDesc"));

        if (Widgets.ButtonText(new Rect(inner.xMax - 266f, inner.y + 5f, 78f, 28f), ClashOfRimText.Key("ClashOfRim.Admin.Save"), active: model.CanSave(draft)))
        {
            _ = model.SaveAsync(draft);
        }

        if (Widgets.ButtonText(new Rect(inner.xMax - 182f, inner.y + 5f, 78f, 28f), ClashOfRimText.Key("ClashOfRim.Edit"), active: item is not null && model.CanChange(draft)))
        {
            Find.WindowStack.Add(new ServerShopListingDialogWindow(model.PrepareEdit(draft)));
        }

        if (Widgets.ButtonText(new Rect(inner.xMax - 98f, inner.y + 5f, 88f, 28f), ClashOfRimText.Key("ClashOfRim.Admin.Remove"), active: model.CanChange(draft)))
        {
            var remove = model.PrepareRemove(draft);
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                ClashOfRimText.Key("ClashOfRim.Shop.ConfirmRemove", label.Named("THING")),
                () => _ = remove()));
        }
    }

}
