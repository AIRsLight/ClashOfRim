using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;

namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

internal sealed class ClientShopListingEditor
{
    private readonly ClientAdminShopPanel.Edit edit;
    private readonly IClientShopListingRuntime runtime;
    private readonly CultureInfo inputCulture = CultureInfo.CurrentCulture;
    private IReadOnlyList<ClientShopDefinition>? catalog;
    private IReadOnlyList<ClientShopDefinition> searchResults = Array.Empty<ClientShopDefinition>();
    private string? query;
    private ValueKey? valueKey;
    private bool manualPrice;
    internal ModThingReferenceDto? Item { get; private set; }
    internal string StockText, CountText, RatioText;
    internal string PriceText { get; private set; }
    internal string Kind { get; private set; }
    internal string QualityMode { get; private set; }
    internal string HitPointsMode { get; private set; }
    internal string MarketValue { get; private set; } = string.Empty;
    internal bool IsBuyOrder => Kind == ServerShopListingKinds.BuyFromPlayer;
    internal string Surface => IsBuyOrder ? ThingReferenceSurfaces.TradeRequest : ThingReferenceSurfaces.ServerShopListing;
    internal bool IsCurrent => edit.IsCurrent;
    internal ClientShopItemFeatures Features => Item is null ? new(false, false, false, 100) : runtime.Features(Item);
    internal bool Complete => Item is not null && runtime.IsComplete(Item, Surface);

    internal ClientShopListingEditor(ClientAdminShopPanel.Edit edit, IClientShopListingRuntime runtime)
    {
        this.edit = edit; this.runtime = runtime;
        var listing = edit.Listing;
        Item = listing?.Item;
        StockText = (listing?.StockCount ?? 1).ToString(CultureInfo.InvariantCulture);
        CountText = Math.Max(1, Item?.StackCount ?? 1).ToString(CultureInfo.InvariantCulture);
        RatioText = ServerShopPricing.NormalizeRatio(listing?.PriceIncreaseRatio ?? 1d).ToString("R", CultureInfo.InvariantCulture);
        Kind = ClientShopRules.NormalizeListingKind(listing?.ListingKind);
        QualityMode = NormalizeMode(listing?.QualityRequirementMode);
        HitPointsMode = NormalizeMode(listing?.HitPointsRequirementMode);
        manualPrice = listing is not null;
        PriceText = (listing is null ? 0 : ClientShopRules.BasePrice(listing)).ToString(CultureInfo.InvariantCulture);
        Refresh();
    }

    internal void Close() => edit.Close();
    internal IReadOnlyList<ClientShopDefinition> Search(string? text)
    {
        var current = runtime.Catalog;
        string normalized = (text ?? string.Empty).Trim();
        if (ReferenceEquals(catalog, current) && query == normalized) return searchResults;
        catalog = current; query = normalized;
        return searchResults = SearchCatalog(current, normalized).Take(100).ToList().AsReadOnly();
    }
    internal static IEnumerable<ClientShopDefinition> SearchCatalog(IEnumerable<ClientShopDefinition> source, string? text)
    {
        string query = (text ?? string.Empty).Trim();
        return source.Where(def => def.DefName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || def.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
    }
    internal void Select(string defName) => edit.TryEdit(() =>
    {
        if (!runtime.Catalog.Any(def => def.DefName == defName)) return;
        Item = runtime.Create(defName, Surface);
        CountText = Math.Max(1, Item.StackCount).ToString(CultureInfo.InvariantCulture);
        manualPrice = false; valueKey = null;
        Refresh();
    });
    internal void SetPrice(string text)
    { if (text == PriceText) return; PriceText = text; manualPrice = true; }
    internal void Refresh()
    {
        if (!edit.IsCurrent || Item is null) return;
        if (PanelNumberInput.TryInt(CountText, out int count)) Item.StackCount = Math.Max(1, count);
        runtime.Normalize(Item, Surface);
        // Complete wire data and structured extension parts avoid delimiter collisions
        // and missing new item fields. Preview bytes are intentionally non-authoritative.
        var key = new ValueKey(ClientProtocolJson.Serialize(Item), ClientProtocolJson.Serialize(runtime.ValueKeyParts(Item).ToList()),
            runtime.PresentationOwner, CultureInfo.CurrentCulture.Name);
        if (key == valueKey) return;
        valueKey = key;
        var value = runtime.Estimate(Item);
        MarketValue = value.UnitLabel;
        if (!manualPrice) PriceText = value.SuggestedPrice.ToString(CultureInfo.InvariantCulture);
    }
    internal IReadOnlyList<ClientShopDefinition> Materials() => Item is null ? Array.Empty<ClientShopDefinition>() : runtime.Materials(Item);
    internal Action<string?> ChooseStuff() => BindItem<string?>((item, value) =>
    {
        if (value is null || runtime.Materials(item).Any(stuff => stuff.DefName == value)) ClientShopItemEditing.SetStuff(item, value);
    });
    internal Action<string?> ChooseQuality() => BindItem<string?>((item, value) => ClientShopItemEditing.SetQuality(item, value));
    internal Action<string> ChooseKind() => value => edit.TryEdit(() =>
    {
        string kind = ClientShopRules.NormalizeListingKind(value);
        if (kind == Kind) return;
        Kind = kind;
        if (Item is not null) runtime.ApplyDefaults(Item, Surface);
        Refresh();
    });
    internal Action<string> ChooseRequirement(bool quality) => BindItem<string>((_, value) =>
    { if (quality) QualityMode = NormalizeMode(value); else HitPointsMode = NormalizeMode(value); });
    internal void SetHitPointsPercent(int percent) => edit.TryEdit(() =>
    {
        if (Item is null) return;
        var features = runtime.Features(Item);
        int selected = Math.Clamp(percent, 1, 100);
        // Drawing an unchanged rounded percentage must not rewrite exact durability
        // (or turn an unconstrained requirement into a concrete maximum).
        if (!features.HitPoints || selected == features.HitPointsPercent) return;
        ClientShopItemEditing.SetHitPoints(Item, runtime.HitPointsFromPercent(Item, selected));
        Refresh();
    });
    private Action<T> BindItem<T>(Action<ModThingReferenceDto, T> action)
    {
        var selected = Item;
        return value => edit.TryEdit(() =>
        {
            if (selected is null || !ReferenceEquals(selected, Item)) return;
            action(selected, value); Refresh();
        });
    }
    internal bool CanSave => edit.CanSubmit && TryBuild(out _);
    internal Task? Save() => TryBuild(out var command) ? edit.Submit(command!) : null;
    private bool TryBuild(out ClientShopListingEdit? command)
    {
        command = null; Refresh();
        if (Item is null || !PanelNumberInput.TryInt(StockText, out int stock) || stock < 0
            || !PanelNumberInput.TryInt(PriceText, out int price) || price < 1
            || !PanelNumberInput.TryInt(CountText, out int count) || count < 1 || !TryRatio(RatioText, out double ratio) || !Complete) return false;
        command = new(null, Kind, Item, price, stock, ratio, QualityMode, HitPointsMode);
        return true;
    }
    private bool TryRatio(string text, out double ratio)
    {
        if (!PanelNumberInput.TryDouble(text, out ratio)
            && !(double.TryParse(text, NumberStyles.Float, inputCulture, out ratio) && double.IsFinite(ratio))) return false;
        ratio = ServerShopPricing.NormalizeRatio(ratio);
        return true;
    }
    private static string NormalizeMode(string? value) => value == "AtMost" ? "AtMost" : "AtLeast";
    private sealed record ValueKey(string Item, string Extensions, object? Presentation, string Culture);
}
