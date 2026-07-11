using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer;

public sealed class ServerShopListingDialogWindow : Window
{
    private static List<ThingDef>? cachedSearchableThingDefs;

    private readonly ClashOfRimMod mod;
    private readonly ModServerShopListingDto? listing;
    private Vector2 searchScrollPosition;
    private string searchText = string.Empty;
    private string stockText;
    private string priceText;
    private string priceIncreaseRatioText;
    private string itemCountText;
    private string listingKind;
    private string qualityRequirementMode;
    private string hitPointsRequirementMode;
    private ModThingReferenceDto? selectedItem;
    private string? cachedSearchText;
    private List<ThingDef> cachedSearchResults = new();
    private string? cachedMarketValueKey;
    private string cachedMarketValueText = string.Empty;
    private bool priceManuallyEdited;
    private string? lastSuggestedPriceKey;

    public ServerShopListingDialogWindow(ClashOfRimMod mod, ModServerShopListingDto? listing)
    {
        this.mod = mod;
        this.listing = listing;
        selectedItem = CloneThingReference(listing?.Item);
        stockText = listing?.StockCount.ToString() ?? "1";
        itemCountText = Math.Max(1, selectedItem?.StackCount ?? 1).ToString();
        listingKind = NormalizeListingKind(listing?.ListingKind);
        qualityRequirementMode = NormalizeQualityRequirementMode(listing?.QualityRequirementMode);
        hitPointsRequirementMode = NormalizeRequirementMode(listing?.HitPointsRequirementMode);
        priceManuallyEdited = listing is not null;
        string suggestedPriceText = SuggestedListingPriceText(selectedItem);
        lastSuggestedPriceKey = selectedItem is null ? null : ItemCacheKey(selectedItem);
        priceText = (listing is not null ? ListingBasePrice(listing).ToString() : suggestedPriceText);
        priceIncreaseRatioText = ClashOfRimMod.NormalizeServerShopPriceIncreaseRatio(listing?.PriceIncreaseRatio ?? 1d)
            .ToString("0.###", CultureInfo.InvariantCulture);
        doCloseX = true;
        closeOnAccept = false;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = false;
        forcePause = false;
        draggable = true;
    }

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

