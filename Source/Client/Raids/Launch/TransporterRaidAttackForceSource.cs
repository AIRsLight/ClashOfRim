using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal sealed class TransporterRaidAttackForceSource : IRaidAttackForceSource
{
    private readonly List<ActiveTransporterInfo> transporters;
    private readonly int tile;
    private readonly string transporterKey;
    private readonly int tileLayerId;

    public TransporterRaidAttackForceSource(
        List<ActiveTransporterInfo> transporters,
        int tile,
        string transporterKey,
        int tileLayerId)
    {
        this.transporters = transporters is null
            ? new List<ActiveTransporterInfo>()
            : transporters.Where(transporter => transporter is not null).ToList();
        this.tile = tile;
        this.transporterKey = string.IsNullOrWhiteSpace(transporterKey)
            ? Guid.NewGuid().ToString("N")
            : transporterKey;
        this.tileLayerId = tileLayerId;
    }

    public bool IsAvailable => transporters.Any(t => t.innerContainer is not null);
    public int Tile => tile;
    public int TileLayerId => tileLayerId;

    public string UniqueLoadId => "transporters:" + transporterKey;

    public IReadOnlyList<Pawn> AttackPawns => EnumeratePayloadThings()
        .OfType<Pawn>()
        .Where(IsAttackPawn)
        .Distinct()
        .ToList();

    public IReadOnlyList<ModThingReferenceDto> BuildCarriedThings(string userId, string colonyId, string snapshotId)
    {
        var references = new List<ModThingReferenceDto>();
        foreach (Thing thing in EnumeratePayloadThings())
        {
            if (thing is Pawn || thing.Destroyed || !TradeThingReferenceUtility.IsTradeableItem(thing))
            {
                continue;
            }

            CompBiocodable? biocodable = thing.TryGetComp<CompBiocodable>();
            references.Add(TradeThingReferenceUtility.BuildThingReference(
                thing,
                $"owner:{userId}/colony:{colonyId}/snapshot:{snapshotId}/transportPods:{transporterKey}/thing:{thing.ThingID}",
                thing.stackCount,
                BuildBiocodedPawnGlobalId(userId, biocodable?.CodedPawn)));
        }

        return references;
    }

    public RaidCaravanMapEntryResult TryEnterMapViaCompatibility(Map map, IReadOnlyList<Pawn> pawns)
    {
        return RaidCaravanMapEntryResult.NotHandled;
    }

    public bool TryPlaceAttackersOnMap(
        ActiveRaidBattleSession session,
        Map map,
        IReadOnlyList<RaidAttackPawnLoad> attackPawns,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (transporters.Count == 0)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Raid.StatusTransporterUnavailable");
            return false;
        }

        IntVec3 entryCell = DropCellFinder.GetBestShuttleLandingSpot(map, Faction.OfPlayer);
        if (!entryCell.IsValid)
        {
            entryCell = PawnExchangePlacementService.FindMapEdgeLandingCell(map);
        }

        var dropPodTransporters = new List<ActiveTransporterInfo>();
        foreach (ActiveTransporterInfo transporter in transporters)
        {
            if (transporter?.innerContainer is null)
            {
                continue;
            }

            if (transporter.GetShuttle() is not null)
            {
                TransportersArrivalActionUtility.DropShuttle(transporter, map, entryCell, null, Faction.OfPlayer);
                continue;
            }

            dropPodTransporters.Add(transporter);
        }

        if (dropPodTransporters.Count > 0)
        {
            TransportersArrivalActionUtility.DropTravellingDropPods(dropPodTransporters, entryCell, map);
        }

        foreach (RaidAttackPawnLoad load in attackPawns)
        {
            Pawn pawn = load.Pawn;
            if (pawn is null || pawn.Dead || pawn.Destroyed || string.IsNullOrWhiteSpace(pawn.ThingID))
            {
                failureReason = ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerPawnUnavailable");
                return false;
            }

            if (!session.AttackPawnThingIds.Contains(pawn.ThingID))
            {
                session.AttackPawnThingIds.Add(pawn.ThingID);
            }
        }

        if (session.AttackPawnThingIds.Count == 0)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Raid.StatusNoPawns");
            return false;
        }

        return true;
    }

    public void CleanupAfterSuccessfulEntry()
    {
    }

    private IEnumerable<Thing> EnumeratePayloadThings()
    {
        foreach (ActiveTransporterInfo transporter in transporters)
        {
            if (transporter?.innerContainer is null)
            {
                continue;
            }

            Thing? shuttle = transporter.GetShuttle();
            for (int index = 0; index < transporter.innerContainer.Count; index++)
            {
                Thing thing = transporter.innerContainer[index];
                if (thing is null || thing.Destroyed || ReferenceEquals(thing, shuttle))
                {
                    continue;
                }

                yield return thing;
            }
        }
    }

    private static bool IsAttackPawn(Pawn pawn)
    {
        return pawn is not null
            && !pawn.Dead
            && !pawn.Destroyed
            && pawn.Faction == Faction.OfPlayer
            && !pawn.IsPrisonerOfColony;
    }

    private static string? BuildBiocodedPawnGlobalId(string userId, Pawn? pawn)
    {
        if (pawn is null || string.IsNullOrWhiteSpace(pawn.ThingID))
        {
            return null;
        }

        return PawnGlobalIdUtility.Build(userId, pawn);
    }
}
