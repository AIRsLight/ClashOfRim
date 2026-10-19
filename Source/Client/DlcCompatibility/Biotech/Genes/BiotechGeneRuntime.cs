using RimWorld;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Genes;

internal static class BiotechGeneRuntime
{
    private static readonly FieldInfo? GeneCachedGenesField =
        typeof(Pawn_GeneTracker).GetField("cachedGenes", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? GeneCachedGenesAffectAgeField =
        typeof(Pawn_GeneTracker).GetField("cachedGenesAffectAge", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? GeneCachedEnabledNeedsField =
        typeof(Pawn_GeneTracker).GetField("cachedEnabledNeeds", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? GeneCachedDisabledNeedsField =
        typeof(Pawn_GeneTracker).GetField("cachedDisabledNeeds", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? GeneCachedTattoosVisibleField =
        typeof(Pawn_GeneTracker).GetField("cachedTattoosVisible", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? GeneCachedWaterCellCostField =
        typeof(Pawn_GeneTracker).GetField("cachedWaterCellCost", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? GeneHasCachedWaterCostField =
        typeof(Pawn_GeneTracker).GetField("hasCachedWaterCost", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? GeneCachedHasCustomXenotypeField =
        typeof(Pawn_GeneTracker).GetField("cachedHasCustomXenotype", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? GeneCachedCustomXenotypeField =
        typeof(Pawn_GeneTracker).GetField("cachedCustomXenotype", BindingFlags.Instance | BindingFlags.NonPublic);

    internal static GeneDef? ResolveDef(string? defName)
    {
        return string.IsNullOrWhiteSpace(defName)
            ? null
            : DefDatabase<GeneDef>.GetNamedSilentFail(defName);
    }

    internal static bool TryResolveDefs(IEnumerable<string> names, out List<GeneDef> genes, out string? missingDefName)
    {
        genes = new List<GeneDef>();
        missingDefName = null;
        foreach (string name in names)
        {
            GeneDef? gene = ResolveDef(name);
            if (gene is null)
            {
                missingDefName = name;
                return false;
            }
            genes.Add(gene);
        }
        return true;
    }

    internal static void ResetCustomXenotypeCaches(Pawn pawn)
    {
        if (pawn.genes is null) return;
        GeneCachedHasCustomXenotypeField?.SetValue(pawn.genes, null);
        GeneCachedCustomXenotypeField?.SetValue(pawn.genes, null);
    }

    internal static void SetPreviewCustomXenotype(Pawn pawn, CustomXenotype? xenotype)
    {
        if (pawn.genes is null) return;
        GeneCachedHasCustomXenotypeField!.SetValue(pawn.genes, xenotype is not null);
        GeneCachedCustomXenotypeField!.SetValue(pawn.genes, xenotype);
    }

    internal static void ResetTrackerCaches(Pawn pawn)
    {
        if (pawn.genes is null)
        {
            return;
        }

        GeneCachedGenesField?.SetValue(pawn.genes, null);
        GeneCachedGenesAffectAgeField?.SetValue(pawn.genes, null);
        GeneCachedEnabledNeedsField?.SetValue(pawn.genes, null);
        GeneCachedDisabledNeedsField?.SetValue(pawn.genes, null);
        ResetCustomXenotypeCaches(pawn);
        GeneCachedTattoosVisibleField?.SetValue(pawn.genes, null);
        GeneCachedWaterCellCostField?.SetValue(pawn.genes, null);
        GeneHasCachedWaterCostField?.SetValue(pawn.genes, false);
    }
}
