using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

internal static class TradeCaravanFulfillmentUtility
{
    public static IReadOnlyList<ModThingReferenceDto> BuildDeliveredThingReferences(
        Caravan caravan,
        string userId,
        string colonyId,
        string snapshotId)
    {
        var references = new List<ModThingReferenceDto>();
        foreach (Thing thing in CaravanInventoryUtility.AllInventoryItems(caravan))
        {
            if (thing is null || thing.Destroyed || !TradeThingReferenceUtility.IsTradeableItem(thing))
            {
                continue;
            }

            references.Add(ToThingReference(thing, caravan, userId, colonyId, snapshotId));
        }

        foreach (Pawn pawn in caravan.PawnsListForReading)
        {
            if (TradePawnUtility.IsTradeablePawn(pawn))
            {
                references.Add(ToThingReference(pawn, caravan, userId, colonyId, snapshotId, 1));
            }
        }

        return references;
    }

    public static ModThingReferenceDto BuildThingReference(
        Thing thing,
        Caravan caravan,
        string userId,
        string colonyId,
        string snapshotId,
        int count,
        string surface = ThingReferenceSurfaces.TradeFulfillment)
    {
        return ToThingReference(thing, caravan, userId, colonyId, snapshotId, count, surface);
    }

    public static bool RemoveSelectedThings(
        Caravan caravan,
        IReadOnlyList<TradeOfferSelection> selections,
        out string message)
    {
        try
        {
            foreach (TradeOfferSelection selection in selections)
            {
                if (selection.Thing.Destroyed
                    || selection.Count <= 0
                    || (selection.Thing is Pawn pawn
                        ? !caravan.ContainsPawn(pawn)
                        : !CaravanInventoryUtility.AllInventoryItems(caravan).Contains(selection.Thing)))
                {
                    message = ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusThingInvalid");
                    return false;
                }

                if (selection.Thing is not Pawn && selection.Thing.stackCount < selection.Count)
                {
                    message = ClashOfRimText.Key(
                        "ClashOfRim.Trade.StatusTradeThingInsufficient",
                        selection.Thing.LabelCap.ToString().Named("THING"),
                        selection.Count.Named("NEEDED"),
                        selection.Thing.stackCount.Named("CURRENT"));
                    return false;
                }
            }

            foreach (TradeOfferSelection selection in selections)
            {
                RemoveThingCount(selection.Thing, selection.Count, caravan);
            }

            message = ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusReserved", selections.Count.Named("COUNT"));
            return true;
        }
        catch (Exception ex)
        {
            message = $"{ex.GetType().Name} {ex.Message}";
            Log.Warning("[ClashOfRim][GiftDelivery] caravan item removal failed: " + ex);
            return false;
        }
    }

    public static bool Satisfies(
        IReadOnlyList<ModThingReferenceDto> requirements,
        IReadOnlyList<ModThingReferenceDto> deliveredThings,
        out IReadOnlyList<string> missingRequirements)
    {
        var missing = new List<string>();
        var deliveredStates = new List<DeliveredThingState>(deliveredThings.Count);
        foreach (ModThingReferenceDto thing in deliveredThings)
        {
            deliveredStates.Add(new DeliveredThingState(thing, Math.Max(1, thing.StackCount)));
        }

        var sortedRequirements = new List<RequirementState>(requirements.Count);
        foreach (ModThingReferenceDto requirement in requirements)
        {
            sortedRequirements.Add(new RequirementState(requirement, RequirementStrictness(requirement)));
        }

        sortedRequirements.Sort((left, right) => right.Strictness.CompareTo(left.Strictness));
        foreach (RequirementState requirementState in sortedRequirements)
        {
            ModThingReferenceDto requirement = requirementState.Requirement;
            int requiredCount = Math.Max(1, requirement.StackCount);
            int deliveredCount = ConsumeMatchingCount(requirement, requiredCount, deliveredStates);
            if (deliveredCount < requiredCount)
            {
                missing.Add(ClashOfRimText.Key(
                    "ClashOfRim.Trade.MissingRequirementLine",
                    TradeUiUtility.ThingLabel(requirement, asRequirement: true).Named("THING"),
                    deliveredCount.Named("COUNT")));
            }
        }

        missingRequirements = missing;
        return missing.Count == 0;
    }

