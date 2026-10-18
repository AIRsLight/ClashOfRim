using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using UnityEngine;
using Verse;
using static AIRsLight.ClashOfRim.ClientWorldRuntimeValues;

namespace AIRsLight.ClashOfRim.Admin;

// Engine definition/stat reads live here; capture sequencing and publication belong to the module.
internal sealed class ClientAdminBaselineRuntime : IClientAdminBaselineSource
{
    public IReadOnlyList<TrapClassificationScanEntry> ScanTraps() => TrapClassificationScanner.ScanLoadedThingDefs();
    public void AppendExtensions(ModSubmitAdminBaselineRequestDto baseline, IReadOnlyList<ModAdminBaselineExtensionRequirementDto>? requirements)
        => ClashOfRimCompatibilityApi.AppendAdminBaselineExtensions(baseline, requirements);

    public List<ModPackableBuildingDto> ReadPackableBuildings()
    {
        return DefDatabase<ThingDef>.AllDefsListForReading
            .Where(def => def != null
                && !string.IsNullOrWhiteSpace(def.defName)
                && Trades.TradeThingReferenceUtility.IsPackableItemDef(def))
            .Select(def => new ModPackableBuildingDto
            {
                DefName = def.defName,
                MinifiedDefName = def.minifiedDef?.defName,
                Label = def.label,
                ModPackageId = def.modContentPack?.PackageId,
                ModName = def.modContentPack?.Name
            })
            .OrderBy(entry => entry.ModPackageId, StringComparer.Ordinal)
            .ThenBy(entry => entry.DefName, StringComparer.Ordinal)
            .ToList();
    }

    public List<ModBuildingBaselineDto> ReadBuildings()
    {
        return DefDatabase<ThingDef>.AllDefsListForReading
            .Where(def => def != null
                && !string.IsNullOrWhiteSpace(def.defName)
                && def.category == ThingCategory.Building)
            .Select(def => new ModBuildingBaselineDto
            {
                DefName = def.defName,
                Label = def.label,
                ModPackageId = def.modContentPack?.PackageId,
                ModName = def.modContentPack?.Name,
                UseHitPoints = def.useHitPoints,
                EstimatedMaxHitPoints = ReadEstimatedMaxHitPoints(def),
                Minifiable = def.Minifiable,
                MinifiedDefName = def.minifiedDef?.defName
            })
            .OrderBy(entry => entry.ModPackageId, StringComparer.Ordinal)
            .ThenBy(entry => entry.DefName, StringComparer.Ordinal)
            .ToList();
    }

    public List<ModStuffHitPointModifierDto> ReadStuffHitPointModifiers()
    {
        return DefDatabase<ThingDef>.AllDefsListForReading
            .Where(def => def != null
                && !string.IsNullOrWhiteSpace(def.defName)
                && def.IsStuff)
            .Select(def => new ModStuffHitPointModifierDto
            {
                DefName = def.defName,
                Label = def.label,
                ModPackageId = def.modContentPack?.PackageId,
                ModName = def.modContentPack?.Name,
                MaxHitPointsFactor = ReadStuffStatModifier(def, "statFactors", StatDefOf.MaxHitPoints, defaultValue: 1f),
                MaxHitPointsOffset = ReadStuffStatModifier(def, "statOffsets", StatDefOf.MaxHitPoints, defaultValue: 0f)
            })
            .Where(entry => entry.MaxHitPointsFactor != 1f || entry.MaxHitPointsOffset != 0f)
            .OrderBy(entry => entry.ModPackageId, StringComparer.Ordinal)
            .ThenBy(entry => entry.DefName, StringComparer.Ordinal)
            .ToList();
    }

    private static float ReadStuffStatModifier(ThingDef stuff, string memberName, StatDef statDef, float defaultValue)
    {
        object? modifiers = ReadMember(stuff.stuffProps, memberName);
        if (modifiers is not System.Collections.IEnumerable enumerable)
        {
            return defaultValue;
        }

        foreach (object? modifier in enumerable)
        {
            object? modifierStat = ReadMember(modifier, "stat");
            string? defName = ReadDefName(modifierStat);
            if (!string.Equals(defName, statDef.defName, StringComparison.Ordinal))
            {
                continue;
            }

            if (TryReadFloatMember(modifier, "value", out float parsed))
            {
                return parsed;
            }
        }

        return defaultValue;
    }

