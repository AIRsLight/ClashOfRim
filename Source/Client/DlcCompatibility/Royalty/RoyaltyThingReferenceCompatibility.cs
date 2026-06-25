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

internal static class RoyaltyThingReferenceCompatibility
{
    public static bool IsPersonaThingReferenceRequest(ThingDef? def)
    {
        return IsPersonaWeaponDef(def);
    }

    public static void ApplyDefaultThingReferenceMetadata(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        if (!IsPersonaWeaponDef(def) || item is null)
        {
            return;
        }

        item.UniqueWeapon = true;
        item.UniqueWeaponTraits ??= new List<string>();
        item.Metadata[TradeThingReferenceUtility.WeaponTraitKindMetadataKey] = TradeThingReferenceUtility.WeaponTraitKindPersona;
        RefreshPersonaWeaponMarketValue(def!, item);
    }

    public static bool DrawThingReferenceEditor(
        string surface,
        ThingDef? def,
        ModThingReferenceDto item,
        Rect rect,
        out float consumedHeight)
    {
        consumedHeight = 0f;
        if (!IsPersonaWeaponDef(def) || item is null)
        {
            return false;
        }

        item.UniqueWeapon = true;
        item.UniqueWeaponTraits ??= new List<string>();
        item.Metadata[TradeThingReferenceUtility.WeaponTraitKindMetadataKey] = TradeThingReferenceUtility.WeaponTraitKindPersona;
        if (string.Equals(surface, ThingReferenceSurfaces.ServerShopListing, StringComparison.Ordinal))
        {
            Widgets.Label(new Rect(rect.x, rect.y + 3f, 120f, 24f), ClashOfRimText.Key("ClashOfRim.Shop.PersonaWeaponTraits"));
            DrawTraitButton(new Rect(rect.x + 130f, rect.y, rect.width - 130f, 28f), def!, item, allowAny: false);
            consumedHeight = 38f;
            return true;
        }

        if (!string.Equals(surface, ThingReferenceSurfaces.TradeRequest, StringComparison.Ordinal))
        {
            return false;
        }

        DrawTraitButton(new Rect(rect.x, rect.y, Math.Min(260f, rect.width), rect.height), def!, item, allowAny: true);
        consumedHeight = rect.height;
        return true;
    }

    public static void ClearThingReferenceMetadata(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        if (item is null || IsPersonaWeaponDef(def))
        {
            return;
        }

        if (string.Equals(
                TradeThingReferenceUtility.WeaponTraitKind(item),
                TradeThingReferenceUtility.WeaponTraitKindPersona,
                StringComparison.Ordinal))
        {
            item.UniqueWeapon = null;
            item.UniqueWeaponTraits?.Clear();
            item.Metadata.Remove(TradeThingReferenceUtility.WeaponTraitKindMetadataKey);
            item.Metadata.Remove(TradeThingReferenceUtility.BladelinkWeaponLastKillTickMetadataKey);
        }
    }

    public static bool IsThingReferenceComplete(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        if (!IsPersonaWeaponDef(def))
        {
            return true;
        }

        if (item.UniqueWeapon != true || !SelectedTraitsAreValid(item))
        {
            return false;
        }

        return string.Equals(surface, ThingReferenceSurfaces.TradeRequest, StringComparison.Ordinal)
            || SelectedTraits(item).Count > 0;
    }

    public static ModAdminBaselineExtensionDto? BuildWeaponTraitMarketValueBaseline()
    {
        return WeaponTraitMarketValueBaselineBuilder.Build(
            RoyaltyCompatibilityKeys.PackageId,
            RoyaltyCompatibilityKeys.WeaponTraitMarketValueBaseline);
    }

    private static void DrawTraitButton(Rect rect, ThingDef def, ModThingReferenceDto item, bool allowAny)
    {
        string label;
        if (item.UniqueWeaponTraits.Count == 0)
        {
            label = allowAny
                ? ClashOfRimText.Key("ClashOfRim.Trade.PersonaWeaponTraitsAny")
                : ClashOfRimText.Key("ClashOfRim.Shop.SelectPersonaWeaponTraits");
        }
        else
        {
            label = string.Join(ClashOfRimText.Key("ClashOfRim.ListSeparator"), item.UniqueWeaponTraits.Select(TraitLabel));
        }

        if (!Widgets.ButtonText(rect, label))
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
                    RefreshPersonaWeaponMarketValue(def, item);
                }));
        }

        foreach (WeaponTraitDef trait in MatchingTraits())
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
                        RefreshPersonaWeaponMarketValue(def, item);
                    }));
                continue;
            }

            if (!CanAddTrait(item, captured))
            {
                options.Add(new FloatMenuOption(captured.LabelCap + " (" + ClashOfRimText.Key("ClashOfRim.Trade.UniqueWeaponTraitUnavailable") + ")", null));
                continue;
            }

            options.Add(new FloatMenuOption(
                captured.LabelCap,
                () =>
                {
                    item.UniqueWeaponTraits.Add(captured.defName);
                    RefreshPersonaWeaponMarketValue(def, item);
                }));
        }

        Find.WindowStack.Add(new FloatMenu(options));
    }

    private static bool HasTrait(ModThingReferenceDto item, string traitDefName)
    {
        return item.UniqueWeaponTraits.Any(defName =>
            string.Equals(defName, traitDefName, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<WeaponTraitDef> MatchingTraits()
    {
        return DefDatabase<WeaponTraitDef>.AllDefsListForReading
            .Where(trait => trait.weaponCategory == WeaponCategoryDefOf.BladeLink)
            .OrderBy(trait => trait.label)
            .ThenBy(trait => trait.defName);
    }

    private static bool CanAddTrait(ModThingReferenceDto item, WeaponTraitDef trait)
    {
        if (trait.weaponCategory != WeaponCategoryDefOf.BladeLink)
        {
            return false;
        }

        return SelectedTraits(item).All(existing => !trait.Overlaps(existing));
    }

    private static bool SelectedTraitsAreValid(ModThingReferenceDto item)
    {
        IReadOnlyList<WeaponTraitDef> selected = SelectedTraits(item);
        if (selected.Any(trait => trait.weaponCategory != WeaponCategoryDefOf.BladeLink))
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

    private static void RefreshPersonaWeaponMarketValue(ThingDef def, ModThingReferenceDto item)
    {
        float value = Math.Max(0f, def.GetStatValueAbstract(StatDefOf.MarketValue));
        foreach (WeaponTraitDef trait in SelectedTraits(item))
        {
            value += trait.marketValueOffset;
        }

        item.MarketValue = Math.Max(0f, value);
    }

    private static bool IsPersonaWeaponDef(ThingDef? def)
    {
        return def is not null
            && def.HasComp(typeof(CompBladelinkWeapon))
            && def.GetCompProperties<CompProperties_BladelinkWeapon>() is not null;
    }
}
