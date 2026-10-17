using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Trades;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

internal sealed class ClientTradeDraftSource : IClientTradeSource
{
    private readonly Map map;
    private readonly TradeOrderDraft draft;
    internal ClientTradeDraftSource(Map map, TradeOrderDraft draft)
    {
        this.map = map;
        this.draft = new(draft.LocalOfferedSelections.Select(s => new TradeOfferSelection(s.Thing, s.Count)).ToArray(),
            draft.OfferedThings, draft.RequestedThings, draft.FeeSilver, draft.RequireTradeBeaconRange, draft.AllowSelfPickup, draft.AllowServerDropPod);
    }
    public ClientTradeOperation Operation => ClientTradeOperation.Create;
    public string Key => "";
    public string? Validate(ClashOfRimClientNetworkContext context, ModTradeOrderSummaryDto order) => ValidateInventory(out var failure) ? null : failure;
    public ClientTradeLocalResult Apply(ClashOfRimClientNetworkContext context, ModTradeOrderSummaryDto order, Action mutationStarting)
    { bool success = Reserve(mutationStarting, out var message); return new(success, Array.Empty<ModThingReferenceDto>(), message); }

    private bool ValidateInventory(out string failureReason)
    {
        if (map is null || !Find.Maps.Contains(map))
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Trade.StatusNoMapReserve");
            return false;
        }

        HashSet<Thing> accessibleItems = new(TradeInventoryUtility.AccessibleMapItems(map, draft.RequireTradeBeaconRange));
        foreach (TradeOfferSelection selection in draft.LocalOfferedSelections)
        {
            if (selection.Thing.Destroyed || selection.Thing.Map != map || selection.Count <= 0)
            {
                failureReason = ClashOfRimText.Key("ClashOfRim.Trade.StatusTradeThingInvalid");
                return false;
            }

            if (selection.Thing is not Pawn
                && !accessibleItems.Contains(selection.Thing))
            {
                failureReason = ClashOfRimText.Key(
                    "ClashOfRim.Trade.StatusTradeThingOutsideBeacon",
                    selection.Thing.LabelCap.ToString().Named("THING"));
                return false;
            }

            if (selection.Thing.stackCount < selection.Count)
            {
                failureReason = ClashOfRimText.Key(
                    "ClashOfRim.Trade.StatusTradeThingInsufficient",
                    selection.Thing.LabelCap.ToString().Named("THING"),
                    selection.Count.Named("NEEDED"),
                    selection.Thing.stackCount.Named("CURRENT"));
                return false;
            }
        }

        int availableSilver = CountAvailableFeeSilver(map, draft.LocalOfferedSelections, draft.RequireTradeBeaconRange);
        if (availableSilver < draft.FeeSilver)
        {
            failureReason = ClashOfRimText.Key(
                "ClashOfRim.Trade.StatusFeeSilverInsufficient",
                draft.FeeSilver.Named("NEEDED"),
                availableSilver.Named("AVAILABLE"));
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private bool Reserve(Action mutationStarting, out string message)
    {
        try
        {
            mutationStarting();
            foreach (TradeOfferSelection selection in draft.LocalOfferedSelections)
            {
                TradeMapFulfillmentUtility.RemoveThingCount(selection.Thing, selection.Count);
            }

            int remainingFee = draft.FeeSilver;
            if (remainingFee > 0)
            {
                foreach (Thing silver in FeeSilverCandidates(map, draft.RequireTradeBeaconRange)
                             .Where(thing => thing.def == ThingDefOf.Silver)
                             .OrderBy(thing => thing.stackCount)
                             .ToList())
                {
                    if (remainingFee <= 0)
                    {
                        break;
                    }

                    int take = Math.Min(remainingFee, Math.Max(0, silver.stackCount));
                    if (take <= 0)
                    {
                        continue;
                    }

                    TradeMapFulfillmentUtility.RemoveThingCount(silver, take);
                    remainingFee -= take;
                }
            }

            if (remainingFee > 0)
            {
                message = ClashOfRimText.Key("ClashOfRim.Trade.StatusFeeReservationIncomplete", remainingFee.Named("REMAINING"));
                return false;
            }

            message = ClashOfRimText.Key(
                "ClashOfRim.Trade.StatusReservedLocalThings",
                draft.LocalOfferedSelections.Count.Named("THINGCOUNT"),
                draft.FeeSilver.Named("FEE"));
            return true;
        }
        catch (Exception ex)
        {
            message = $"{ex.GetType().Name} {ex.Message}";
            Log.Warning("[ClashOfRim][Trade] local reservation exception: " + ex);
            return false;
        }
    }

    private static int CountAvailableFeeSilver(Map map, IReadOnlyList<TradeOfferSelection> offeredSelections, bool requireTradeBeaconRange)
    {
        return FeeSilverCandidates(map, requireTradeBeaconRange)
            .Where(thing => thing.def == ThingDefOf.Silver)
            .Sum(thing =>
            {
                TradeOfferSelection? selection = offeredSelections.FirstOrDefault(entry => ReferenceEquals(entry.Thing, thing));
                return Math.Max(0, thing.stackCount - (selection?.Count ?? 0));
            });
    }

    private static IEnumerable<Thing> FeeSilverCandidates(Map map, bool requireTradeBeaconRange)
    {
        return TradeInventoryUtility.AccessibleMapItems(map, requireTradeBeaconRange);
    }

}
