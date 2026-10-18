using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal interface IRaidAttackForceSource
{
    bool IsAvailable { get; }
    int Tile { get; }
    int TileLayerId { get; }

    string UniqueLoadId { get; }

    IReadOnlyList<Pawn> AttackPawns { get; }

    IReadOnlyList<ModThingReferenceDto> BuildCarriedThings(string userId, string colonyId, string snapshotId);

    RaidCaravanMapEntryResult TryEnterMapViaCompatibility(Map map, IReadOnlyList<Pawn> pawns);

    bool TryPlaceAttackersOnMap(
        ActiveRaidBattleSession session,
        Map map,
        IReadOnlyList<RaidAttackPawnLoad> attackPawns,
        out string failureReason);

    void CleanupAfterSuccessfulEntry();
}

internal sealed record RaidAttackPawnLoad(string GlobalKey, Pawn Pawn);
