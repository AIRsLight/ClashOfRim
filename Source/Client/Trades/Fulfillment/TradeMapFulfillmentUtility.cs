using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Pawns;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

internal static class TradeMapFulfillmentUtility
{
    public static bool TryReserveDropPodDelivery(
        Map map,
        IReadOnlyList<ModThingReferenceDto> requirements,
        int postageSilver,
        string userId,
        string colonyId,
        string snapshotId,
        out IReadOnlyList<ModThingReferenceDto> deliveredThings,
        out string message)
    {
        deliveredThings = Array.Empty<ModThingReferenceDto>();
        var inventory = new List<ThingCountState>();
        foreach (Thing thing in TradeInventoryUtility.AccessibleMapThings(map, beaconOnly: false))
        {
            inventory.Add(new ThingCountState(thing, Math.Max(1, thing.stackCount)));
        }

        if (!TryPlanRequirementRemoval(inventory, requirements, out List<ThingRemovalPlan> removalPlans, out message))
        {
            return false;
        }

        int remainingSilver = Math.Max(0, postageSilver);
        var silverPlans = new List<ThingRemovalPlan>();
        var silverStates = new List<ThingCountState>();
        foreach (ThingCountState state in inventory)
        {
            if (state.RemainingCount > 0 && state.Thing.def == ThingDefOf.Silver)
            {
                silverStates.Add(state);
            }
        }

        silverStates.Sort((left, right) => left.RemainingCount.CompareTo(right.RemainingCount));
        foreach (ThingCountState state in silverStates)
        {
            if (remainingSilver <= 0)
            {
                break;
            }

            int take = Math.Min(remainingSilver, state.RemainingCount);
            if (take <= 0)
            {
                continue;
            }

            silverPlans.Add(new ThingRemovalPlan(state.Thing, take));
            state.RemainingCount -= take;
            remainingSilver -= take;
        }

        if (remainingSilver > 0)
        {
            message = ClashOfRimText.Key(
                "ClashOfRim.Trade.StatusDropPodPostageInsufficient",
                postageSilver.Named("NEEDED"),
                (postageSilver - remainingSilver).Named("AVAILABLE"));
            return false;
        }

        var delivered = new List<ModThingReferenceDto>(removalPlans.Count);
        foreach (ThingRemovalPlan plan in removalPlans)
        {
            delivered.Add(ToThingReference(plan.Thing, plan.Count, map, userId, colonyId, snapshotId));
        }

        deliveredThings = delivered;

        foreach (ThingRemovalPlan plan in removalPlans)
        {
            RemoveThingCount(plan.Thing, plan.Count);
        }

        foreach (ThingRemovalPlan plan in silverPlans)
        {
            RemoveThingCount(plan.Thing, plan.Count);
        }

        message = ClashOfRimText.Key(
            "ClashOfRim.Trade.StatusDropPodReservedLocalThings",
            removalPlans.Count.Named("THINGCOUNT"),
            postageSilver.Named("POSTAGE"));
        return true;
    }

    public static ModThingReferenceDto BuildThingReference(
        Thing thing,
        Map map,
        string userId,
        string colonyId,
        string snapshotId,
        int count)
    {
        return ToThingReference(
            thing,
            Math.Min(Math.Max(1, count), Math.Max(1, thing.stackCount)),
            map,
            userId,
            colonyId,
            snapshotId);
    }

    public static bool RemoveSelectedThings(
        IReadOnlyList<TradeOfferSelection> selections,
        out string message)
    {
        int removedEntries = 0;
        foreach (TradeOfferSelection selection in selections)
        {
            Thing thing = selection.Thing;
            int count = Math.Min(Math.Max(1, selection.Count), Math.Max(0, thing.stackCount));
            if (thing.Destroyed || count <= 0)
            {
                message = ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusThingInvalid");
                return false;
            }

            RemoveThingCount(thing, count);
            removedEntries++;
        }

        message = ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusMapReserved", removedEntries.Named("COUNT"));
        return true;
    }

