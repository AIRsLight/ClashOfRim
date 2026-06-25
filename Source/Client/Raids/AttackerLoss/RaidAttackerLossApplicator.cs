using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

public static class RaidAttackerLossApplicator
{
    public static RaidAttackerLossApplicationResult Apply(RaidAttackerLossApplicationRequest? request)
    {
        if (request == null)
        {
            return Rejected(
                RaidAttackerLossApplicationResultKind.MissingRequest,
                ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerLossMissingRequest"));
        }

        if (!string.Equals(request.AttackerSnapshotId, request.CurrentSnapshotId, StringComparison.Ordinal))
        {
            return new RaidAttackerLossApplicationResult(
                RaidAttackerLossApplicationResultKind.SnapshotMismatch,
                Array.Empty<string>(),
                Array.Empty<RaidLostThingReference>(),
                triggeredVanillaCaravanLostEvent: false,
                requiresSnapshotUploadConfirmation: false,
                matchedCaravanLoadId: null,
                failureReason: ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerLossSnapshotMismatch"));
        }

        RaidAttackerLossCaravanMatch? match = RaidAttackerLossCaravanMatcher.FindMatchingCaravan(request.LostPawnGlobalKeys);
        if (request.ClientEffect == RaidAttackerLossClientEffect.TriggerVanillaCaravanLostEvent &&
            match != null &&
            match.CanTriggerVanillaCaravanLostEvent)
        {
            TriggerVanillaCaravanLostEvent(match.Caravan, match.OwnerPawns);
            return Applied(
                RaidAttackerLossApplicationResultKind.AppliedWithVanillaCaravanLostEvent,
                request,
                triggeredVanilla: true,
                matchedCaravanLoadId: match.CaravanLoadId,
                failureReason: null);
        }

        int fallbackThoughts = TryGiveLostThoughtsForKnownPawns(request.LostPawnGlobalKeys);
        if (fallbackThoughts > 0)
        {
            ClashLog.Message("[ClashOfRim][RaidLoss] Applied lost thoughts for known pawns without caravan match: count="
                + fallbackThoughts
                + ".");
        }

        return Applied(
            RaidAttackerLossApplicationResultKind.AppliedWithSnapshotFallback,
            request,
            triggeredVanilla: false,
            matchedCaravanLoadId: match?.CaravanLoadId,
            failureReason: match == null
                ? ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerLossNoCaravan")
                : ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerLossIncompleteCaravan"));
    }

    private static void TriggerVanillaCaravanLostEvent(Caravan caravan, IReadOnlyList<Pawn> ownerPawns)
    {
        List<Pawn> owners = ownerPawns
            .Where(pawn => pawn != null && !pawn.Destroyed && caravan.ContainsPawn(pawn))
            .ToList();

        for (int i = 0; i < owners.Count && !caravan.Destroyed; i++)
        {
            PawnDiedOrDownedThoughtsUtility.TryGiveThoughts(owners[i], null, PawnDiedOrDownedThoughtsKind.Lost);
            caravan.Notify_MemberDied(owners[i]);
        }
    }

    private static int TryGiveLostThoughtsForKnownPawns(IEnumerable<string>? lostPawnGlobalKeys)
    {
        List<HashSet<string>> requiredKeyGroups = BuildLocalPawnKeyGroups(lostPawnGlobalKeys);
        if (requiredKeyGroups.Count == 0)
        {
            return 0;
        }

        int applied = 0;
        foreach (Pawn pawn in PawnsFinder.AllMapsWorldAndTemporary_Alive)
        {
            if (pawn == null || pawn.Destroyed)
            {
                continue;
            }

            if (!requiredKeyGroups.Any(group => MatchesAnyPawnKey(pawn, group)))
            {
                continue;
            }

            PawnDiedOrDownedThoughtsUtility.TryGiveThoughts(pawn, null, PawnDiedOrDownedThoughtsKind.Lost);
            applied++;
        }

        return applied;
    }

    private static bool MatchesAnyPawnKey(Pawn pawn, HashSet<string> keys)
    {
        return keys.Contains(pawn.ThingID) || keys.Contains(pawn.GetUniqueLoadID());
    }

    private static List<HashSet<string>> BuildLocalPawnKeyGroups(IEnumerable<string>? globalKeys)
    {
        List<HashSet<string>> groups = new();
        if (globalKeys == null)
        {
            return groups;
        }

        foreach (string globalKey in globalKeys)
        {
            if (string.IsNullOrWhiteSpace(globalKey))
            {
                continue;
            }

            HashSet<string> keys = new(StringComparer.Ordinal);
            AddKey(keys, globalKey);
            int thingMarker = globalKey.LastIndexOf("/thing:", StringComparison.Ordinal);
            if (thingMarker >= 0)
            {
                AddKey(keys, globalKey.Substring(thingMarker + "/thing:".Length));
            }
            else
            {
                int looseMarker = globalKey.LastIndexOf("thing:", StringComparison.Ordinal);
                if (looseMarker >= 0)
                {
                    AddKey(keys, globalKey.Substring(looseMarker + "thing:".Length));
                }
            }

            groups.Add(keys);
        }

        return groups;
    }

    private static void AddKey(HashSet<string> keys, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        keys.Add(key);
        if (key.StartsWith("Thing_", StringComparison.Ordinal))
        {
            keys.Add(key.Substring("Thing_".Length));
        }
        else
        {
            keys.Add("Thing_" + key);
        }
    }

    private static RaidAttackerLossApplicationResult Applied(
        RaidAttackerLossApplicationResultKind kind,
        RaidAttackerLossApplicationRequest request,
        bool triggeredVanilla,
        string? matchedCaravanLoadId,
        string? failureReason)
    {
        RaidAttackerLossApplicationResult result = new(
            kind,
            request.LostPawnGlobalKeys,
            request.LostThings,
            triggeredVanilla,
            requiresSnapshotUploadConfirmation: true,
            matchedCaravanLoadId,
            failureReason);

        RaidAttackerLossSnapshotConfirmationQueue.Enqueue(new RaidAttackerLossSnapshotConfirmationRequest(
            request.SourceRaidEventId,
            request.AttackerSnapshotId,
            kind,
            matchedCaravanLoadId,
            request.LostPawnGlobalKeys,
            request.LostThings));

        return result;
    }

    private static RaidAttackerLossApplicationResult Rejected(
        RaidAttackerLossApplicationResultKind kind,
        string failureReason)
    {
        return new RaidAttackerLossApplicationResult(
            kind,
            Array.Empty<string>(),
            Array.Empty<RaidLostThingReference>(),
            triggeredVanillaCaravanLostEvent: false,
            requiresSnapshotUploadConfirmation: false,
            matchedCaravanLoadId: null,
            failureReason);
    }
}
