using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.ServerShop;

// Engine-backed definition and metadata operations; editor policy lives in its model.
internal sealed class ClientShopListingRuntime : IClientShopListingRuntime
{
    private object? definitions, language;
    private int definitionCount;
    private string? culture;
    private IReadOnlyList<ClientShopDefinition> catalog = Array.Empty<ClientShopDefinition>();
    public object? PresentationOwner => LanguageDatabase.activeLanguage;
    public IReadOnlyList<ClientShopDefinition> Catalog
    {
        get
        {
            var source = DefDatabase<ThingDef>.AllDefsListForReading;
            if (ReferenceEquals(definitions, source) && definitionCount == source.Count && ReferenceEquals(language, PresentationOwner)
                && culture == CultureInfo.CurrentCulture.Name) return catalog;
            definitions = source; definitionCount = source.Count; language = PresentationOwner; culture = CultureInfo.CurrentCulture.Name;
            return catalog = source.Where(def => !def.IsCorpse && (TradeThingReferenceUtility.IsPackableItemDef(def)
                    || TradeThingReferenceUtility.IsBookDef(def) || TradeThingReferenceUtility.IsTradeableItemDef(def)))
                .Select(def => new ClientShopDefinition(def.defName, def.LabelCap))
                .OrderBy(def => def.Label, StringComparer.CurrentCulture).ThenBy(def => def.DefName, StringComparer.Ordinal).ToList().AsReadOnly();
        }
    }
    internal static IEnumerable<ThingDef> SearchThingDefs(string text) => ClientShopListingEditor.SearchCatalog(new ClientShopListingRuntime().Catalog, text)
        .Select(def => DefDatabase<ThingDef>.GetNamed(def.DefName));
    public ModThingReferenceDto Create(string defName, string surface) => BuildItemReference(DefDatabase<ThingDef>.GetNamed(defName), surface);
    internal static ModThingReferenceDto BuildItemReference(ThingDef def, string surface)
    {
        bool packable = TradeThingReferenceUtility.IsPackableItemDef(def);
        var reference = new ModThingReferenceDto
        {
            GlobalKey = "server-shop-template:" + (packable ? "minified:" : string.Empty) + def.defName,
            DefName = (packable ? def.minifiedDef! : def).defName,
            StackCount = 1,
            DisplayLabel = def.LabelCap,
            Quality = TradeUiUtility.DefSupportsQuality(def) ? QualityCategory.Normal.ToString() : null,
            HitPoints = TradeUiUtility.DefSupportsHitPoints(def) ? TradeUiUtility.DefaultHitPoints(def) : null,
            MaxHitPoints = TradeUiUtility.DefSupportsHitPoints(def) ? TradeUiUtility.DefaultHitPoints(def) : null
        };
        if (packable)
        {
            reference.MinifiedInnerDefName = def.defName;
            reference.MinifiedInnerHitPoints = reference.HitPoints;
            reference.MinifiedInnerMaxHitPoints = reference.MaxHitPoints;
            reference.MinifiedInnerQuality = reference.Quality;
        }
        // Extensions see the actual requested surface and complete minified identity once.
        ClashOfRimCompatibilityApi.ApplyThingReferenceDefaultMetadata(surface, def, reference);
        return reference;
    }
    public void ApplyDefaults(ModThingReferenceDto item, string surface) =>
        ClashOfRimCompatibilityApi.ApplyThingReferenceDefaultMetadata(surface, TradeThingReferenceUtility.ResolveReferenceDef(item), item);
    public void Normalize(ModThingReferenceDto item, string surface)
    {
        var def = TradeThingReferenceUtility.ResolveReferenceDef(item);
        ClashOfRimCompatibilityApi.ClearThingReferenceMetadata(surface, def, item);
        if (!TradeThingReferenceUtility.DefSupportsStuff(def)) ClientShopItemEditing.SetStuff(item, null);
    }
    public bool IsComplete(ModThingReferenceDto item, string surface) => HasCompleteItem(item, surface);
    internal static bool HasCompleteItem(ModThingReferenceDto? item, string surface) => item is not null
        && ClashOfRimCompatibilityApi.IsThingReferenceComplete(surface, TradeThingReferenceUtility.ResolveReferenceDef(item), item);
    public ClientShopItemFeatures Features(ModThingReferenceDto item)
    {
        var def = TradeThingReferenceUtility.ResolveReferenceDef(item);
        bool hp = TradeUiUtility.DefSupportsHitPoints(def);
        return new(TradeThingReferenceUtility.DefSupportsStuff(def), TradeUiUtility.DefSupportsQuality(def), hp,
            hp && item.HitPoints.HasValue ? TradeUiUtility.HitPointsPercent(item.HitPoints.Value, def!) : 100);
    }
    public IReadOnlyList<ClientShopDefinition> Materials(ModThingReferenceDto item)
    {
        var def = TradeThingReferenceUtility.ResolveReferenceDef(item);
        return !TradeThingReferenceUtility.DefSupportsStuff(def) ? Array.Empty<ClientShopDefinition>()
            : TradeThingReferenceUtility.AllowedStuffDefs(def!).Select(stuff => new ClientShopDefinition(stuff.defName, stuff.LabelCap)).ToList();
    }
    public int HitPointsFromPercent(ModThingReferenceDto item, int percent) =>
        TradeUiUtility.HitPointsFromPercent(TradeThingReferenceUtility.ResolveReferenceDef(item)!, percent);
    public IReadOnlyList<string> ValueKeyParts(ModThingReferenceDto item) => ClashOfRimCompatibilityApi.ThingReferenceCacheKeyParts(item).ToList();
    public ClientShopItemValue Estimate(ModThingReferenceDto item) => TradeUiUtility.TryEstimateUnitMarketValue(item, out float unitValue)
        ? new(unitValue, TradeUiUtility.FormatMarketValue(unitValue))
        : new(0, ClashOfRimText.Key("ClashOfRim.Unknown"));
}
