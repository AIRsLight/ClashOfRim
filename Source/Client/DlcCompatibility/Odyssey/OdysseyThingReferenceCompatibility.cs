using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class OdysseyThingReferenceCompatibility
{
    public static bool IsUniqueThingReferenceRequest(ThingDef? def)
    {
        return IsUniqueWeaponDef(def);
    }

    public static void ApplyDefaultThingReferenceMetadata(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        if (!IsSupportedSurface(surface)
            || !IsUniqueWeaponDef(def)
            || item is null)
        {
            return;
        }

        item.UniqueWeapon = true;
        item.UniqueWeaponTraits ??= new List<string>();
        item.Metadata[TradeThingReferenceUtility.WeaponTraitKindMetadataKey] = TradeThingReferenceUtility.WeaponTraitKindSpecialized;
        RefreshUniqueWeaponMarketValue(def!, item);
    }

    public static bool DrawThingReferenceEditor(
        string surface,
        ThingDef? def,
        ModThingReferenceDto item,
        Rect rect,
        out float consumedHeight)
    {
        consumedHeight = 0f;
        if (!IsSupportedSurface(surface)
            || !IsUniqueWeaponDef(def)
            || item is null)
        {
            return false;
        }

        bool serverShopListing = string.Equals(surface, ThingReferenceSurfaces.ServerShopListing, StringComparison.Ordinal);
        if (serverShopListing)
        {
            Widgets.Label(new Rect(rect.x, rect.y + 3f, 120f, 24f), ClashOfRimText.Key("ClashOfRim.Shop.UniqueWeaponTraits"));
            DrawTraitButton(new Rect(rect.x + 130f, rect.y, rect.width - 130f, 28f), def!, item, allowAny: false);
            consumedHeight = 38f;
            return true;
        }

        DrawTraitButton(new Rect(rect.x, rect.y, Math.Min(260f, rect.width), rect.height), def!, item, allowAny: true);
        consumedHeight = rect.height;
        return true;
    }

    public static void ClearThingReferenceMetadata(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        if (item is null || IsUniqueWeaponDef(def))
        {
            return;
        }

        if (!string.Equals(
                TradeThingReferenceUtility.WeaponTraitKind(item),
                TradeThingReferenceUtility.WeaponTraitKindSpecialized,
                StringComparison.Ordinal))
        {
            return;
        }

        item.UniqueWeapon = null;
        item.UniqueWeaponTraits?.Clear();
        item.Metadata.Remove(TradeThingReferenceUtility.WeaponTraitKindMetadataKey);
        item.Metadata.Remove(TradeThingReferenceUtility.UniqueWeaponNameMetadataKey);
        item.Metadata.Remove(TradeThingReferenceUtility.UniqueWeaponColorDefMetadataKey);
    }

    public static bool IsThingReferenceComplete(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        if (!IsUniqueWeaponDef(def))
        {
            return true;
        }

        if (item.UniqueWeapon != true || !SelectedTraitsAreValid(def!, item))
        {
            return false;
        }

        return string.Equals(surface, ThingReferenceSurfaces.TradeRequest, StringComparison.Ordinal)
            || SelectedTraits(item).Count > 0;
    }

    private static void DrawTraitButton(Rect rect, ThingDef def, ModThingReferenceDto item, bool allowAny)
    {
        item.UniqueWeapon = true;
        item.UniqueWeaponTraits ??= new List<string>();
        item.Metadata[TradeThingReferenceUtility.WeaponTraitKindMetadataKey] = TradeThingReferenceUtility.WeaponTraitKindSpecialized;
        string label = item.UniqueWeaponTraits.Count == 0
            ? ClashOfRimText.Key(allowAny
                ? "ClashOfRim.Trade.UniqueWeaponTraitsAny"
                : "ClashOfRim.Shop.SelectUniqueWeaponTraits")
            : string.Join(ClashOfRimText.Key("ClashOfRim.ListSeparator"), item.UniqueWeaponTraits.Select(TraitLabel));
        if (!ClashOfRimUiUtility.SelectionButton(rect, label))
        {
            return;
        }

        List<FloatMenuOption> options = new();
        if (allowAny)
        {
            options.Add(new FloatMenuOption(
                ClashOfRimText.Key("ClashOfRim.Any"),
                () =>
                {
                    item.UniqueWeaponTraits.Clear();
                    RefreshUniqueWeaponMarketValue(def, item);
                }));
        }

        foreach (WeaponTraitDef trait in MatchingTraits(def))
        {
            WeaponTraitDef captured = trait;
            bool selected = HasTrait(item, captured.defName);
            if (selected)
            {
                options.Add(new FloatMenuOption(
                    "[x] " + captured.LabelCap,
                    () =>
                    {
                        item.UniqueWeaponTraits.RemoveAll(defName =>
                            string.Equals(defName, captured.defName, StringComparison.OrdinalIgnoreCase));
                        RefreshUniqueWeaponMarketValue(def, item);
                    }));
                continue;
            }

            if (!CanAddTrait(def, item, captured))
            {
                options.Add(new FloatMenuOption(captured.LabelCap + " (" + ClashOfRimText.Key("ClashOfRim.Trade.UniqueWeaponTraitUnavailable") + ")", null));
                continue;
            }

            options.Add(new FloatMenuOption(
                captured.LabelCap,
                () =>
                {
                    item.UniqueWeaponTraits.Add(captured.defName);
                    RefreshUniqueWeaponMarketValue(def, item);
                }));
        }

        Find.WindowStack.Add(new FloatMenu(options));
    }

    public static ModAdminBaselineExtensionDto? BuildWeaponTraitMarketValueBaseline()
    {
        return WeaponTraitMarketValueBaselineBuilder.Build(
            OdysseyCompatibilityKeys.PackageId,
            OdysseyCompatibilityKeys.WeaponTraitMarketValueBaseline);
    }

    private static bool HasTrait(ModThingReferenceDto item, string traitDefName)
    {
        return item.UniqueWeaponTraits.Any(defName =>
            string.Equals(defName, traitDefName, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<WeaponTraitDef> MatchingTraits(ThingDef def)
    {
        CompProperties_UniqueWeapon? props = def.GetCompProperties<CompProperties_UniqueWeapon>();
        return DefDatabase<WeaponTraitDef>.AllDefsListForReading
            .Where(trait => props?.weaponCategories?.Contains(trait.weaponCategory) == true)
            .OrderBy(trait => trait.label)
            .ThenBy(trait => trait.defName);
    }

    private static bool CanAddTrait(ThingDef def, ModThingReferenceDto item, WeaponTraitDef trait)
    {
        CompProperties_UniqueWeapon? props = def.GetCompProperties<CompProperties_UniqueWeapon>();
        if (props?.weaponCategories?.Contains(trait.weaponCategory) != true)
        {
            return false;
        }

        IReadOnlyList<WeaponTraitDef> selected = SelectedTraits(item);
        if (selected.Count == 0 && !trait.canGenerateAlone)
        {
            return false;
        }

        return selected.All(existing => !trait.Overlaps(existing));
    }

    private static bool SelectedTraitsAreValid(ThingDef def, ModThingReferenceDto item)
    {
        IReadOnlyList<WeaponTraitDef> selected = SelectedTraits(item);
        if (selected.Count == 0)
        {
            return true;
        }

        CompProperties_UniqueWeapon? props = def.GetCompProperties<CompProperties_UniqueWeapon>();
        if (props is null
            || selected.Any(trait => props.weaponCategories?.Contains(trait.weaponCategory) != true)
            || selected.All(trait => !trait.canGenerateAlone))
        {
            return false;
        }

        for (int outer = 0; outer < selected.Count; outer++)
        {
            for (int inner = outer + 1; inner < selected.Count; inner++)
            {
                if (selected[outer].Overlaps(selected[inner]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static IReadOnlyList<WeaponTraitDef> SelectedTraits(ModThingReferenceDto item)
    {
        return (item.UniqueWeaponTraits ?? new List<string>())
            .Select(defName => DefDatabase<WeaponTraitDef>.GetNamedSilentFail(defName))
            .Where(trait => trait is not null)
            .Cast<WeaponTraitDef>()
            .ToList();
    }

    private static string TraitLabel(string traitDefName)
    {
        return DefDatabase<WeaponTraitDef>.GetNamedSilentFail(traitDefName)?.LabelCap.ToString() ?? traitDefName;
    }

    private static void RefreshUniqueWeaponMarketValue(ThingDef def, ModThingReferenceDto item)
    {
        if (def is null || item is null)
        {
            return;
        }

        float value = Math.Max(0f, def.GetStatValueAbstract(StatDefOf.MarketValue));
        foreach (WeaponTraitDef trait in SelectedTraits(item))
        {
            value += trait.marketValueOffset;
        }

        item.MarketValue = Math.Max(0f, value);
    }

    private static bool IsUniqueWeaponDef(ThingDef? def)
    {
        return def is not null
            && def.HasComp(typeof(CompUniqueWeapon))
            && def.GetCompProperties<CompProperties_UniqueWeapon>() is not null;
    }

    private static bool IsSupportedSurface(string surface)
    {
        return string.Equals(surface, ThingReferenceSurfaces.TradeRequest, StringComparison.Ordinal)
            || string.Equals(surface, ThingReferenceSurfaces.ServerShopListing, StringComparison.Ordinal);
    }
}