        int stock = 0;
        int price = 0;
        int itemCount = 0;
        double priceIncreaseRatio = 0d;
        ApplyItemCountFromText();
        bool canSave = selectedItem is not null
            && int.TryParse(stockText, out stock)
            && int.TryParse(priceText, out price)
            && int.TryParse(itemCountText, out itemCount)
            && TryParsePriceIncreaseRatio(priceIncreaseRatioText, out priceIncreaseRatio)
            && stock >= 0
            && price >= 1
            && itemCount > 0
            && HasCompleteItem(selectedItem, ThingReferenceSurface);
        Rect saveRect = new(inRect.xMax - 132f, inRect.yMax - 36f, 132f, 32f);
        if (Widgets.ButtonText(saveRect, ClashOfRimText.Key("ClashOfRim.Admin.Save"), active: canSave))
        {
            selectedItem!.StackCount = Math.Max(1, itemCount);
            mod.StartUpsertServerShopListing(
                listing?.ListingId,
                listingKind,
                selectedItem!,
                price,
                stock,
                priceIncreaseRatio,
                qualityRequirementMode,
                hitPointsRequirementMode);
            Close();
        }
    }

    private void DrawSearchPanel(Rect rect)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Shop.SelectThing"));
        searchText = Widgets.TextField(new Rect(inner.x, inner.y + 28f, inner.width, 28f), searchText ?? string.Empty);

        List<ThingDef> defs = SearchThingDefsCached(searchText);
        Rect outRect = new(inner.x, inner.y + 62f, inner.width, inner.height - 62f);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, defs.Count * TradeUiUtility.RowHeight));
        Widgets.BeginScrollView(outRect, ref searchScrollPosition, viewRect);
        for (int index = 0; index < defs.Count; index++)
        {
            ThingDef def = defs[index];
            Rect row = new(0f, index * TradeUiUtility.RowHeight, viewRect.width, TradeUiUtility.RowHeight);
            DrawThingDefRow(row, def);
        }

        Widgets.EndScrollView();
    }

    private void DrawThingDefRow(Rect row, ThingDef def)
    {
        Widgets.DrawHighlightIfMouseover(row);
        Rect iconRect = new(row.x + 3f, row.y + 3f, TradeUiUtility.IconSize, TradeUiUtility.IconSize);
        TradeUiUtility.DrawThingIconWithInfo(iconRect, def.defName);
        Text.WordWrap = false;
        Widgets.Label(new Rect(iconRect.xMax + 8f, row.y + 5f, row.width - 86f, 24f), def.LabelCap + " (" + def.defName + ")");
        Text.WordWrap = true;
        if (Widgets.ButtonText(new Rect(row.xMax - 58f, row.y + 4f, 54f, 26f), ClashOfRimText.Key("ClashOfRim.Select")))
        {
            selectedItem = BuildItemReference(def);
            ClashOfRimCompatibilityApi.ApplyThingReferenceDefaultMetadata(ThingReferenceSurface, def, selectedItem);
            itemCountText = Math.Max(1, selectedItem.StackCount).ToString();
            cachedMarketValueKey = null;
            priceManuallyEdited = false;
            RefreshSuggestedPriceIfUntouched(force: true);
        }
    }

    private List<ThingDef> SearchThingDefsCached(string text)
    {
        string query = (text ?? string.Empty).Trim();
        if (cachedSearchText is not null && string.Equals(cachedSearchText, query, StringComparison.Ordinal))
        {
            return cachedSearchResults;
        }

        cachedSearchText = query;
        cachedSearchResults = SearchThingDefs(query).Take(100).ToList();
        return cachedSearchResults;
    }

    private void DrawEditorPanel(Rect rect)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Shop.ListingDetails"));
        float y = inner.y + 36f;
        DrawListingKindDropdown(new Rect(inner.x, y, inner.width, 30f));
        y += 40f;
        if (selectedItem is null)
        {
            Widgets.Label(new Rect(inner.x, y, inner.width, 30f), ClashOfRimText.Key("ClashOfRim.Shop.NoThingSelected"));
            return;
        }

        ApplyItemCountFromText();
        RefreshSuggestedPriceIfUntouched();
        Rect iconRect = new(inner.x, y, 42f, 42f);
        TradeUiUtility.DrawThingIconWithInfo(iconRect, selectedItem);
        Text.WordWrap = false;
        Widgets.Label(new Rect(iconRect.xMax + 8f, y + 8f, inner.width - 50f, 26f), TradeUiUtility.ThingLabel(
            selectedItem,
            asRequirement: IsBuyOrder,
            qualityRequirementMode: IsBuyOrder ? qualityRequirementMode : null,
            hitPointsRequirementMode: IsBuyOrder ? hitPointsRequirementMode : null));
        Text.WordWrap = true;
        y += 58f;

        Widgets.Label(new Rect(inner.x, y, 120f, 24f), ClashOfRimText.Key(IsBuyOrder ? "ClashOfRim.Shop.DemandShort" : "ClashOfRim.Shop.StockShort"));
        stockText = Widgets.TextField(new Rect(inner.x + 130f, y, 120f, 26f), stockText);
        y += 36f;
        Widgets.Label(new Rect(inner.x, y, 120f, 24f), ClashOfRimText.Key("ClashOfRim.Shop.ItemCount"));
        itemCountText = Widgets.TextField(new Rect(inner.x + 130f, y, 120f, 26f), itemCountText);
        ApplyItemCountFromText();
        y += 36f;
        Widgets.Label(new Rect(inner.x, y, 120f, 24f), ClashOfRimText.Key(IsBuyOrder ? "ClashOfRim.Shop.UnitPayout" : "ClashOfRim.Shop.UnitPrice"));
        string editedPriceText = Widgets.TextField(new Rect(inner.x + 130f, y, 120f, 26f), priceText);
        if (!string.Equals(editedPriceText, priceText, StringComparison.Ordinal))
        {
            priceManuallyEdited = true;
        }

        priceText = editedPriceText;
        y += 34f;
        Widgets.Label(new Rect(inner.x, y, 120f, 24f), ClashOfRimText.Key("ClashOfRim.Shop.PriceIncreaseRatio"));
        priceIncreaseRatioText = Widgets.TextField(new Rect(inner.x + 130f, y, 120f, 26f), priceIncreaseRatioText);
        TooltipHandler.TipRegion(
            new Rect(inner.x, y, inner.width, 28f),
            ClashOfRimText.Key("ClashOfRim.Shop.PriceIncreaseRatioDesc"));
        y += 34f;
        Widgets.Label(new Rect(inner.x, y, 120f, 24f), ClashOfRimText.Key("ClashOfRim.Shop.VanillaMarketValue"));
        Widgets.Label(new Rect(inner.x + 130f, y, inner.width - 130f, 24f), FormatVanillaMarketValueCached(selectedItem));
        y += 38f;

        ThingDef? def = TradeThingReferenceUtility.ResolveReferenceDef(selectedItem);
        string surface = ThingReferenceSurface;
        ClashOfRimCompatibilityApi.ClearThingReferenceMetadata(surface, def, selectedItem);
        if (ClashOfRimCompatibilityApi.TryDrawThingReferenceEditor(
            surface,
            def,
            selectedItem,
            new Rect(inner.x, y, inner.width, 30f),
            out float extensionHeight))
        {
            y += extensionHeight;
        }

        if (TradeThingReferenceUtility.DefSupportsStuff(def))
        {
            DrawStuffEditor(new Rect(inner.x, y, inner.width, 30f), def!, selectedItem);
            y += 38f;
        }
        else
        {
            SetItemStuff(selectedItem, null);
        }

        DrawQualityEditor(new Rect(inner.x, y, inner.width, 30f), def, selectedItem);
        y += 38f;
        if (IsBuyOrder && TradeUiUtility.DefSupportsQuality(def))
        {
            DrawQualityRequirementModeEditor(new Rect(inner.x, y, inner.width, 30f));
            y += 38f;
        }

        DrawHitPointsEditor(new Rect(inner.x, y, inner.width, 30f), def, selectedItem);
        y += 36f;
        if (IsBuyOrder && TradeUiUtility.DefSupportsHitPoints(def))
        {
            DrawHitPointsRequirementModeEditor(new Rect(inner.x, y, inner.width, 30f));
            y += 38f;
        }

        if (!HasCompleteItem(selectedItem, ThingReferenceSurface))
        {
            GUI.color = Color.yellow;
            Widgets.Label(new Rect(inner.x, y, inner.width, 44f), ClashOfRimText.Key("ClashOfRim.Shop.IncompleteListing"));
            GUI.color = Color.white;
        }
    }

    private void DrawListingKindDropdown(Rect rect)
    {
        Widgets.Label(new Rect(rect.x, rect.y + 3f, 120f, 24f), ClashOfRimText.Key("ClashOfRim.Shop.Kind"));
        string label = IsBuyOrder
            ? ClashOfRimText.Key("ClashOfRim.Shop.KindBuy")
            : ClashOfRimText.Key("ClashOfRim.Shop.KindSell");
        if (ClashOfRimUiUtility.SelectionButton(
                new Rect(rect.x + 130f, rect.y, rect.width - 130f, 28f),
                label))
        {
            Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
            {
                new(ClashOfRimText.Key("ClashOfRim.Shop.KindSell"), () => SetListingKind("SellToPlayer")),
                new(ClashOfRimText.Key("ClashOfRim.Shop.KindBuy"), () => SetListingKind("BuyFromPlayer"))
            }));
        }
    }

    private void SetListingKind(string kind)
    {
        string normalized = NormalizeListingKind(kind);
        if (string.Equals(listingKind, normalized, StringComparison.Ordinal))
        {
            return;
        }

        listingKind = normalized;
        if (selectedItem is not null)
        {
            ThingDef? def = TradeThingReferenceUtility.ResolveReferenceDef(selectedItem);
            ClashOfRimCompatibilityApi.ApplyThingReferenceDefaultMetadata(ThingReferenceSurface, def, selectedItem);
        }
    }

    private static IEnumerable<ThingDef> SearchThingDefs(string text)
    {
        string query = (text ?? string.Empty).Trim();
        IEnumerable<ThingDef> defs = SearchableThingDefs();
        if (string.IsNullOrWhiteSpace(query))
        {
            return defs;
        }

        return defs.Where(def =>
            def.defName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || (def.label?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
    }

    private static List<ThingDef> SearchableThingDefs()
    {
        return cachedSearchableThingDefs ??= DefDatabase<ThingDef>.AllDefsListForReading
            .Where(def => IsPackableBuildingDef(def)
                || TradeThingReferenceUtility.IsBookDef(def)
                || TradeThingReferenceUtility.IsTradeableItemDef(def))
            .Where(def => !def.IsCorpse)
            .OrderBy(def => def.label)
            .ThenBy(def => def.defName)
            .ToList();
    }

    private static ModThingReferenceDto BuildItemReference(ThingDef def)
    {
        bool packableBuilding = IsPackableBuildingDef(def);
        ThingDef effectiveDef = packableBuilding ? def.minifiedDef! : def;
        var reference = new ModThingReferenceDto
        {
            GlobalKey = "server-shop-template:" + (packableBuilding ? "minified:" : string.Empty) + def.defName,
            DefName = effectiveDef.defName,
            StackCount = 1,
            DisplayLabel = def.LabelCap,
            Quality = TradeUiUtility.DefSupportsQuality(def) ? QualityCategory.Normal.ToString() : null,
            HitPoints = TradeUiUtility.DefSupportsHitPoints(def) ? TradeUiUtility.DefaultHitPoints(def) : null,
            MaxHitPoints = TradeUiUtility.DefSupportsHitPoints(def) ? TradeUiUtility.DefaultHitPoints(def) : null
        };
        ClashOfRimCompatibilityApi.ApplyThingReferenceDefaultMetadata(ThingReferenceSurfaces.ServerShopListing, def, reference);
        if (packableBuilding)
        {
            reference.MinifiedInnerDefName = def.defName;
            reference.MinifiedInnerHitPoints = reference.HitPoints;
            reference.MinifiedInnerMaxHitPoints = reference.MaxHitPoints;
            reference.MinifiedInnerQuality = reference.Quality;
        }

        return reference;
    }

    private static bool IsPackableBuildingDef(ThingDef def)
    {
        return def.category == ThingCategory.Building
            && def.Minifiable
            && def.minifiedDef is not null;
    }

    private static void DrawStuffEditor(Rect rect, ThingDef def, ModThingReferenceDto item)
    {
        string? selectedStuff = !string.IsNullOrWhiteSpace(item.MinifiedInnerDefName)
            ? item.MinifiedInnerStuffDefName
            : item.StuffDefName;
        string label = string.IsNullOrWhiteSpace(selectedStuff)
            ? ClashOfRimText.Key("ClashOfRim.Trade.StuffAny")
            : TradeThingReferenceUtility.StuffLabel(selectedStuff);
        if (ClashOfRimUiUtility.SelectionButton(
                new Rect(rect.x + 130f, rect.y, rect.width - 130f, 28f),
                label))
        {
            List<FloatMenuOption> options = new()
            {
                new FloatMenuOption(ClashOfRimText.Key("ClashOfRim.Any"), () => SetItemStuff(item, null))
            };
            foreach (ThingDef stuff in TradeThingReferenceUtility.AllowedStuffDefs(def))
            {
                ThingDef captured = stuff;
                options.Add(new FloatMenuOption(captured.LabelCap, () => SetItemStuff(item, captured.defName)));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        Widgets.Label(new Rect(rect.x, rect.y + 3f, 120f, 24f), ClashOfRimText.Key("ClashOfRim.Shop.Stuff"));
    }

    private static void DrawQualityEditor(Rect rect, ThingDef? def, ModThingReferenceDto item)
    {
        Widgets.Label(new Rect(rect.x, rect.y + 3f, 120f, 24f), ClashOfRimText.Key("ClashOfRim.Shop.Quality"));
        if (!TradeUiUtility.DefSupportsQuality(def))
        {
            GUI.color = Color.gray;
            Widgets.Label(new Rect(rect.x + 130f, rect.y + 3f, rect.width - 130f, 24f), ClashOfRimText.Key("ClashOfRim.Trade.NoQuality"));
            GUI.color = Color.white;
            return;
        }

        string label = string.IsNullOrWhiteSpace(item.Quality)
            ? ClashOfRimText.Key("ClashOfRim.Trade.QualityAny")
            : ClashOfRimText.Key("ClashOfRim.Trade.QualitySelected", TradeUiUtility.FormatQualityLabel(item.Quality).Named("QUALITY"));
        if (ClashOfRimUiUtility.SelectionButton(
                new Rect(rect.x + 130f, rect.y, rect.width - 130f, 28f),
                label))
        {
            List<FloatMenuOption> options = new()
            {
                new FloatMenuOption(ClashOfRimText.Key("ClashOfRim.Any"), () => SetItemQuality(item, null))
            };
            foreach (QualityCategory quality in Enum.GetValues(typeof(QualityCategory)).Cast<QualityCategory>())
            {
                QualityCategory captured = quality;
                options.Add(new FloatMenuOption(TradeUiUtility.FormatQualityLabel(captured), () => SetItemQuality(item, captured.ToString())));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }
    }

    private void DrawQualityRequirementModeEditor(Rect rect)
    {
        Widgets.Label(new Rect(rect.x, rect.y + 3f, 120f, 24f), ClashOfRimText.Key("ClashOfRim.Shop.QualityRequirement"));
        string label = ClashOfRimText.Key(string.Equals(qualityRequirementMode, "AtMost", StringComparison.Ordinal)
            ? "ClashOfRim.Shop.QualityAtMost"
            : "ClashOfRim.Shop.QualityAtLeast");
        if (ClashOfRimUiUtility.SelectionButton(
                new Rect(rect.x + 130f, rect.y, rect.width - 130f, 28f),
                label))
        {
            Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
            {
                new(ClashOfRimText.Key("ClashOfRim.Shop.QualityAtLeast"), () => qualityRequirementMode = "AtLeast"),
                new(ClashOfRimText.Key("ClashOfRim.Shop.QualityAtMost"), () => qualityRequirementMode = "AtMost")
            }));
        }
    }

    private void DrawHitPointsRequirementModeEditor(Rect rect)
    {
        Widgets.Label(new Rect(rect.x, rect.y + 3f, 120f, 24f), ClashOfRimText.Key("ClashOfRim.Shop.HitPointsRequirement"));
        string label = ClashOfRimText.Key(string.Equals(hitPointsRequirementMode, "AtMost", StringComparison.Ordinal)
            ? "ClashOfRim.Shop.HitPointsAtMost"
            : "ClashOfRim.Shop.HitPointsAtLeast");
        if (ClashOfRimUiUtility.SelectionButton(
                new Rect(rect.x + 130f, rect.y, rect.width - 130f, 28f),
                label))
        {
            Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
            {
                new(ClashOfRimText.Key("ClashOfRim.Shop.HitPointsAtLeast"), () => hitPointsRequirementMode = "AtLeast"),
                new(ClashOfRimText.Key("ClashOfRim.Shop.HitPointsAtMost"), () => hitPointsRequirementMode = "AtMost")
            }));
        }
    }

    private static void DrawHitPointsEditor(Rect rect, ThingDef? def, ModThingReferenceDto item)
    {
        Widgets.Label(new Rect(rect.x, rect.y + 3f, 120f, 24f), ClashOfRimText.Key("ClashOfRim.Shop.HitPoints"));
        if (!TradeUiUtility.DefSupportsHitPoints(def))
        {
            GUI.color = Color.gray;
            Widgets.Label(new Rect(rect.x + 130f, rect.y + 3f, rect.width - 130f, 24f), ClashOfRimText.Key("ClashOfRim.Trade.NoHitPoints"));
            GUI.color = Color.white;
            return;
        }

        int percent = item.HitPoints.HasValue ? TradeUiUtility.HitPointsPercent(item.HitPoints.Value, def!) : 100;
        Widgets.Label(new Rect(rect.x + 130f, rect.y + 3f, 52f, 24f), percent + "%");
        float sliderValue = Widgets.HorizontalSlider(
            new Rect(rect.x + 184f, rect.y + 2f, rect.width - 184f, 28f),
            percent,
            1f,
            100f,
            roundTo: 1f);
        SetItemHitPoints(item, TradeUiUtility.HitPointsFromPercent(def!, Mathf.RoundToInt(sliderValue)));
    }

    private static void SetItemStuff(ModThingReferenceDto item, string? stuffDefName)
    {
        if (!string.IsNullOrWhiteSpace(item.MinifiedInnerDefName))
        {
            item.MinifiedInnerStuffDefName = stuffDefName;
        }
        else
        {
            item.StuffDefName = stuffDefName;
        }
    }

    private static void SetItemQuality(ModThingReferenceDto item, string? quality)
    {
        item.Quality = quality;
        if (!string.IsNullOrWhiteSpace(item.MinifiedInnerDefName))
        {
            item.MinifiedInnerQuality = quality;
        }
    }

    private static void SetItemHitPoints(ModThingReferenceDto item, int hitPoints)
    {
        item.HitPoints = hitPoints;
        if (!string.IsNullOrWhiteSpace(item.MinifiedInnerDefName))
        {
            item.MinifiedInnerHitPoints = hitPoints;
        }
    }

    private static bool HasCompleteItem(ModThingReferenceDto? item, string surface)
    {
        if (item is null)
        {
            return false;
        }

        ThingDef? def = TradeThingReferenceUtility.ResolveReferenceDef(item);
        return ClashOfRimCompatibilityApi.IsThingReferenceComplete(surface, def, item);
    }

    private bool IsBuyOrder => string.Equals(listingKind, "BuyFromPlayer", StringComparison.Ordinal);

    private string ThingReferenceSurface => IsBuyOrder
        ? ThingReferenceSurfaces.TradeRequest
        : ThingReferenceSurfaces.ServerShopListing;

    private void ApplyItemCountFromText()
    {
        if (selectedItem is not null && int.TryParse(itemCountText, out int itemCount))
        {
            selectedItem.StackCount = Math.Max(1, itemCount);
        }
    }

    private static string NormalizeListingKind(string? kind)
    {
        return string.Equals(kind, "BuyFromPlayer", StringComparison.Ordinal)
            ? "BuyFromPlayer"
            : "SellToPlayer";
    }

    private static string NormalizeQualityRequirementMode(string? mode)
    {
        return NormalizeRequirementMode(mode);
    }

    private static string NormalizeRequirementMode(string? mode)
    {
        return string.Equals(mode, "AtMost", StringComparison.Ordinal)
            ? "AtMost"
            : "AtLeast";
    }

    private string FormatVanillaMarketValueCached(ModThingReferenceDto item)
    {
        string key = ItemCacheKey(item);
        if (string.Equals(cachedMarketValueKey, key, StringComparison.Ordinal))
        {
            return cachedMarketValueText;
        }

        cachedMarketValueKey = key;
        cachedMarketValueText = TradeUiUtility.FormatEstimatedUnitMarketValue(item);
        return cachedMarketValueText;
    }

    private void RefreshSuggestedPriceIfUntouched(bool force = false)
    {
        if (selectedItem is null || (priceManuallyEdited && !force))
        {
            return;
        }

        string suggestedKey = ItemCacheKey(selectedItem);
        if (!force && string.Equals(lastSuggestedPriceKey, suggestedKey, StringComparison.Ordinal))
        {
            return;
        }

        lastSuggestedPriceKey = suggestedKey;
        priceText = SuggestedListingPriceText(selectedItem);
    }

    private static string SuggestedListingPriceText(ModThingReferenceDto? item)
    {
        return item is not null && TradeUiUtility.TryEstimateTotalMarketValue(item, out float marketValue)
            ? Mathf.RoundToInt(marketValue).ToString()
            : "0";
    }

    private static int ListingBasePrice(ModServerShopListingDto listing)
    {
        return listing.BasePriceSilver > 0 ? listing.BasePriceSilver : listing.PriceSilver;
    }

    private static bool TryParsePriceIncreaseRatio(string text, out double ratio)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out ratio)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out ratio))
        {
            return false;
        }

        ratio = ClashOfRimMod.NormalizeServerShopPriceIncreaseRatio(ratio);
        return true;
    }

    private static string ItemCacheKey(ModThingReferenceDto item)
    {
        List<string> parts = new()
        {
            item.DefName ?? string.Empty,
            item.StackCount.ToString(),
            item.Quality ?? string.Empty,
            item.HitPoints?.ToString() ?? string.Empty,
            item.StuffDefName ?? string.Empty,
            item.MaxHitPoints?.ToString() ?? string.Empty,
            item.MinifiedInnerDefName ?? string.Empty,
            item.MinifiedInnerStuffDefName ?? string.Empty,
            item.MinifiedInnerQuality ?? string.Empty,
            item.MinifiedInnerHitPoints?.ToString() ?? string.Empty,
            item.MinifiedInnerMaxHitPoints?.ToString() ?? string.Empty,
            item.WornByCorpse?.ToString() ?? string.Empty,
            item.Biocoded?.ToString() ?? string.Empty,
            item.BiocodedPawnLabel ?? string.Empty,
            item.BiocodedPawnGlobalId ?? string.Empty,
            item.MarketValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            item.UniqueWeapon?.ToString() ?? string.Empty,
            item.PawnPackageId ?? string.Empty
        };
        if (item.UniqueWeaponTraits is not null)
        {
            parts.AddRange(item.UniqueWeaponTraits
                .Where(trait => !string.IsNullOrWhiteSpace(trait))
                .OrderBy(trait => trait, StringComparer.Ordinal));
        }

        parts.AddRange((item.Metadata ?? new Dictionary<string, string?>())
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + (pair.Value ?? string.Empty)));
        parts.AddRange(ClashOfRimCompatibilityApi.ThingReferenceCacheKeyParts(item));
        return string.Join("\u001f", parts);
    }

    private static ModThingReferenceDto? CloneThingReference(ModThingReferenceDto? source)
    {
        if (source is null)
        {
            return null;
        }

        return new ModThingReferenceDto
        {
            GlobalKey = source.GlobalKey,
            DefName = source.DefName,
            StackCount = Math.Max(1, source.StackCount),
            Quality = source.Quality,
            HitPoints = source.HitPoints,
            StuffDefName = source.StuffDefName,
            MaxHitPoints = source.MaxHitPoints,
            MinifiedInnerDefName = source.MinifiedInnerDefName,
            MinifiedInnerStuffDefName = source.MinifiedInnerStuffDefName,
            MinifiedInnerQuality = source.MinifiedInnerQuality,
            MinifiedInnerHitPoints = source.MinifiedInnerHitPoints,
            MinifiedInnerMaxHitPoints = source.MinifiedInnerMaxHitPoints,
            WornByCorpse = source.WornByCorpse,
            Biocoded = source.Biocoded,
            BiocodedPawnLabel = source.BiocodedPawnLabel,
            BiocodedPawnGlobalId = source.BiocodedPawnGlobalId,
            DisplayLabel = source.DisplayLabel,
            MarketValue = source.MarketValue,
            UniqueWeapon = source.UniqueWeapon,
            UniqueWeaponTraits = source.UniqueWeaponTraits?.ToList() ?? new List<string>(),
            PawnPackage = source.PawnPackage,
            PawnPackageId = source.PawnPackageId,
            ThingPackage = source.ThingPackage,
            ThingPackageId = source.ThingPackageId,
            Metadata = source.Metadata?.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal) ?? new Dictionary<string, string?>()
        };
    }
}