    public static bool ApplyExchangeToCaravan(
        Caravan caravan,
        IReadOnlyList<ModThingReferenceDto> requestedThings,
        IReadOnlyList<ModThingReferenceDto> receivedThings,
        string userId,
        string colonyId,
        string snapshotId,
        out IReadOnlyList<ModThingReferenceDto> deliveredThings,
        out string message)
    {
        deliveredThings = Array.Empty<ModThingReferenceDto>();
        try
        {
            if (!TryPlanRequirementRemoval(caravan, requestedThings, out List<ThingRemovalPlan> removalPlans, out string removeMessage))
            {
                message = removeMessage;
                return false;
            }

            removalPlans = MergeRemovalPlans(removalPlans);
            deliveredThings = removalPlans
                .Select(plan => ToThingReference(plan.Thing, caravan, userId, colonyId, snapshotId, plan.Count))
                .ToList();

            foreach (ThingRemovalPlan plan in removalPlans)
            {
                RemoveThingCount(plan.Thing, plan.Count, caravan);
            }

            if (!AddReceivedThings(caravan, receivedThings, out int receivedStacks, out string receiveMessage))
            {
                message = receiveMessage;
                return false;
            }

            message = ClashOfRimText.Key(
                "ClashOfRim.Trade.CaravanExchangeApplied",
                requestedThings.Count.Named("REQUESTCOUNT"),
                receivedStacks.Named("STACKCOUNT"));
            return true;
        }
        catch (Exception ex)
        {
            message = $"{ex.GetType().Name} {ex.Message}";
            Log.Warning("[ClashOfRim][TradeFulfill] caravan exchange apply failed: " + ex);
            return false;
        }
    }

