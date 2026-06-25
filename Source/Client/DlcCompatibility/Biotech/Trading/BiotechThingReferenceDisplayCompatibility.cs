using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static partial class BiotechCompatibility
{
    internal static void AppendThingReferenceDisplayParts(ModThingReferenceDto thing, bool asRequirement, List<string> parts)
    {
        if (!HasBiotechTradeMetadata || thing is null || parts is null)
        {
            return;
        }

        NormalizeBiotechMetadata(thing);
        IReadOnlyList<string> targetGeneDefNames = TargetGeneDefNames(thing);
        IReadOnlyList<string> geneDefNames = GeneDefNames(thing);
        if (asRequirement && targetGeneDefNames.Count > 0)
        {
            parts.Add(ClashOfRimText.Key(
                "ClashOfRim.Trade.TargetGeneRequirement",
                FormatGeneList(targetGeneDefNames).Named("GENE")));
        }
        else if (!asRequirement && geneDefNames.Count > 0)
        {
            parts.Add(FormatGeneList(geneDefNames));
        }
    }

    internal static IEnumerable<string> ThingReferenceCacheKeyParts(ModThingReferenceDto thing)
    {
        if (!HasBiotechTradeMetadata)
        {
            yield break;
        }

        NormalizeBiotechMetadata(thing);
        yield return string.Join(",", GeneDefNames(thing));
        yield return string.Join(",", TargetGeneDefNames(thing));
        yield return XenotypeName(thing) ?? string.Empty;
        yield return XenotypeIconDefName(thing) ?? string.Empty;
    }

    internal static bool SuppressesStandardThingStats(ThingDef? def)
    {
        return IsGeneSetHolderDef(def);
    }

    public static bool IsGeneSetHolderDef(ThingDef? def)
    {
        return HasBiotechTradeMetadata
            && def?.thingClass is not null
            && typeof(GeneSetHolderBase).IsAssignableFrom(def.thingClass);
    }

    public static string GeneLabel(string? defName)
    {
        GeneDef? gene = ResolveGeneDef(defName);
        return gene?.label?.CapitalizeFirst() ?? defName ?? ClashOfRimText.Key("ClashOfRim.Any");
    }

    internal static string FormatGeneList(IEnumerable<string>? geneDefNames)
    {
        string[] labels = (geneDefNames ?? Array.Empty<string>())
            .Where(defName => !string.IsNullOrWhiteSpace(defName))
            .Select(GeneLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return labels.Length == 0
            ? ClashOfRimText.Key("ClashOfRim.None")
            : string.Join(ClashOfRimText.Key("ClashOfRim.ListSeparator"), labels);
    }
}
