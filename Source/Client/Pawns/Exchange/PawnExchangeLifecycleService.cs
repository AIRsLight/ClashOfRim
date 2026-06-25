using Verse;

namespace AIRsLight.ClashOfRim.Pawns;

internal static class PawnExchangeLifecycleService
{
    public static void StripExchangeTags(Pawn? pawn)
    {
        PawnExchangeScribeRestorer.StripLocalExchangeTags(pawn);
    }

    public static int CleanupRelationshipArtifacts(Pawn? pawn)
    {
        return PawnExchangeScribeRestorer.CleanupRelationshipArtifactsForRemovedPawn(pawn);
    }

    public static int CleanupRelationshipPlaceholdersAfterRemoval(Pawn? pawn)
    {
        return CleanupRelationshipArtifacts(pawn);
    }

    public static bool RemoveFromLocalWorld(Pawn? pawn)
    {
        if (pawn is null || pawn.Destroyed)
        {
            return true;
        }

        CleanupRelationshipPlaceholdersAfterRemoval(pawn);

        if (pawn.Spawned)
        {
            pawn.DeSpawn(DestroyMode.Vanish);
        }

        if (!pawn.Destroyed)
        {
            pawn.Destroy(DestroyMode.Vanish);
        }

        return pawn.Destroyed || !pawn.Spawned;
    }
}
