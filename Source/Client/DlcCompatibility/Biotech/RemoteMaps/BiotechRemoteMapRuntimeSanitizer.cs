using AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Genes;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.RemoteMaps;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static partial class BiotechCompatibility
{
    internal static void SanitizeBiotechRemoteMapLoaded(
        Map map,
        RemoteSessionMapParent carrier,
        string scope,
        ModSnapshotPackageMetadataDto package)
    {
        if (!HasBiotechPawnExchange || map?.mapPawns is null || Find.UniqueIDsManager is null)
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
            int pawnLocalizedGenes = LocalizeRemoteMapGeneLoadIds(pawn.genes.Xenogenes)
                + LocalizeRemoteMapGeneLoadIds(pawn.genes.Endogenes);
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
            ClashLog.Message("[ClashOfRim][RemoteMapProjection][Biotech] Sanitized loaded remote-map genes: pawns="
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
            if (gene is null || gene.pawn == pawn)
            {
                continue;
            }

            gene.pawn = pawn;
            changed = true;
            relinked++;
        }

        return relinked;
    }

    private static int LocalizeRemoteMapGeneLoadIds(List<Gene>? genes)
    {
        return CoreCompatibility.CorePawnGeneIdentity.LocalizeGenes(genes);
    }
}
