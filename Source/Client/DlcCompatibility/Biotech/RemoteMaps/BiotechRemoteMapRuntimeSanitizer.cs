using System;
using AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Genes;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility.Biotech.RemoteMaps;

internal sealed class BiotechRemoteMapRuntimeSanitizer
{
    private readonly Func<bool> enabled;
    private readonly Action<string> message;

    internal BiotechRemoteMapRuntimeSanitizer(Func<bool> enabled, Action<string> message)
    {
        this.enabled = enabled;
        this.message = message;
    }

    internal void Sanitize(Map map)
    {
        if (!enabled() || map?.mapPawns is null || Find.UniqueIDsManager is null)
        {
            return;
        }

        int pawnsTouched = 0;
        int removedGenes = 0;
        int relinkedGenes = 0;
        int localizedGenes = 0;
        foreach (Pawn pawn in map.mapPawns.AllPawns.Where(pawn => pawn?.genes is not null).ToList())
        {
            bool changed = false;
            removedGenes += RemoveInvalidGenes(pawn.genes.Xenogenes, ref changed);
            removedGenes += RemoveInvalidGenes(pawn.genes.Endogenes, ref changed);
            relinkedGenes += RelinkGenesToPawn(pawn.genes.Xenogenes, pawn, ref changed);
            relinkedGenes += RelinkGenesToPawn(pawn.genes.Endogenes, pawn, ref changed);
            int pawnLocalizedGenes = CoreCompatibility.CorePawnGeneIdentity.LocalizeGenes(pawn.genes.Xenogenes)
                + CoreCompatibility.CorePawnGeneIdentity.LocalizeGenes(pawn.genes.Endogenes);
            localizedGenes += pawnLocalizedGenes;
            changed = changed || pawnLocalizedGenes > 0;

            if (!changed)
            {
                continue;
            }

            BiotechGeneRuntime.ResetTrackerCaches(pawn);
            pawn.skills?.DirtyAptitudes();
            pawn.Notify_DisabledWorkTypesChanged();
            pawnsTouched++;
        }

        if (pawnsTouched > 0 || removedGenes > 0 || relinkedGenes > 0)
        {
            message("[ClashOfRim][RemoteMapProjection][Biotech] Sanitized loaded remote-map genes: pawns="
                + pawnsTouched
                + ", removedGenes="
                + removedGenes
                + ", relinkedGenes="
                + relinkedGenes
                + ", localizedGenes="
                + localizedGenes
                + ".");
        }
    }

    private static int RemoveInvalidGenes(List<Gene>? genes, ref bool changed)
    {
        if (genes is null)
        {
            return 0;
        }

        int removed = genes.RemoveAll(gene => gene is null || gene.def is null);
        if (removed > 0)
        {
            changed = true;
        }

        return removed;
    }

    private static int RelinkGenesToPawn(List<Gene>? genes, Pawn pawn, ref bool changed)
    {
        if (genes is null)
        {
            return 0;
        }

        int relinked = 0;
        foreach (Gene gene in genes)
        {
            if (gene.pawn == pawn)
            {
                continue;
            }

            gene.pawn = pawn;
            changed = true;
            relinked++;
        }

        return relinked;
    }

}
