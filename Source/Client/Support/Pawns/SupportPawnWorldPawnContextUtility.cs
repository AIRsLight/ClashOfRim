using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Support;

internal static class SupportPawnWorldPawnContextUtility
{
    private const string PawnGlobalIdTagPrefix = "ClashOfRim.PawnGlobalId:";

    public static bool MarkDepartingTemporarySupportPawn(Pawn pawn, string pawnGlobalKey, out string message)
    {
        message = string.Empty;
        if (pawn is null || pawn.Destroyed)
        {
            message = "pawn is missing";
            return false;
        }

        if (Find.WorldPawns is null)
        {
            message = "world pawns are unavailable";
            return false;
        }

        try
        {
            AddGlobalIdTag(pawn, pawnGlobalKey);
            if (!Find.WorldPawns.Contains(pawn))
            {
                Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);
            }

            Find.WorldPawns.ForcefullyKeptPawns.Add(pawn);
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            Log.Warning("[ClashOfRim][Support] Failed to keep departing support pawn as world pawn context: "
                + pawn.ThingID
                + " global="
                + pawnGlobalKey
                + " exception="
                + ex);
            return false;
        }
    }

    public static DetachedSupportPawnContext DetachReturnContext(string? pawnGlobalKey)
    {
        if (Find.WorldPawns is null)
        {
            return DetachedSupportPawnContext.Empty;
        }

        List<Pawn> detached = FindMatchingWorldPawns(pawnGlobalKey)
            .Where(pawn => pawn is { Destroyed: false, Discarded: false })
            .Distinct()
            .ToList();
        foreach (Pawn pawn in detached)
        {
            try
            {
                if (Find.WorldPawns.Contains(pawn))
                {
                    Find.WorldPawns.RemovePawn(pawn);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][Support] Failed to detach support world pawn context: "
                    + pawn.ThingID
                    + " global="
                    + pawnGlobalKey
                    + " exception="
                    + ex);
            }
        }

        return detached.Count == 0
            ? DetachedSupportPawnContext.Empty
            : new DetachedSupportPawnContext(detached);
    }

    public static void RestoreDetachedContext(DetachedSupportPawnContext context)
    {
        if (Find.WorldPawns is null || context.IsEmpty)
        {
            return;
        }

        foreach (Pawn pawn in context.Pawns)
        {
            if (pawn is null || pawn.Destroyed || pawn.Discarded)
            {
                continue;
            }

            try
            {
                if (!Find.WorldPawns.Contains(pawn))
                {
                    Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);
                }

                Find.WorldPawns.ForcefullyKeptPawns.Add(pawn);
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][Support] Failed to restore detached support world pawn context: "
                    + pawn.ThingID
                    + " exception="
                    + ex);
            }
        }
    }

    public static int ReplaceDetachedContextWithReturnedPawn(
        DetachedSupportPawnContext context,
        Pawn returnedPawn,
        string? pawnGlobalKey)
    {
        if (returnedPawn is null)
        {
            RestoreDetachedContext(context);
            return 0;
        }

        AddGlobalIdTag(returnedPawn, pawnGlobalKey);
        if (context.IsEmpty)
        {
            return 0;
        }

        int replaced = 0;
        foreach (Pawn oldPawn in context.Pawns)
        {
            if (oldPawn is null || oldPawn == returnedPawn || oldPawn.Destroyed || oldPawn.Discarded)
            {
                continue;
            }

            try
            {
                TransferDirectRelations(oldPawn, returnedPawn);
                DiscardDetachedContextPawn(oldPawn);
                replaced++;
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][Support] Failed to replace detached support world pawn context: old="
                    + oldPawn.ThingID
                    + " returned="
                    + returnedPawn.ThingID
                    + " exception="
                    + ex);
            }
        }

        if (replaced > 0)
        {
            ClashLog.Message("[ClashOfRim][Support] Replaced support world pawn context with returned pawn: count="
                + replaced
                + " returned="
                + returnedPawn.ThingID
                + ".");
        }

        return replaced;
    }

    public static bool TryMarkSupportPawnLost(
        string? pawnGlobalKey,
        string? fallbackPawnName,
        string? reason,
        out Pawn? primaryPawn,
        out string pawnLabel,
        out string message)
    {
        primaryPawn = null;
        pawnLabel = string.IsNullOrWhiteSpace(fallbackPawnName)
            ? ExtractGlobalPawnLocalThingId(pawnGlobalKey) ?? "unknown"
            : fallbackPawnName!.Trim();
        message = string.Empty;

        if (Find.WorldPawns is null)
        {
            message = ClashOfRimText.Key("ClashOfRim.Support.ApplyLossWorldPawnsUnavailable");
            return false;
        }

        List<Pawn> matches = FindMatchingWorldPawns(pawnGlobalKey)
            .Where(pawn => pawn is { Destroyed: false, Discarded: false })
            .Distinct()
            .ToList();
        if (matches.Count == 0)
        {
            message = ClashOfRimText.Key("ClashOfRim.Support.ApplyLossContextMissing", pawnLabel.Named("PAWN"));
            Log.Warning("[ClashOfRim][Support] Support pawn loss context was missing; confirming loss event without local pawn context. global="
                + (pawnGlobalKey ?? "<null>")
                + " name="
                + pawnLabel
                + " reason="
                + (reason ?? "<null>"));
            return true;
        }

        int affected = 0;
        foreach (Pawn pawn in matches)
        {
            if (primaryPawn is null)
            {
                primaryPawn = pawn;
                pawnLabel = pawn.LabelShortCap;
            }

            try
            {
                AddGlobalIdTag(pawn, pawnGlobalKey);
                if (!Find.WorldPawns.Contains(pawn) && !pawn.Spawned)
                {
                    Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);
                }

                Find.WorldPawns.ForcefullyKeptPawns.Remove(pawn);
                if (!pawn.Dead)
                {
                    pawn.Kill(null);
                }

                affected++;
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][Support] Failed to mark support world pawn as lost: "
                    + pawn.ThingID
                    + " global="
                    + (pawnGlobalKey ?? "<null>")
                    + " exception="
                    + ex);
            }
        }

        if (affected == 0)
        {
            message = ClashOfRimText.Key("ClashOfRim.Support.ApplyLossFailed", pawnLabel.Named("PAWN"));
            return false;
        }

        message = ClashOfRimText.Key("ClashOfRim.Support.ApplyLossMarked", pawnLabel.Named("PAWN"));
        return true;
    }

    private static List<Pawn> FindMatchingWorldPawns(string? pawnGlobalKey)
    {
        var result = new List<Pawn>();
        if (Find.WorldPawns is null)
        {
            return result;
        }

        string? globalTag = string.IsNullOrWhiteSpace(pawnGlobalKey)
            ? null
            : PawnGlobalIdTagPrefix + pawnGlobalKey!.Trim();
        string? localThingId = ExtractGlobalPawnLocalThingId(pawnGlobalKey);
        foreach (Pawn pawn in Find.WorldPawns.AllPawnsAliveOrDead)
        {
            if (pawn is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(globalTag)
                && pawn.questTags?.Contains(globalTag, StringComparer.Ordinal) == true)
            {
                result.Add(pawn);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(localThingId)
                && (string.Equals(pawn.ThingID, localThingId, StringComparison.Ordinal)
                    || string.Equals(pawn.GetUniqueLoadID(), localThingId, StringComparison.Ordinal)
                    || string.Equals(pawn.GetUniqueLoadID(), "Thing_" + localThingId, StringComparison.Ordinal)))
            {
                result.Add(pawn);
            }
        }

        return result;
    }

    private static void TransferDirectRelations(Pawn oldPawn, Pawn returnedPawn)
    {
        foreach (Pawn owner in EnumerateKnownPawns()
                     .Where(pawn => pawn != oldPawn && pawn != returnedPawn)
                     .ToList())
        {
            if (owner.relations?.DirectRelations is null)
            {
                continue;
            }

            foreach (DirectPawnRelation relation in owner.relations.DirectRelations
                         .Where(relation => relation?.otherPawn == oldPawn && relation.def is not null)
                         .ToList())
            {
                AddDirectRelationIfMissing(owner, relation.def, returnedPawn);
                owner.relations.RemoveDirectRelation(relation.def, oldPawn);
            }
        }

        if (oldPawn.relations?.DirectRelations is not null)
        {
            foreach (DirectPawnRelation relation in oldPawn.relations.DirectRelations
                         .Where(relation => relation?.otherPawn is not null && relation.def is not null)
                         .ToList())
            {
                if (relation.otherPawn != returnedPawn)
                {
                    AddDirectRelationIfMissing(returnedPawn, relation.def, relation.otherPawn);
                }

                oldPawn.relations.RemoveDirectRelation(relation.def, relation.otherPawn);
            }
        }

        if (returnedPawn.relations?.DirectRelations is not null)
        {
            foreach (DirectPawnRelation relation in returnedPawn.relations.DirectRelations
                         .Where(relation => relation?.otherPawn == oldPawn && relation.def is not null)
                         .ToList())
            {
                returnedPawn.relations.RemoveDirectRelation(relation.def, oldPawn);
            }
        }
    }

    private static void AddDirectRelationIfMissing(Pawn pawn, PawnRelationDef relationDef, Pawn otherPawn)
    {
        if (pawn == otherPawn || pawn.relations is null)
        {
            return;
        }

        if (!pawn.relations.DirectRelationExists(relationDef, otherPawn))
        {
            pawn.relations.AddDirectRelation(relationDef, otherPawn);
        }
    }

    private static IEnumerable<Pawn> EnumerateKnownPawns()
    {
        var seen = new HashSet<Pawn>();
        foreach (Map map in Find.Maps ?? new List<Map>())
        {
            foreach (Pawn pawn in map.mapPawns?.AllPawns ?? new List<Pawn>())
            {
                if (pawn is not null && seen.Add(pawn))
                {
                    yield return pawn;
                }
            }
        }

        if (Find.WorldObjects is not null)
        {
            foreach (Caravan caravan in Find.WorldObjects.Caravans)
            {
                foreach (Pawn pawn in caravan.PawnsListForReading ?? new List<Pawn>())
                {
                    if (pawn is not null && seen.Add(pawn))
                    {
                        yield return pawn;
                    }
                }
            }
        }

        if (Find.WorldPawns is not null)
        {
            foreach (Pawn pawn in Find.WorldPawns.AllPawnsAliveOrDead)
            {
                if (pawn is not null && seen.Add(pawn))
                {
                    yield return pawn;
                }
            }
        }
    }

    private static void DiscardDetachedContextPawn(Pawn pawn)
    {
        if (pawn.Destroyed || pawn.Discarded)
        {
            return;
        }

        if (pawn.Spawned)
        {
            pawn.DeSpawnOrDeselect(DestroyMode.Vanish);
        }

        if (Find.WorldPawns is not null && Find.WorldPawns.Contains(pawn))
        {
            Find.WorldPawns.RemoveAndDiscardPawnViaGC(pawn);
            return;
        }

        pawn.Destroy(DestroyMode.Vanish);
        if (!pawn.Discarded)
        {
            pawn.Discard(silentlyRemoveReferences: true);
        }
    }

    private static void AddGlobalIdTag(Pawn pawn, string? pawnGlobalKey)
    {
        if (pawn is null || string.IsNullOrWhiteSpace(pawnGlobalKey))
        {
            return;
        }

        QuestUtility.AddQuestTag(ref pawn.questTags, PawnGlobalIdTagPrefix + pawnGlobalKey!.Trim());
    }

    private static string? ExtractGlobalPawnLocalThingId(string? globalId)
    {
        if (string.IsNullOrWhiteSpace(globalId))
        {
            return null;
        }

        const string marker = "/pawn:";
        int start = globalId!.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        string localId = globalId.Substring(start + marker.Length).Trim();
        return localId.Length == 0
            ? null
            : localId.StartsWith("Thing_", StringComparison.Ordinal)
                ? localId.Substring("Thing_".Length)
                : localId;
    }
}

internal sealed class DetachedSupportPawnContext
{
    public static DetachedSupportPawnContext Empty { get; } = new(Array.Empty<Pawn>());

    public DetachedSupportPawnContext(IReadOnlyList<Pawn> pawns)
    {
        Pawns = pawns ?? Array.Empty<Pawn>();
    }

    public IReadOnlyList<Pawn> Pawns { get; }

    public bool IsEmpty => Pawns.Count == 0;
}