    public List<ModStandardMarketValueDto> ReadMarketValues()
    {
        return DefDatabase<ThingDef>.AllDefsListForReading
            .Where(def => def != null
                && !string.IsNullOrWhiteSpace(def.defName))
            .Select(def => new ModStandardMarketValueDto
            {
                DefName = def.defName,
                MarketValue = ReadThingDefMarketValue(def)
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.DefName) && entry.MarketValue >= 0f)
            .OrderBy(entry => entry.DefName, StringComparer.Ordinal)
            .ToList();
    }

    public List<ModStuffMarketValueDto> ReadStuffMarketValues()
    {
        List<ThingDef> stuffs = DefDatabase<ThingDef>.AllDefsListForReading
            .Where(def => def != null && !string.IsNullOrWhiteSpace(def.defName) && def.IsStuff)
            .ToList();

        var values = new List<ModStuffMarketValueDto>();
        foreach (ThingDef thingDef in DefDatabase<ThingDef>.AllDefsListForReading
            .Where(def => def != null
                && !string.IsNullOrWhiteSpace(def.defName)
                && def.category != ThingCategory.Pawn
                && def.MadeFromStuff))
        {
            float baseValue = ReadThingDefMarketValue(thingDef);
            foreach (ThingDef stuff in stuffs.Where(stuff => stuff.stuffProps?.CanMake(thingDef) == true))
            {
                float stuffValue = ReadThingDefMarketValue(thingDef, stuff);
                if (stuffValue < 0f || Mathf.Approximately(stuffValue, baseValue))
                {
                    continue;
                }

                values.Add(new ModStuffMarketValueDto
                {
                    ThingDefName = thingDef.defName,
                    StuffDefName = stuff.defName,
                    MarketValue = stuffValue
                });
            }
        }

        return values
            .OrderBy(entry => entry.ThingDefName, StringComparer.Ordinal)
            .ThenBy(entry => entry.StuffDefName, StringComparer.Ordinal)
            .ToList();
    }

    public List<ModQualityMarketValueModifierDto> ReadQualityModifiers()
    {
        object? parts = ReadMember(StatDefOf.MarketValue, "parts");
        if (parts is not System.Collections.IEnumerable enumerable)
        {
            return new List<ModQualityMarketValueModifierDto>();
        }

        foreach (object? part in enumerable)
        {
            if (part is not StatPart_Quality)
            {
                continue;
            }

            return Enum.GetValues(typeof(QualityCategory))
                .Cast<QualityCategory>()
                .Select(quality => new ModQualityMarketValueModifierDto
                {
                    Quality = quality.ToString(),
                    Factor = ReadFloatMember(part, "factor" + quality, 1f),
                    MaxGain = ReadFloatMember(part, "maxGain" + quality, 9999999f)
                })
                .ToList();
        }

        return new List<ModQualityMarketValueModifierDto>();
    }

    private static bool TryReadFloatMember(object? source, string memberName, out float value)
        => float.TryParse(Convert.ToString(ReadMember(source, memberName), CultureInfo.InvariantCulture),
            NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static float ReadFloatMember(object source, string memberName, float defaultValue)
        => TryReadFloatMember(source, memberName, out float value) ? value : defaultValue;

    private static float ReadThingDefMarketValue(ThingDef def, ThingDef? stuff = null)
    {
        try { return Mathf.Max(0f, def.GetStatValueAbstract(StatDefOf.MarketValue, stuff)); }
        catch { return Mathf.Max(0f, ReadFloatMember(def, "BaseMarketValue", 0f)); }
    }

    private static int ReadEstimatedMaxHitPoints(ThingDef def)
    {
        if (!def.useHitPoints)
        {
            return 0;
        }

        try
        {
            return Math.Max(1, Mathf.RoundToInt(def.GetStatValueAbstract(StatDefOf.MaxHitPoints)));
        }
        catch
        {
            return Math.Max(1, def.BaseMaxHitPoints);
        }
    }

}