    private static bool TryPlanRequirementRemoval(
        Caravan caravan,
        IReadOnlyList<ModThingReferenceDto> requestedThings,
        out List<ThingRemovalPlan> removalPlans,
        out string message)
    {
        removalPlans = new List<ThingRemovalPlan>();
        var inventory = new List<ThingCountState>();
        foreach (Thing thing in CaravanInventoryUtility.AllInventoryItems(caravan))
        {
            if (thing is not null
                && !thing.Destroyed
                && thing.def?.category == ThingCategory.Item)
            {
                inventory.Add(new ThingCountState(thing, Math.Max(1, thing.stackCount)));
            }
        }

        foreach (Pawn pawn in caravan.PawnsListForReading)
        {
            if (TradePawnUtility.IsTradeablePawn(pawn))
            {
                inventory.Add(new ThingCountState(pawn, Math.Max(1, pawn.stackCount)));
            }
        }

        var sortedRequirements = new List<RequirementState>(requestedThings.Count);
        foreach (ModThingReferenceDto requirement in requestedThings)
        {
            sortedRequirements.Add(new RequirementState(requirement, RequirementStrictness(requirement)));
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
                    "ClashOfRim.Trade.CaravanInventoryInsufficient",
                    TradeUiUtility.ThingLabel(requirement, asRequirement: true).Named("THING"));
                return false;
            }
        }

        message = string.Empty;
        return true;
    }

    private static List<ThingRemovalPlan> MergeRemovalPlans(IReadOnlyList<ThingRemovalPlan> removalPlans)
    {
        var merged = new List<ThingRemovalPlan>();
        foreach (ThingRemovalPlan plan in removalPlans)
        {
            ThingRemovalPlan? existing = merged.FirstOrDefault(item => ReferenceEquals(item.Thing, plan.Thing));
            if (existing is null)
            {
                merged.Add(new ThingRemovalPlan(plan.Thing, plan.Count));
            }
            else
            {
                existing.Count += plan.Count;
            }
        }

        return merged;
    }

    private static bool AddReceivedThings(
        Caravan caravan,
        IReadOnlyList<ModThingReferenceDto> receivedThings,
        out int createdStacks,
        out string message)
    {
        createdStacks = 0;
        message = string.Empty;
        foreach (ModThingReferenceDto reference in receivedThings)
        {
            if (TradePawnUtility.IsPawnReference(reference))
            {
                if (reference.PawnPackage is not null
                    && GiftPawnPackageUtility.TryRestoreTradePawn(reference.PawnPackage, out Pawn? pawn, out string restoreMessage)
                    && pawn is not null)
                {
                    PawnExchangePlacementService.AddToCaravan(caravan, pawn);
                    createdStacks++;
                    ClashLog.Message("[ClashOfRim][TradeFulfill] restored received trade pawn: " + restoreMessage);
                }
                else
                {
                    message = ClashOfRimText.Key(
                        "ClashOfRim.Trade.StatusReceivedPawnRestoreFailed",
                        (reference.PawnPackageId ?? reference.DefName ?? "<unknown>").Named("THING"));
                    Log.Warning("[ClashOfRim][TradeFulfill] " + message);
                    return false;
                }

                continue;
            }

            int remaining = Math.Max(1, reference.StackCount);
            while (remaining > 0)
            {
                ThingDef? effectiveDef = TradeThingReferenceUtility.ResolveReferenceDef(reference);
                int stackCount = Math.Min(
                    remaining,
                    string.IsNullOrWhiteSpace(reference.MinifiedInnerDefName)
                        ? Math.Max(1, effectiveDef?.stackLimit ?? 1)
                        : 1);
                if (!TradeThingReferenceUtility.TryMakeThing(reference, stackCount, out Thing? thing, out string? missingDefName)
                    || thing is null)
                {
                    message = ClashOfRimText.Key(
                        "ClashOfRim.Trade.StatusReceivedThingDefMissing",
                        (missingDefName ?? reference.DefName ?? "<unknown>").Named("THING"));
                    Log.Warning("[ClashOfRim][TradeFulfill] " + message);
                    return false;
                }

                CaravanInventoryUtility.GiveThing(caravan, thing);
                createdStacks++;
                remaining -= stackCount;
            }
        }

        return true;
    }

    private static ModThingReferenceDto ToThingReference(
        Thing thing,
        Caravan caravan,
        string userId,
        string colonyId,
        string snapshotId,
        int? countOverride = null,
        string surface = ThingReferenceSurfaces.TradeFulfillment)
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
                "trade-caravan:" + caravan.GetUniqueLoadID());
        }

        CompBiocodable? biocodable = thing.TryGetComp<CompBiocodable>();
        return TradeThingReferenceUtility.BuildThingReference(
            thing,
            $"owner:{userId}/colony:{colonyId}/snapshot:{snapshotId}/caravan:{caravan.GetUniqueLoadID()}/thing:{thing.ThingID}",
            countOverride ?? thing.stackCount,
            BuildBiocodedPawnGlobalId(userId, colonyId, snapshotId, biocodable?.CodedPawn),
            surface);
    }

    private static string? BuildBiocodedPawnGlobalId(string userId, string colonyId, string snapshotId, Pawn? pawn)
    {
        if (pawn is null || string.IsNullOrWhiteSpace(pawn.ThingID))
        {
            return null;
        }

        return PawnGlobalIdUtility.Build(userId, pawn);
    }

    private static void RemoveThingCount(Thing thing, int count, Caravan? caravan = null)
    {
        if (thing is Pawn pawn)
        {
            TradePawnUtility.ApplySoldPawnEffectsAndRemove(pawn, caravan);
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

    private static int ConsumeMatchingCount(
        ModThingReferenceDto requirement,
        int requiredCount,
        IReadOnlyList<DeliveredThingState> deliveredStates)
    {
        int deliveredCount = 0;
        foreach (DeliveredThingState state in deliveredStates)
        {
            if (state.RemainingCount <= 0 || !MatchesRequirement(requirement, state.Thing))
            {
                continue;
            }

            int consumed = Math.Min(requiredCount - deliveredCount, state.RemainingCount);
            state.RemainingCount -= consumed;
            deliveredCount += consumed;
            if (deliveredCount >= requiredCount)
            {
                break;
            }
        }

        return deliveredCount;
    }

    private static bool MatchesRequirement(ModThingReferenceDto requirement, ModThingReferenceDto candidate)
    {
        return TradeThingReferenceUtility.MatchesRequirement(requirement, candidate);
    }

    private static bool MatchesRequirement(ModThingReferenceDto requirement, Thing candidate)
    {
        return TradeThingReferenceUtility.MatchesRequirement(requirement, candidate);
    }

    private static int RequirementStrictness(ModThingReferenceDto requirement)
    {
        return TradeThingReferenceUtility.RequirementStrictness(requirement);
    }

    private sealed class DeliveredThingState
    {
        public DeliveredThingState(ModThingReferenceDto thing, int remainingCount)
        {
            Thing = thing;
            RemainingCount = remainingCount;
        }

        public ModThingReferenceDto Thing { get; }

        public int RemainingCount { get; set; }
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

        public int Count { get; set; }
    }
}
