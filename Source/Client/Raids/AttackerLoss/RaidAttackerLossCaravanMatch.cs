using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

public sealed class RaidAttackerLossCaravanMatch
{
    public RaidAttackerLossCaravanMatch(
        Caravan caravan,
        IReadOnlyList<Pawn> matchedPawns,
        IReadOnlyList<Pawn> ownerPawns,
        bool canTriggerVanillaCaravanLostEvent)
    {
        Caravan = caravan;
        MatchedPawns = matchedPawns;
        OwnerPawns = ownerPawns;
        CanTriggerVanillaCaravanLostEvent = canTriggerVanillaCaravanLostEvent;
    }

    public Caravan Caravan { get; }

    public IReadOnlyList<Pawn> MatchedPawns { get; }

    public IReadOnlyList<Pawn> OwnerPawns { get; }

    public bool CanTriggerVanillaCaravanLostEvent { get; }

    public string CaravanLoadId => Caravan.GetUniqueLoadID();
}
