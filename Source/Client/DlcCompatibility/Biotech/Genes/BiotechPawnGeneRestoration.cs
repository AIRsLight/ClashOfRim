using AIRsLight.ClashOfRim.Modules.Compatibility.Biotech;
using RimWorld;
using System.Collections.Generic;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Genes;

internal sealed class BiotechPawnGeneRestoration
{
    private readonly XenotypeDef? xenotype;
    private readonly List<GeneDef> endogenes;
    private readonly List<GeneDef> xenogenes;

    private BiotechPawnGeneRestoration(XenotypeDef? xenotype, List<GeneDef> endogenes, List<GeneDef> xenogenes)
    {
        this.xenotype = xenotype;
        this.endogenes = endogenes;
        this.xenogenes = xenogenes;
    }

    internal static bool TryPrepare(Pawn pawn, ReproductiveSourceRecord record,
        out BiotechPawnGeneRestoration? restoration, out string? missingDefName)
    {
        restoration = null;
        missingDefName = null;
        if (pawn.genes is null) return true;

        XenotypeDef? xenotype = XenotypeDefOf.Baseliner;
        if (!string.IsNullOrWhiteSpace(record.XenotypeDefName))
        {
            xenotype = DefDatabase<XenotypeDef>.GetNamedSilentFail(record.XenotypeDefName!);
            if (xenotype is null)
            {
                missingDefName = record.XenotypeDefName;
                return false;
            }
        }

        if (!BiotechGeneRuntime.TryResolveDefs(record.EndogeneDefNames, out var endogenes, out missingDefName)
            || !BiotechGeneRuntime.TryResolveDefs(record.XenogeneDefNames, out var xenogenes, out missingDefName))
            return false;

        restoration = new BiotechPawnGeneRestoration(xenotype, endogenes, xenogenes);
        return true;
    }

    internal void Apply(Pawn pawn)
    {
        if (endogenes.Count == 0 && xenogenes.Count == 0)
        {
            pawn.genes.SetXenotype(xenotype ?? XenotypeDefOf.Baseliner);
            BiotechGeneRuntime.ResetTrackerCaches(pawn);
            return;
        }

        pawn.genes.SetXenotypeDirect(xenotype ?? XenotypeDefOf.Baseliner);
        pawn.genes.Endogenes.Clear();
        pawn.genes.ClearXenogenes();
        foreach (GeneDef gene in endogenes) pawn.genes.AddGene(gene, xenogene: false);
        foreach (GeneDef gene in xenogenes) pawn.genes.AddGene(gene, xenogene: true);
        BiotechGeneRuntime.ResetTrackerCaches(pawn);
        pawn.skills?.DirtyAptitudes();
        pawn.Notify_DisabledWorkTypesChanged();
    }
}