    private static bool TryPlanRequirementRemoval(
        IReadOnlyList<ThingCountState> inventory,
        IReadOnlyList<ModThingReferenceDto> requirements,
        out List<ThingRemovalPlan> removalPlans,
        out string message)
    {
        removalPlans = new List<ThingRemovalPlan>();
        var sortedRequirements = new List<RequirementState>(requirements.Count);
        foreach (ModThingReferenceDto requirement in requirements)
        {
            sortedRequirements.Add(new RequirementState(
                requirement,
                TradeThingReferenceUtility.RequirementStrictness(requirement)));
        }

        sortedRequirements.Sort((left, right) => right.Strictness.CompareTo(left.Strictness));
        foreach (RequirementState requirementState in sortedRequirements)
        {
            ModThingReferenceDto requirement = requirementState.Requirement;
            int remaining = Math.Max(1, requirement.StackCount);
            foreach (ThingCountState state in inventory)
            {
                if (state.RemainingCount <= 0 || !MatchesRequirement(requirement, state.Thing))
                {
                    continue;
                }

                int take = Math.Min(remaining, state.RemainingCount);
                removalPlans.Add(new ThingRemovalPlan(state.Thing, take));
                state.RemainingCount -= take;
                remaining -= take;
                if (remaining <= 0)
                {
                    break;
                }
            }

            if (remaining > 0)
            {
                message = ClashOfRimText.Key(
                    "ClashOfRim.Trade.StatusDropPodMissingRequirements",
                    TradeUiUtility.ThingLabel(requirement, asRequirement: true).Named("THING"));
                return false;
            }
        }

        message = string.Empty;
        return true;
    }

    private static bool MatchesRequirement(ModThingReferenceDto requirement, Thing candidate)
    {
        return TradeThingReferenceUtility.MatchesRequirement(requirement, candidate);
    }

    private static ModThingReferenceDto ToThingReference(
        Thing thing,
        int count,
        Map map,
        string userId,
        string colonyId,
        string snapshotId)
    {
        if (thing is Pawn pawn)
        {
            string globalKey = PawnGlobalIdUtility.Build(userId, pawn);
            return TradePawnUtility.BuildPawnReference(
                pawn,
                globalKey,
                userId,
                colonyId,
                snapshotId,
                "trade-map:" + map.uniqueID);
        }

        CompBiocodable? biocodable = thing.TryGetComp<CompBiocodable>();
        return TradeThingReferenceUtility.BuildThingReference(
            thing,
            $"owner:{userId}/colony:{colonyId}/snapshot:{snapshotId}/map:Map_{map.uniqueID}/thing:{thing.ThingID}",
            count,
            BuildBiocodedPawnGlobalId(userId, colonyId, snapshotId, biocodable?.CodedPawn));
    }

    private static string? BuildBiocodedPawnGlobalId(string userId, string colonyId, string snapshotId, Pawn? pawn)
    {
        if (pawn is null || string.IsNullOrWhiteSpace(pawn.ThingID))
        {
            return null;
        }

        return PawnGlobalIdUtility.Build(userId, pawn);
    }

    private static void RemoveThingCount(Thing thing, int count)
    {
        if (thing is Pawn pawn)
        {
            TradePawnUtility.ApplySoldPawnEffectsAndRemove(pawn);
            return;
        }

        int clamped = Math.Min(count, Math.Max(0, thing.stackCount));
        if (clamped <= 0)
        {
            return;
        }

        Thing removed = thing.SplitOff(clamped);
        if (!removed.Destroyed)
        {
            removed.Destroy(DestroyMode.Vanish);
        }
    }

    private sealed class RequirementState
    {
        public RequirementState(ModThingReferenceDto requirement, int strictness)
        {
            Requirement = requirement;
            Strictness = strictness;
        }

        public ModThingReferenceDto Requirement { get; }

        public int Strictness { get; }
    }

    private sealed class ThingCountState
    {
        public ThingCountState(Thing thing, int remainingCount)
        {
            Thing = thing;
            RemainingCount = remainingCount;
        }

        public Thing Thing { get; }

        public int RemainingCount { get; set; }
    }

    private sealed class ThingRemovalPlan
    {
        public ThingRemovalPlan(Thing thing, int count)
        {
            Thing = thing;
            Count = count;
        }

        public Thing Thing { get; }

        public int Count { get; }
    }
}
