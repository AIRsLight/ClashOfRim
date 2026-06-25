using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class WeaponTraitMarketValueBaselineBuilder
{
    public static ModAdminBaselineExtensionDto Build(string providerId, string kind)
    {
        List<ModAdminBaselineExtensionRecordDto> records = DefDatabase<WeaponTraitDef>.AllDefsListForReading
            .Where(trait => trait is not null && !string.IsNullOrWhiteSpace(trait.defName))
            .OrderBy(trait => trait.modContentPack?.PackageId, StringComparer.Ordinal)
            .ThenBy(trait => trait.defName, StringComparer.Ordinal)
            .Select(trait => new ModAdminBaselineExtensionRecordDto
            {
                Key = trait.defName,
                Values = new Dictionary<string, string>
                {
                    ["defName"] = trait.defName,
                    ["label"] = trait.label ?? string.Empty,
                    ["weaponCategory"] = trait.weaponCategory?.defName ?? string.Empty,
                    ["marketValueOffset"] = trait.marketValueOffset.ToString(CultureInfo.InvariantCulture),
                    ["modPackageId"] = trait.modContentPack?.PackageId ?? string.Empty,
                    ["modName"] = trait.modContentPack?.Name ?? string.Empty
                }
            })
            .ToList();

        return new ModAdminBaselineExtensionDto
        {
            ProviderId = providerId,
            Kind = kind,
            Records = records
        };
    }
}
