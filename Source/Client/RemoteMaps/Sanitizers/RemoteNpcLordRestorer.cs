using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace AIRsLight.ClashOfRim.RemoteMaps;

internal readonly struct RemoteNpcLordRestoreResult
{
    public RemoteNpcLordRestoreResult(int restoredLords, int restoredPawns, int restoredBuildings)
    {
        RestoredLords = restoredLords;
        RestoredPawns = restoredPawns;
        RestoredBuildings = restoredBuildings;
    }

    public int RestoredLords { get; }

    public int RestoredPawns { get; }

    public int RestoredBuildings { get; }
}

internal static class RemoteNpcLordRestorer
{
    public static RemoteNpcLordRestoreResult Apply(Map map, RemoteSessionMapParent carrier)
    {
        IReadOnlyList<RemoteNpcLordSnapshot> snapshots = carrier.RemoteNpcLordSnapshots;
        if (snapshots.Count == 0)
        {
            return default;
        }

        Dictionary<string, Pawn> pawnsByLoadId = map.mapPawns.AllPawnsSpawned
            .Where(pawn => pawn is not null)
            .GroupBy(pawn => pawn.GetUniqueLoadID(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        Dictionary<string, Thing> thingsByLoadId = (map.listerThings?.AllThings ?? new List<Thing>())
            .Where(thing => thing is not null && !thing.Destroyed)
            .GroupBy(thing => thing.GetUniqueLoadID(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        int restoredLords = 0;
        int restoredPawns = 0;
        int restoredBuildings = 0;
        foreach (RemoteNpcLordSnapshot snapshot in snapshots)
        {
            Faction? faction = ResolveFaction(snapshot.FactionLoadId);
            if (faction is null || faction == Faction.OfPlayer || faction == carrier.Faction)
            {
                continue;
            }

            List<Pawn> pawns = ResolvePawns(snapshot, faction, pawnsByLoadId);
            List<Thing> things = ResolveThings(snapshot.ThingLoadIds, thingsByLoadId);
            List<Building> ownedBuildings = ResolveThings(snapshot.OwnedBuildingLoadIds, thingsByLoadId)
                .OfType<Building>()
                .ToList();
            if (pawns.Count == 0 && things.Count == 0 && ownedBuildings.Count == 0)
            {
                continue;
            }

            LordJob? job = CreateLordJob(snapshot, faction, map, things, thingsByLoadId);
            if (job is null)
            {
                continue;
            }

            foreach (Pawn pawn in pawns)
            {
                pawn.GetLord()?.RemovePawn(pawn);
                pawn.mindState.duty = null;
            }

            Lord lord = LordMaker.MakeNewLord(faction, job, map, pawns);
            foreach (Building building in ownedBuildings)
            {
                if (!lord.ownedBuildings.Contains(building))
                {
                    lord.AddBuilding(building);
                }
            }

            lord.CurLordToil?.UpdateAllDuties();
            restoredLords++;
            restoredPawns += pawns.Count;
            restoredBuildings += ownedBuildings.Count;
        }

        if (restoredLords > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteMapProjection] Restored remote NPC lords: lords="
                + restoredLords
                + ", pawns="
                + restoredPawns
                + ", buildings="
                + restoredBuildings
                + ".");
        }

        return new RemoteNpcLordRestoreResult(restoredLords, restoredPawns, restoredBuildings);
    }

    private static Faction? ResolveFaction(string loadId)
    {
        if (string.IsNullOrWhiteSpace(loadId) || Find.World?.factionManager is null)
        {
            return null;
        }

        return Find.World.factionManager.AllFactionsListForReading
            .FirstOrDefault(faction => string.Equals(faction.GetUniqueLoadID(), loadId, StringComparison.Ordinal));
    }

    private static List<Pawn> ResolvePawns(
        RemoteNpcLordSnapshot snapshot,
        Faction faction,
        IReadOnlyDictionary<string, Pawn> pawnsByLoadId)
    {
        var result = new List<Pawn>();
        foreach (string loadId in snapshot.PawnLoadIds)
        {
            if (!pawnsByLoadId.TryGetValue(loadId, out Pawn pawn)
                || pawn is not { Spawned: true, Dead: false }
                || pawn.Faction != faction
                || pawn.mindState is null)
            {
                continue;
            }

            result.Add(pawn);
        }

        return result;
    }

    private static List<Thing> ResolveThings(
        IEnumerable<string> loadIds,
        IReadOnlyDictionary<string, Thing> thingsByLoadId)
    {
        var result = new List<Thing>();
        foreach (string loadId in loadIds)
        {
            if (thingsByLoadId.TryGetValue(loadId, out Thing thing) && thing is { Destroyed: false })
            {
                result.Add(thing);
            }
        }

        return result;
    }

    private static LordJob? CreateLordJob(
        RemoteNpcLordSnapshot snapshot,
        Faction faction,
        Map map,
        List<Thing> things,
        IReadOnlyDictionary<string, Thing> thingsByLoadId)
    {
        switch (snapshot.Kind)
        {
            case RemoteNpcLordJobKind.AssaultColony:
                return new LordJob_AssaultColony(
                    faction,
                    snapshot.CanKidnap,
                    snapshot.CanTimeoutOrFlee,
                    snapshot.Sappers,
                    snapshot.UseAvoidGridSmart,
                    snapshot.CanSteal,
                    snapshot.Breachers,
                    snapshot.CanPickUpOpportunisticWeapons);
            case RemoteNpcLordJobKind.StageThenAttack:
                return new LordJob_StageThenAttack(
                    faction,
                    ParseCell(snapshot.StageLoc, map.Center),
                    snapshot.RaidSeed,
                    snapshot.CanTimeoutFlee,
                    snapshot.CanKidnap,
                    snapshot.CanSteal,
                    new IntRange(snapshot.DelayMin, snapshot.DelayMax));
            case RemoteNpcLordJobKind.SleepThenAssaultColony:
                return new LordJob_SleepThenAssaultColony(faction, snapshot.SendWokenUpMessage)
                {
                    awakeOnClamor = snapshot.AwakeOnClamor
                };
            case RemoteNpcLordJobKind.MechanoidsDefend:
                if (things.Count == 0)
                {
                    return null;
                }

                return AddDefeatNotifications(
                    new LordJob_MechanoidsDefend(
                        things,
                        faction,
                        snapshot.DefendRadius,
                        ParseCell(snapshot.DefSpot, map.Center),
                        snapshot.CanAssaultColony,
                        snapshot.IsMechCluster),
                    snapshot,
                    thingsByLoadId);
            case RemoteNpcLordJobKind.SleepThenMechanoidsDefend:
                if (things.Count == 0)
                {
                    return null;
                }

                return AddDefeatNotifications(
                    new LordJob_SleepThenMechanoidsDefend(
                        things,
                        faction,
                        snapshot.DefendRadius,
                        ParseCell(snapshot.DefSpot, map.Center),
                        snapshot.CanAssaultColony,
                        snapshot.IsMechCluster)
                    {
                        awakeOnClamor = snapshot.AwakeOnClamor
                    },
                    snapshot,
                    thingsByLoadId);
            default:
                return null;
        }
    }

    private static LordJob AddDefeatNotifications(
        LordJob_MechanoidDefendBase job,
        RemoteNpcLordSnapshot snapshot,
        IReadOnlyDictionary<string, Thing> thingsByLoadId)
    {
        foreach (Thing thing in ResolveThings(snapshot.ThingsToNotifyOnDefeatLoadIds, thingsByLoadId))
        {
            job.AddThingToNotifyOnDefeat(thing);
        }

        return job;
    }

    private static IntVec3 ParseCell(string value, IntVec3 fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            IntVec3 parsed = IntVec3.FromString(value);
            return parsed.IsValid ? parsed : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
