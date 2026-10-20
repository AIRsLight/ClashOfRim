using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.ServerShop;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer;

public sealed class ServerShopListingDialogWindow : Window
{
    private readonly ClientShopListingEditor editor;
    private Vector2 searchScrollPosition;
    private Vector2 editorScrollPosition;
    private float editorContentHeight;
    private float labelWidth;
    private float FieldX => labelWidth + 10f;
    private static readonly string[] FieldLabels = { "Kind", "DemandShort", "StockShort", "ItemCount", "UnitPayout", "UnitPrice",
        "PriceIncreaseRatio", "VanillaMarketValue", "Stuff", "Quality", "QualityRequirement", "HitPoints", "HitPointsRequirement" };
    private string searchText = string.Empty;

    public ServerShopListingDialogWindow(ClashOfRimMod mod, ModServerShopListingDto? listing)
        : this(mod.CreateAdminShopEdit(listing)) { }

    internal ServerShopListingDialogWindow(ClientAdminShopPanel.Edit edit)
    {
        editor = new(edit, new ClientShopListingRuntime());
        doCloseX = true;
        closeOnAccept = false;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = false;
        forcePause = false;
        draggable = true;
    }

    public override void PostClose() { editor.Close(); base.PostClose(); }

    public override Vector2 InitialSize => new(780f, 620f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), ClashOfRimText.Key("ClashOfRim.Shop.EditListing"));
        Text.Font = GameFont.Small;

        Rect left = new(inRect.x, inRect.y + 40f, (inRect.width - 12f) * 0.5f, inRect.height - 92f);
        Rect right = new(left.xMax + 12f, left.y, left.width, left.height);
        DrawSearchPanel(left);
        DrawEditorPanel(right);

        Rect cancelRect = new(inRect.xMax - 252f, inRect.yMax - 36f, 110f, 32f);
        if (Widgets.ButtonText(cancelRect, ClashOfRimText.Key("ClashOfRim.Cancel")))
        {
            Close();
        }

        Rect saveRect = new(inRect.xMax - 132f, inRect.yMax - 36f, 132f, 32f);
        if (Widgets.ButtonText(saveRect, ClashOfRimText.Key("ClashOfRim.Admin.Save"), active: editor.CanSave)
            && editor.Save() is not null) Close();
    }

    private void DrawSearchPanel(Rect rect)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Shop.SelectThing"));
        searchText = Widgets.TextField(new Rect(inner.x, inner.y + 28f, inner.width, 28f), searchText ?? string.Empty);

        var defs = editor.Search(searchText);
        Rect outRect = new(inner.x, inner.y + 62f, inner.width, inner.height - 62f);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, defs.Count * TradeUiUtility.RowHeight));
        Widgets.BeginScrollView(outRect, ref searchScrollPosition, viewRect);
        for (int index = 0; index < defs.Count; index++)
        {
            var def = defs[index];
            Rect row = new(0f, index * TradeUiUtility.RowHeight, viewRect.width, TradeUiUtility.RowHeight);
            DrawThingDefRow(row, def);
        }

        Widgets.EndScrollView();
    }

    private void DrawThingDefRow(Rect row, ClientShopDefinition def)
    {
        Widgets.DrawHighlightIfMouseover(row);
        Rect iconRect = new(row.x + 3f, row.y + 3f, TradeUiUtility.IconSize, TradeUiUtility.IconSize);
        TradeUiUtility.DrawThingIconWithInfo(iconRect, def.DefName);
        string selectLabel = ClashOfRimText.Key("ClashOfRim.Select");
        float buttonWidth = ClashOfRimUiUtility.ButtonWidth(selectLabel);
        Rect button = new(row.xMax - buttonWidth - 4f, row.y + 4f, buttonWidth, 26f);
        TradeUiUtility.DrawTruncatedLabel(new Rect(iconRect.xMax + 8f, row.y + 5f, button.x - iconRect.xMax - 16f, 24f), def.Label + " (" + def.DefName + ")");
        if (Widgets.ButtonText(button, selectLabel))
        {
            editor.Select(def.DefName);
        }
    }

    private void DrawEditorPanel(Rect rect)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        Rect view = new(0f, 0f, inner.width - 16f, Mathf.Max(inner.height, editorContentHeight));
        Widgets.BeginScrollView(inner, ref editorScrollPosition, view);
        DrawEditorContents(view);
        Widgets.EndScrollView();
    }

    private void DrawEditorContents(Rect inner)
    {
        labelWidth = Mathf.Max(120f, FieldLabels.Max(key => Text.CalcSize(ClashOfRimText.Key("ClashOfRim.Shop." + key)).x));
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Shop.ListingDetails"));
        float y = inner.y + 36f;
        DrawListingKindDropdown(new Rect(inner.x, y, inner.width, 30f));
        y += 40f;
        editor.Refresh();
        var selectedItem = editor.Item;
        if (selectedItem is null)
        {
            Widgets.Label(new Rect(inner.x, y, inner.width, 30f), ClashOfRimText.Key("ClashOfRim.Shop.NoThingSelected"));
            editorContentHeight = y + 30f;
            return;
        }

        Rect iconRect = new(inner.x, y, 42f, 42f);
        TradeUiUtility.DrawThingIconWithInfo(iconRect, selectedItem);
        ClashOfRimUiUtility.DrawTruncatedLabel(new Rect(iconRect.xMax + 8f, y + 8f, inner.width - 50f, 26f), TradeUiUtility.ThingLabel(
            selectedItem,
            asRequirement: editor.IsBuyOrder,
            qualityRequirementMode: editor.IsBuyOrder ? editor.QualityMode : null,
            hitPointsRequirementMode: editor.IsBuyOrder ? editor.HitPointsMode : null));
        y += 58f;

        Widgets.Label(new Rect(inner.x, y, labelWidth, 24f), ClashOfRimText.Key(editor.IsBuyOrder ? "ClashOfRim.Shop.DemandShort" : "ClashOfRim.Shop.StockShort"));
        editor.StockText = Widgets.TextField(new Rect(inner.x + FieldX, y, inner.width - FieldX, 26f), editor.StockText);
        y += 36f;
        Widgets.Label(new Rect(inner.x, y, labelWidth, 24f), ClashOfRimText.Key("ClashOfRim.Shop.ItemCount"));
        editor.CountText = Widgets.TextField(new Rect(inner.x + FieldX, y, inner.width - FieldX, 26f), editor.CountText);
        editor.Refresh();
        y += 36f;
        Widgets.Label(new Rect(inner.x, y, labelWidth, 24f), ClashOfRimText.Key(editor.IsBuyOrder ? "ClashOfRim.Shop.UnitPayout" : "ClashOfRim.Shop.UnitPrice"));
        editor.SetPrice(Widgets.TextField(new Rect(inner.x + FieldX, y, inner.width - FieldX, 26f), editor.PriceText));
        y += 34f;
        Widgets.Label(new Rect(inner.x, y, labelWidth, 24f), ClashOfRimText.Key("ClashOfRim.Shop.PriceIncreaseRatio"));
        editor.RatioText = Widgets.TextField(new Rect(inner.x + FieldX, y, inner.width - FieldX, 26f), editor.RatioText);
        TooltipHandler.TipRegion(
            new Rect(inner.x, y, inner.width, 28f),
            ClashOfRimText.Key("ClashOfRim.Shop.PriceIncreaseRatioDesc"));
        y += 34f;
        Widgets.Label(new Rect(inner.x, y, labelWidth, 24f), ClashOfRimText.Key("ClashOfRim.Shop.VanillaMarketValue"));
        Widgets.Label(new Rect(inner.x + FieldX, y, inner.width - FieldX, 24f), editor.MarketValue);
        y += 38f;

        ThingDef? def = TradeThingReferenceUtility.ResolveReferenceDef(selectedItem);
        string surface = editor.Surface;
        if (ClashOfRimCompatibilityApi.TryDrawThingReferenceEditor(
            surface,
            def,
            selectedItem,
            new Rect(inner.x, y, inner.width, 30f),
            out float extensionHeight))
        {
            y += extensionHeight;
        }

        var features = editor.Features;
        if (features.Stuff)
        {
            DrawStuffEditor(new Rect(inner.x, y, inner.width, 30f), selectedItem);
            y += 38f;
        }

        DrawQualityEditor(new Rect(inner.x, y, inner.width, 30f), features, selectedItem);
        y += 38f;
        if (editor.IsBuyOrder && features.Quality)
        {
            DrawQualityRequirementModeEditor(new Rect(inner.x, y, inner.width, 30f));
            y += 38f;
        }

        DrawHitPointsEditor(new Rect(inner.x, y, inner.width, 30f), features);
        y += 36f;
        if (editor.IsBuyOrder && features.HitPoints)
        {
            DrawHitPointsRequirementModeEditor(new Rect(inner.x, y, inner.width, 30f));
            y += 38f;
        }

        if (!editor.Complete)
        {
            GUI.color = Color.yellow;
            Widgets.Label(new Rect(inner.x, y, inner.width, 44f), ClashOfRimText.Key("ClashOfRim.Shop.IncompleteListing"));
            GUI.color = Color.white;
        }
        editorContentHeight = y + (editor.Complete ? 4f : 48f);
    }

    private void DrawListingKindDropdown(Rect rect)
    {
        Widgets.Label(new Rect(rect.x, rect.y + 3f, labelWidth, 24f), ClashOfRimText.Key("ClashOfRim.Shop.Kind"));
        string label = editor.IsBuyOrder
            ? ClashOfRimText.Key("ClashOfRim.Shop.KindBuy")
            : ClashOfRimText.Key("ClashOfRim.Shop.KindSell");
        if (ClashOfRimUiUtility.SelectionButton(
                new Rect(rect.x + FieldX, rect.y, rect.width - FieldX, 28f),
                label))
        {
            var choose = editor.ChooseKind();
            Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
            {
                new(ClashOfRimText.Key("ClashOfRim.Shop.KindSell"), () => choose("SellToPlayer")),
                new(ClashOfRimText.Key("ClashOfRim.Shop.KindBuy"), () => choose("BuyFromPlayer"))
            }));
        }
    }

    private void DrawStuffEditor(Rect rect, ModThingReferenceDto item)
    {
        string? selectedStuff = !string.IsNullOrWhiteSpace(item.MinifiedInnerDefName)
            ? item.MinifiedInnerStuffDefName
            : item.StuffDefName;
        string label = string.IsNullOrWhiteSpace(selectedStuff)
            ? ClashOfRimText.Key("ClashOfRim.Trade.StuffAny")
            : TradeThingReferenceUtility.StuffLabel(selectedStuff);
        if (ClashOfRimUiUtility.SelectionButton(
                new Rect(rect.x + FieldX, rect.y, rect.width - FieldX, 28f),
                label))
        {
            var choose = editor.ChooseStuff();
            List<FloatMenuOption> options = new()
            {
                new FloatMenuOption(ClashOfRimText.Key("ClashOfRim.Any"), () => choose(null))
            };
            foreach (var stuff in editor.Materials())
            {
                var captured = stuff;
                options.Add(new FloatMenuOption(captured.Label, () => choose(captured.DefName)));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        Widgets.Label(new Rect(rect.x, rect.y + 3f, labelWidth, 24f), ClashOfRimText.Key("ClashOfRim.Shop.Stuff"));
    }

    private void DrawQualityEditor(Rect rect, ClientShopItemFeatures features, ModThingReferenceDto item)
    {
        Widgets.Label(new Rect(rect.x, rect.y + 3f, labelWidth, 24f), ClashOfRimText.Key("ClashOfRim.Shop.Quality"));
        if (!features.Quality)
        {
            GUI.color = Color.gray;
            Widgets.Label(new Rect(rect.x + FieldX, rect.y + 3f, rect.width - FieldX, 24f), ClashOfRimText.Key("ClashOfRim.Trade.NoQuality"));
            GUI.color = Color.white;
            return;
        }

        string label = string.IsNullOrWhiteSpace(item.Quality)
            ? ClashOfRimText.Key("ClashOfRim.Trade.QualityAny")
            : ClashOfRimText.Key("ClashOfRim.Trade.QualitySelected", TradeUiUtility.FormatQualityLabel(item.Quality).Named("QUALITY"));
        if (ClashOfRimUiUtility.SelectionButton(
                new Rect(rect.x + FieldX, rect.y, rect.width - FieldX, 28f),
                label))
        {
            var choose = editor.ChooseQuality();
            List<FloatMenuOption> options = new()
            {
                new FloatMenuOption(ClashOfRimText.Key("ClashOfRim.Any"), () => choose(null))
            };
            foreach (QualityCategory quality in Enum.GetValues(typeof(QualityCategory)).Cast<QualityCategory>())
            {
                QualityCategory captured = quality;
                options.Add(new FloatMenuOption(TradeUiUtility.FormatQualityLabel(captured), () => choose(captured.ToString())));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }
    }

    private void DrawQualityRequirementModeEditor(Rect rect)
    {
        Widgets.Label(new Rect(rect.x, rect.y + 3f, labelWidth, 24f), ClashOfRimText.Key("ClashOfRim.Shop.QualityRequirement"));
        string label = ClashOfRimText.Key(string.Equals(editor.QualityMode, "AtMost", StringComparison.Ordinal)
            ? "ClashOfRim.Shop.QualityAtMost"
            : "ClashOfRim.Shop.QualityAtLeast");
        if (ClashOfRimUiUtility.SelectionButton(
                new Rect(rect.x + FieldX, rect.y, rect.width - FieldX, 28f),
                label))
        {
            var choose = editor.ChooseRequirement(true);
            Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
            {
                new(ClashOfRimText.Key("ClashOfRim.Shop.QualityAtLeast"), () => choose("AtLeast")),
                new(ClashOfRimText.Key("ClashOfRim.Shop.QualityAtMost"), () => choose("AtMost"))
            }));
        }
    }

    private void DrawHitPointsRequirementModeEditor(Rect rect)
    {
        Widgets.Label(new Rect(rect.x, rect.y + 3f, labelWidth, 24f), ClashOfRimText.Key("ClashOfRim.Shop.HitPointsRequirement"));
        string label = ClashOfRimText.Key(string.Equals(editor.HitPointsMode, "AtMost", StringComparison.Ordinal)
            ? "ClashOfRim.Shop.HitPointsAtMost"
            : "ClashOfRim.Shop.HitPointsAtLeast");
        if (ClashOfRimUiUtility.SelectionButton(
                new Rect(rect.x + FieldX, rect.y, rect.width - FieldX, 28f),
                label))
        {
            var choose = editor.ChooseRequirement(false);
            Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
            {
                new(ClashOfRimText.Key("ClashOfRim.Shop.HitPointsAtLeast"), () => choose("AtLeast")),
                new(ClashOfRimText.Key("ClashOfRim.Shop.HitPointsAtMost"), () => choose("AtMost"))
            }));
        }
    }

    private void DrawHitPointsEditor(Rect rect, ClientShopItemFeatures features)
    {
        Widgets.Label(new Rect(rect.x, rect.y + 3f, labelWidth, 24f), ClashOfRimText.Key("ClashOfRim.Shop.HitPoints"));
        if (!features.HitPoints)
        {
            GUI.color = Color.gray;
            Widgets.Label(new Rect(rect.x + FieldX, rect.y + 3f, rect.width - FieldX, 24f), ClashOfRimText.Key("ClashOfRim.Trade.NoHitPoints"));
            GUI.color = Color.white;
            return;
        }

        int percent = features.HitPointsPercent;
        Widgets.Label(new Rect(rect.x + FieldX, rect.y + 3f, 52f, 24f), percent + "%");
        float sliderValue = Widgets.HorizontalSlider(
            new Rect(rect.x + FieldX + 54f, rect.y + 2f, rect.width - FieldX - 54f, 28f),
            percent,
            1f,
            100f,
            roundTo: 1f);
        editor.SetHitPointsPercent(Mathf.RoundToInt(sliderValue));
    }

    // Existing RimTest reflection entry points delegate to the extracted owners.
    private static IEnumerable<ThingDef> SearchThingDefs(string text) => ClientShopListingRuntime.SearchThingDefs(text);
    private static ModThingReferenceDto BuildItemReference(ThingDef def) => ClientShopListingRuntime.BuildItemReference(def, ThingReferenceSurfaces.ServerShopListing);
    private static void SetItemStuff(ModThingReferenceDto item, string? value) => ClientShopItemEditing.SetStuff(item, value);
    private static void SetItemQuality(ModThingReferenceDto item, string? value) => ClientShopItemEditing.SetQuality(item, value);
    private static void SetItemHitPoints(ModThingReferenceDto item, int value) => ClientShopItemEditing.SetHitPoints(item, value);
    private static bool HasCompleteItem(ModThingReferenceDto? item, string surface) => ClientShopListingRuntime.HasCompleteItem(item, surface);
}
