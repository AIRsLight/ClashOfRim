using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal sealed class CaravanRaidAttackForceSource : IRaidAttackForceSource
{
    private readonly Caravan caravan;

    public CaravanRaidAttackForceSource(Caravan caravan)
    {
        this.caravan = caravan;
    }

    public bool IsAvailable => !caravan.Destroyed;
    public int Tile => caravan.Tile;
    public int TileLayerId => RemoteWorldObjectUiUtility.ReadTileLayerId(caravan.Tile);

    public string UniqueLoadId => caravan.GetUniqueLoadID();

    public IReadOnlyList<Pawn> AttackPawns => caravan.PawnsListForReading
        .Where(pawn => pawn != null && !pawn.Dead && !pawn.Destroyed && caravan.IsOwner(pawn))
        .ToList();

    public IReadOnlyList<ModThingReferenceDto> BuildCarriedThings(string userId, string colonyId, string snapshotId)
    {
        return TradeCaravanFulfillmentUtility.BuildDeliveredThingReferences(caravan, userId, colonyId, snapshotId);
    }

    public RaidCaravanMapEntryResult TryEnterMapViaCompatibility(Map map, IReadOnlyList<Pawn> pawns)
    {
        return ClashOfRimCompatibilityApi.TryEnterRaidCaravanMap(caravan, map, pawns);
    }

    public bool TryPlaceAttackersOnMap(
        ActiveRaidBattleSession session,
        Map map,
        IReadOnlyList<RaidAttackPawnLoad> attackPawns,
        out string failureReason)
    {
        failureReason = string.Empty;
        IntVec3 attackerEntryCell = PawnExchangePlacementService.FindMapEdgeLandingCell(map);
        int attackerEntryRadius = Math.Min(12, 4 + attackPawns.Count / 3);
        foreach (RaidAttackPawnLoad load in attackPawns)
        {
            if (TryMoveRaidAttackerPawnToMap(
                    caravan,
                    load,
                    map,
                    attackerEntryCell,
                    attackerEntryRadius,
                    out string pawnThingId,
                    out string pawnFailureReason)
                && !string.IsNullOrWhiteSpace(pawnThingId))
            {
                session.AttackPawnThingIds.Add(pawnThingId);
                continue;
            }

            failureReason = ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusAttackForcePlacementFailed",
                load.GlobalKey.Named("PAWN"),
                pawnFailureReason.Named("REASON"));
            return false;
        }

        return true;
    }

    public void CleanupAfterSuccessfulEntry()
    {
        if (caravan.Destroyed)
        {
            return;
        }

        if (caravan.PawnsListForReading.Count > 0)
        {
            return;
        }

        if (Find.WorldObjects?.Contains(caravan) == true)
        {
            Find.WorldObjects.Remove(caravan);
            ClashLog.Message("[ClashOfRim][Raid] Removed empty source caravan after moving all attackers into raid battle.");
        }
    }

    private static bool TryMoveRaidAttackerPawnToMap(
        Caravan sourceCaravan,
        RaidAttackPawnLoad load,
        Map map,
        IntVec3 entryCell,
        int entryRadius,
        out string pawnThingId,
        out string failureReason)
    {
        pawnThingId = string.Empty;
        failureReason = string.Empty;
        Pawn pawn = load.Pawn;
        if (pawn is null || pawn.Dead || pawn.Destroyed)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerPawnUnavailable");
            return false;
        }

        if (!sourceCaravan.ContainsPawn(pawn))
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerPawnNotInCaravan");
            return false;
        }

        sourceCaravan.RemovePawn(pawn);
        if (pawn.Spawned)
        {
            pawn.DeSpawn(DestroyMode.Vanish);
        }

        PawnExchangePlacementService.SpawnNearMapEdge(pawn, map, entryCell, entryRadius);
        if (pawn.Faction != Faction.OfPlayer)
        {
            pawn.SetFaction(Faction.OfPlayer);
        }

        pawnThingId = pawn.ThingID;
        return true;
    }
}
