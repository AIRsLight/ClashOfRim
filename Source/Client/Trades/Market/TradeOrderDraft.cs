using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

public sealed class TradeOrderDraft
{
    public TradeOrderDraft(
        IReadOnlyList<TradeOfferSelection> localOfferedSelections,
        IReadOnlyList<ModThingReferenceDto> offeredThings,
        IReadOnlyList<ModThingReferenceDto> requestedThings,
        int feeSilver,
        bool requireTradeBeaconRange,
        bool allowSelfPickup,
        bool allowServerDropPod)
    {
        LocalOfferedSelections = localOfferedSelections;
        OfferedThings = offeredThings;
        RequestedThings = requestedThings;
        FeeSilver = feeSilver;
        RequireTradeBeaconRange = requireTradeBeaconRange;
        AllowSelfPickup = allowSelfPickup;
        AllowServerDropPod = allowServerDropPod;
    }

    public IReadOnlyList<TradeOfferSelection> LocalOfferedSelections { get; }

    public IReadOnlyList<ModThingReferenceDto> OfferedThings { get; }

    public IReadOnlyList<ModThingReferenceDto> RequestedThings { get; }

    public int FeeSilver { get; }

    public bool RequireTradeBeaconRange { get; }

    public bool AllowSelfPickup { get; }

    public bool AllowServerDropPod { get; }
}

public sealed class TradeOfferSelection
{
    public TradeOfferSelection(Thing thing, int count)
    {
        Thing = thing;
        Count = count;
    }

    public Thing Thing { get; }

    public int Count { get; set; }
}
