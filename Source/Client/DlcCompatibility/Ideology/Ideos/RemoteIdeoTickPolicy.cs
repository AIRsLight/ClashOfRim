using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

public static class RemoteIdeoTickPolicy
{
    private static readonly HashSet<Ideo> ReferencedIdeosThisTick = new();
    private static int referencedIdeosTick = int.MinValue + 1;

    public static bool ShouldSkipTick(Ideo ideo)
    {
        return RemoteIdeoCatalog.IsRemoteShadow(ideo) && !IsReferencedByCurrentWorldPawn(ideo);
    }

    private static bool IsReferencedByCurrentWorldPawn(Ideo ideo)
    {
        RefreshReferencedIdeosIfNeeded();
        return ReferencedIdeosThisTick.Contains(ideo);
    }

    private static void RefreshReferencedIdeosIfNeeded()
    {
        int ticks = Find.TickManager?.TicksGame ?? int.MinValue;
        if (referencedIdeosTick == ticks)
        {
            return;
        }

        referencedIdeosTick = ticks;
        ReferencedIdeosThisTick.Clear();

        if (Find.Maps is not null)
        {
            foreach (Map map in Find.Maps)
            {
                if (map?.mapPawns is null)
                {
                    continue;
                }

                foreach (Pawn pawn in map.mapPawns.AllPawns)
                {
                    Ideo? pawnIdeo = pawn?.Ideo;
                    if (pawnIdeo is not null)
                    {
                        ReferencedIdeosThisTick.Add(pawnIdeo);
                    }
                }
            }
        }

        if (Find.WorldObjects?.Caravans is not null)
        {
            foreach (RimWorld.Planet.Caravan caravan in Find.WorldObjects.Caravans)
            {
                foreach (Pawn pawn in caravan.PawnsListForReading)
                {
                    Ideo? pawnIdeo = pawn?.Ideo;
                    if (pawnIdeo is not null)
                    {
                        ReferencedIdeosThisTick.Add(pawnIdeo);
                    }
                }
            }
        }
    }
}
