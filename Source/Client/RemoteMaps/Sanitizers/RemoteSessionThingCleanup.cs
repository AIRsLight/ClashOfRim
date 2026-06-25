using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public static class RemoteSessionThingCleanup
{
    private const string CleanupTagPrefix = "ClashOfRim.RemoteSessionCleanup:";

    public static void MarkMapThingsForCleanup(Map? map, RemoteSessionMapParent? parent)
    {
        if (map is null || parent is null || string.IsNullOrWhiteSpace(parent.SessionId))
        {
            return;
        }

        string tag = CleanupTag(parent.SessionId);
        int marked = 0;
        foreach (Thing thing in SnapshotCleanupCandidates(map))
        {
            if (thing is null || thing.Destroyed)
            {
                continue;
            }

            QuestUtility.AddQuestTag(ref thing.questTags, tag);
            marked++;
        }

        ClashLog.Message("[ClashOfRim][RemoteSession] Marked "
            + marked
            + " transient things for cleanup in session "
            + parent.SessionId
            + ".");
    }

    public static int DiscardMarkedMapThings(Map? map, RemoteSessionMapParent? parent, string reason)
    {
        if (map is null || parent is null || string.IsNullOrWhiteSpace(parent.SessionId))
        {
            return 0;
        }

        string tag = CleanupTag(parent.SessionId);
        int discarded = 0;
        int exempt = 0;
        foreach (Pawn pawn in SnapshotCleanupCandidates(map).OfType<Pawn>().Where(pawn => HasCleanupTag(pawn, tag)).ToList())
        {
            try
            {
                if (ShouldLetVanillaUnloadPawn(pawn))
                {
                    exempt++;
                    continue;
                }

                DiscardPawn(pawn, reason);
                discarded++;
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][RemoteSession] Failed to discard transient pawn "
                    + pawn.ToStringSafe()
                    + " during "
                    + reason
                    + ": "
                    + ex);
            }
        }

        if (discarded > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteSession] Discarded "
                + discarded
                + " marked transient pawns before "
                + reason
                + " (vanilla-unload exempt="
                + exempt
                + ")"
                + ".");
        }

        return discarded;
    }

    public static bool HasRemoteSessionCleanupTag(Thing? thing)
    {
        return thing?.questTags is not null
            && thing.questTags.Any(tag =>
                !string.IsNullOrWhiteSpace(tag)
                && tag.StartsWith(CleanupTagPrefix, StringComparison.Ordinal));
    }

    public static int KeepReferencePawnsForCleanup(IEnumerable<Pawn>? pawns, RemoteSessionMapParent? parent)
    {
        if (pawns is null || parent is null || string.IsNullOrWhiteSpace(parent.SessionId))
        {
            return 0;
        }

        string tag = CleanupTag(parent.SessionId);
        int kept = 0;
        foreach (Pawn pawn in pawns.Where(pawn => pawn is not null).Distinct().ToList())
        {
            if (pawn.Destroyed || pawn.Discarded)
            {
                continue;
            }

            QuestUtility.AddQuestTag(ref pawn.questTags, tag);
            if (Find.WorldPawns is not null && !Find.WorldPawns.Contains(pawn))
            {
                Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);
            }

            kept++;
        }

        if (kept > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteSession] Kept "
                + kept
                + " temporary reference world pawns for session "
                + parent.SessionId
                + ".");
        }

        return kept;
    }

    public static int DiscardMarkedReferencePawns(RemoteSessionMapParent? parent, string reason)
    {
        if (parent is null || string.IsNullOrWhiteSpace(parent.SessionId) || Find.WorldPawns is null)
        {
            return 0;
        }

        string tag = CleanupTag(parent.SessionId);
        int discarded = 0;
        foreach (Pawn pawn in Find.WorldPawns.AllPawnsAliveOrDead
                     .Where(pawn => pawn is not null && HasCleanupTag(pawn, tag))
                     .ToList())
        {
            try
            {
                DiscardPawn(pawn, reason);
                discarded++;
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][RemoteSession] Failed to discard temporary reference pawn "
                    + pawn.ToStringSafe()
                    + " during "
                    + reason
                    + ": "
                    + ex);
            }
        }

        if (discarded > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteSession] Discarded "
                + discarded
                + " temporary reference world pawns before "
                + reason
                + ".");
        }

        return discarded;
    }

    private static IEnumerable<Thing> SnapshotCleanupCandidates(Map map)
    {
        var seen = new HashSet<Thing>();
        foreach (Thing thing in map.listerThings?.AllThings ?? new List<Thing>())
        {
            if (seen.Add(thing))
            {
                yield return thing;
            }
        }

        foreach (Pawn pawn in map.mapPawns?.AllPawns ?? new List<Pawn>())
        {
            if (seen.Add(pawn))
            {
                yield return pawn;
            }
        }
    }

    private static bool HasCleanupTag(Thing thing, string tag)
    {
        return thing.questTags is not null
            && thing.questTags.Contains(tag, StringComparer.Ordinal);
    }

    private static string CleanupTag(string sessionId)
    {
        return CleanupTagPrefix + sessionId;
    }

    private static void DiscardPawn(Pawn pawn, string reason)
    {
        if (pawn.Destroyed || pawn.Discarded)
        {
            return;
        }

        if (ClashOfRimCompatibilityApi.TryCleanupRemoteSessionPawn(pawn, reason))
        {
            return;
        }

        int cleanedRelationships = PawnExchangeLifecycleService.CleanupRelationshipPlaceholdersAfterRemoval(pawn);
        if (cleanedRelationships > 0)
        {
            ClashLog.Message(
                $"[ClashOfRim][RemoteMapCleanup] cleaned relationship placeholders before discarding pawn={pawn.ThingID} count={cleanedRelationships}.");
        }

        NotifyPawnDiscardedSilently(pawn);

        if (pawn.Spawned)
        {
            pawn.DeSpawnOrDeselect(DestroyMode.Vanish);
        }

        if (Find.WorldPawns is not null)
        {
            if (Find.WorldPawns.Contains(pawn))
            {
                Find.WorldPawns.RemoveAndDiscardPawnViaGC(pawn);
            }
            else
            {
                Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.Discard);
            }

            return;
        }

        pawn.Destroy(DestroyMode.Vanish);
    }

    private static void NotifyPawnDiscardedSilently(Pawn pawn)
    {
        if (Current.ProgramState != ProgramState.Playing)
        {
            return;
        }

        try
        {
            Find.PlayLog?.Notify_PawnDiscarded(pawn, silentlyRemoveReferences: true);
            Find.BattleLog?.Notify_PawnDiscarded(pawn, silentlyRemoveReferences: true);
            Find.TaleManager?.Notify_PawnDiscarded(pawn, silentlyRemoveReferences: true);
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][RemoteSession] Failed to silently clear log references for transient pawn "
                + pawn.ToStringSafe()
                + ": "
                + ex);
        }
    }

    private static bool ShouldLetVanillaUnloadPawn(Pawn pawn)
    {
        if (pawn is null || string.IsNullOrWhiteSpace(pawn.ThingID))
        {
            return false;
        }

        if (ClashOfRimGameComponent.FindSupportAssignment(pawn) is not null)
        {
            return true;
        }

        ActiveRaidBattleSession? raid = ClashOfRimGameComponent.ActiveRaidBattleSession;
        return raid?.AttackPawnThingIds is not null
            && raid.AttackPawnThingIds.Contains(pawn.ThingID, StringComparer.Ordinal);
    }
}
