using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public static class RemoteObservationMapPostProcessor
{
    public static void Apply(Map map, RemoteSessionMapParent carrier, string scope, ModSnapshotPackageMetadataDto package)
    {
        RemoteMapSessionPolicy policy = RemoteMapSessionPolicy.ForMode(scope);
        RemoteSessionMapAreaSanitizerResult areaResult = RemoteSessionMapAreaSanitizer.Apply(map);
        RemoteSessionMapConstructionSanitizerResult constructionResult = RemoteSessionMapConstructionSanitizer.Apply(map);
        RemoteSessionPawnStateSanitizerResult pawnStateResult = RemoteSessionPawnStateSanitizer.DropLoadedCarriedThings(map);
        int forbiddenLoadedThings = RemoteSessionMapThingAccessSanitizer.ForbidLoadedHaulables(map);
        RaidDefenderMapConversionResult defenderConversion = default;
        int scoutResidentPawns = 0;
        if (policy.HideEnemyTraps || policy.EnableDefensePointAi)
        {
            Faction? defenderFaction = PlayerFactionProxyUtility.EnsureProxyForUser(carrier.OwnerUserId);
            if (defenderFaction is not null)
            {
                defenderFaction.SetRelationDirect(Faction.OfPlayer, FactionRelationKind.Hostile, canSendHostilityLetter: false);
                Faction.OfPlayer?.SetRelationDirect(defenderFaction, FactionRelationKind.Hostile, canSendHostilityLetter: false);
                defenderConversion = RaidDefenderMapUtility.ConvertPlayerOwnedMapObjectsToDefenderProxy(map, defenderFaction);
                if (policy.EnableDefensePointAi)
                {
                    DefensePointRaidAiApplicator.Apply(map, defenderFaction);
                }
                else if (string.Equals(scope, RemoteSessionMapParent.ScoutMode, StringComparison.OrdinalIgnoreCase))
                {
                    scoutResidentPawns = ApplyScoutResidentLords(map, defenderFaction);
                }

                ClashOfRimCompatibilityApi.NotifyRemoteDefenderMapPrepared(map, defenderFaction);
            }
        }

        int hiddenThingCount = 0;
        if (policy.HideEnemyTraps)
        {
            hiddenThingCount = ApplyHiddenTacticalObjectSession(map, carrier, package);
        }

        int fogRevealRoots = 0;
        if (policy.HideEnemyTraps)
        {
            fogRevealRoots = ApplyEdgeReachableFog(map);
        }

        int friendlyResidentPawns = 0;
        if (string.Equals(scope, RemoteSessionMapParent.FriendlyObservationMode, StringComparison.OrdinalIgnoreCase))
        {
            friendlyResidentPawns = ApplyFriendlyObservationResidentLords(map, carrier);
        }

        RemoteNpcLordRestoreResult remoteNpcLordResult = RemoteNpcLordRestorer.Apply(map, carrier);
        ClashOfRimCompatibilityApi.NotifyRemoteMapLoaded(map, carrier, scope, package);

        ClashLog.Message(
            "[ClashOfRim][Observation] Applied observation map rules: scope="
            + scope
            + ", target="
            + carrier.OwnerUserId
            + "/"
            + carrier.OwnerColonyId
            + ", hiddenTacticalObjects="
            + hiddenThingCount
            + ", removedZones="
            + areaResult.RemovedZones
            + ", clearedAreas="
            + areaResult.ClearedAreas
            + ", removedConstructionPlaceholders="
            + constructionResult.RemovedPlaceholders
            + ", removedConstructionDesignations="
            + constructionResult.RemovedDesignations
            + ", carriedThingsDropped="
            + pawnStateResult.DroppedThings
            + ", carriedThingJobsInterrupted="
            + pawnStateResult.InterruptedJobs
            + ", carriedThingsDestroyed="
            + pawnStateResult.DestroyedThings
            + ", forbiddenLoadedThings="
            + forbiddenLoadedThings
            + ", defenderPawns="
            + defenderConversion.ConvertedPawns
            + ", scoutResidentPawns="
            + scoutResidentPawns
            + ", defenderThings="
            + defenderConversion.ConvertedThings
            + ", repairablesRefreshed="
            + defenderConversion.RepairableBuildingsRefreshed
            + ", fogRevealRoots="
            + fogRevealRoots
            + ", friendlyResidentPawns="
            + friendlyResidentPawns
            + ", restoredRemoteNpcLords="
            + remoteNpcLordResult.RestoredLords
            + ", restoredRemoteNpcLordPawns="
            + remoteNpcLordResult.RestoredPawns
            + ", restoredRemoteNpcLordBuildings="
            + remoteNpcLordResult.RestoredBuildings
            + ", map="
            + map.GetUniqueLoadID());
    }

    private static int ApplyScoutResidentLords(Map map, Faction defenderFaction)
    {
        List<Pawn> residentPawns = map.mapPawns.PawnsInFaction(defenderFaction)
            .Where(IsScoutResident)
            .ToList();
        if (residentPawns.Count == 0)
        {
            return 0;
        }

        foreach (Pawn pawn in residentPawns)
        {
            pawn.GetLord()?.RemovePawn(pawn);
            if (pawn.CurJob is not null)
            {
                pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced, startNewJob: true, canReturnToPool: true);
            }

            pawn.pather?.StopDead();
            pawn.mindState.duty = null;
            pawn.mindState.enemyTarget = null;
            pawn.mindState.meleeThreat = null;
        }

        Lord lord = LordMaker.MakeNewLord(
            defenderFaction,
            new LordJob_DefendBase(defenderFaction, map.Center, 180000, attackWhenPlayerBecameEnemy: false),
            map,
            residentPawns);
        lord.CurLordToil?.UpdateAllDuties();
        return residentPawns.Count;
    }

    private static bool IsScoutResident(Pawn pawn)
    {
        if (pawn is not { Spawned: true, Dead: false } || pawn.mindState is null)
        {
            return false;
        }

        return pawn.RaceProps?.Humanlike == true || pawn.RaceProps?.IsMechanoid == true;
    }

    private static int ApplyFriendlyObservationResidentLords(Map map, RemoteSessionMapParent carrier)
    {
        Faction? ownerFaction = carrier.Faction ?? PlayerFactionProxyUtility.EnsureProxyForUser(carrier.OwnerUserId);
        if (ownerFaction is null)
        {
            return 0;
        }

        ownerFaction.SetRelationDirect(Faction.OfPlayer, FactionRelationKind.Ally, canSendHostilityLetter: false);
        Faction.OfPlayer?.SetRelationDirect(ownerFaction, FactionRelationKind.Ally, canSendHostilityLetter: false);

        List<Pawn> residentPawns = map.mapPawns.PawnsInFaction(ownerFaction)
            .Where(IsFriendlyObservationResident)
            .ToList();
        if (residentPawns.Count == 0)
        {
            return 0;
        }

        foreach (Pawn pawn in residentPawns)
        {
            pawn.GetLord()?.RemovePawn(pawn);
            pawn.mindState.duty = null;
        }

        Lord lord = LordMaker.MakeNewLord(
            ownerFaction,
            new LordJob_DefendBase(ownerFaction, map.Center, 180000, attackWhenPlayerBecameEnemy: false),
            map,
            residentPawns);
        lord.CurLordToil?.UpdateAllDuties();

        return residentPawns.Count;
    }

    private static bool IsFriendlyObservationResident(Pawn pawn)
    {
        return pawn is { Spawned: true, Dead: false }
            && pawn.RaceProps?.Humanlike == true
            && pawn.mindState is not null;
    }

    private static int ApplyHiddenTacticalObjectSession(
        Map map,
        RemoteSessionMapParent carrier,
        ModSnapshotPackageMetadataDto package)
    {
        List<Thing> allThings = map.listerThings?.AllThings ?? new List<Thing>();
        List<Thing> hiddenThings = allThings
            .Where(IsHiddenTacticalObject)
            .ToList();
        int trapCount = hiddenThings.Count(IsHiddenTrap);
        int defensePointCount = hiddenThings.Count(IsDefensePoint);
        int proxiedTrapCount = RaidHiddenTrapProxyManager.ReplaceHiddenTraps(
            map,
            hiddenThings.Where(IsHiddenTrap));

        RaidTrapVisibilityController.ApplyHiddenThingSession(
            "observation:" + carrier.SessionId,
            package.SnapshotId ?? carrier.SourceSnapshotId,
            map,
            hiddenThings.Where(IsDefensePoint));

        ClashLog.Message("[ClashOfRim][Observation] Hidden tactical object sample: traps="
            + trapCount
            + ", defensePoints="
            + defensePointCount
            + ", trapProxies="
            + proxiedTrapCount
            + ", defs="
            + FormatDefSample(hiddenThings));

        return hiddenThings.Count;
    }

    private static bool IsHiddenTacticalObject(Thing thing)
    {
        return IsHiddenTrap(thing) || IsDefensePoint(thing);
    }

    private static bool IsDefensePoint(Thing thing)
    {
        return thing is Building_ClashDefensePoint || DefensePointUtility.IsDefensePointDef(thing.def?.defName);
    }

    private static bool IsHiddenTrap(Thing thing)
    {
        if (thing is null)
        {
            return false;
        }

        ThingDef? def = thing.def;
        if (thing is Building_Trap)
        {
            return true;
        }

        if (def?.building?.isTrap == true)
        {
            return true;
        }

        Type? thingClass = def?.thingClass;
        return ContainsTrapMarker(def?.defName)
            || ContainsTrapMarker(def?.label)
            || ContainsTrapMarker(thingClass?.FullName);
    }

    private static bool ContainsTrapMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value!.IndexOf("trap", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("landmine", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("ied", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string FormatDefSample(IEnumerable<Thing> things)
    {
        List<string> sample = things
            .Where(thing => thing?.def is not null)
            .GroupBy(thing => thing.def.defName)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Take(12)
            .Select(group => group.Key + ":" + group.Count())
            .ToList();
        return sample.Count == 0 ? "none" : string.Join(",", sample);
    }

    private static int ApplyEdgeReachableFog(Map map)
    {
        if (ShouldUseSpaceFog(map))
        {
            return ApplySpaceFog(map);
        }

        map.fogGrid.Refog(CellRect.WholeMap(map));

        int revealRoots = 0;
        var processedDistricts = new HashSet<District>();
        TraverseParms edgeTraversal = TraverseParms.For(
            TraverseMode.NoPassClosedDoorsOrWater,
            Danger.Deadly,
            false,
            false,
            false,
            true,
            false);

        foreach (Region region in map.regionGrid.AllRegions.ToList())
        {
            District? district = region.District;
            if (district is null || !district.Passable || !processedDistricts.Add(district))
            {
                continue;
            }

            if (!TryFindOutdoorEdgeReachableRoot(district, map, edgeTraversal, out IntVec3 root))
            {
                continue;
            }

            FloodFillerFog.FloodUnfog(root, map);
            map.fogGrid.Unfog(root);
            revealRoots++;
        }

        return revealRoots;
    }

    private static bool ShouldUseSpaceFog(Map map)
    {
        if (map.OrbitalDebris is not null)
        {
            return true;
        }

        string? generatorDefName = map.generatorDef?.defName;
        if (LooksLikeSpaceDefName(generatorDefName))
        {
            return true;
        }

        string? parentDefName = map.Parent?.def?.defName;
        if (map.Parent?.def?.fullyExpandedInSpace == true || LooksLikeSpaceDefName(parentDefName))
        {
            return true;
        }

        return HasSpaceTerrainOnEdge(map);
    }

    private static bool LooksLikeSpaceDefName(string? defName)
    {
        if (string.IsNullOrWhiteSpace(defName))
        {
            return false;
        }

        return defName!.IndexOf("Space", StringComparison.OrdinalIgnoreCase) >= 0
            || defName.IndexOf("Asteroid", StringComparison.OrdinalIgnoreCase) >= 0
            || defName.IndexOf("Orbital", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool HasSpaceTerrainOnEdge(Map map)
    {
        TerrainDef? spaceTerrain = TerrainDefOf.Space;
        if (spaceTerrain is null)
        {
            return false;
        }

        int sampled = 0;
        int space = 0;
        CellRect bounds = map.BoundsRect(0);
        foreach (IntVec3 cell in bounds.EdgeCells)
        {
            sampled++;
            if (cell.GetTerrain(map) == spaceTerrain)
            {
                space++;
            }
        }

        return sampled > 0 && space * 2 >= sampled;
    }

    private static int ApplySpaceFog(Map map)
    {
        map.fogGrid.Refog(CellRect.WholeMap(map));

        int revealRoots = 0;
        foreach (IntVec3 root in map.BoundsRect(0).Corners)
        {
            if (!IsSpaceFogRoot(root, map))
            {
                continue;
            }

            FloodFillerFog.FloodUnfog(root, map);
            map.fogGrid.Unfog(root);
            revealRoots++;
        }

        if (revealRoots > 0)
        {
            return revealRoots;
        }

        foreach (IntVec3 root in map.BoundsRect(0).EdgeCells)
        {
            if (!IsSpaceFogRoot(root, map))
            {
                continue;
            }

            FloodFillerFog.FloodUnfog(root, map);
            map.fogGrid.Unfog(root);
            return 1;
        }

        return 0;
    }

    private static bool IsSpaceFogRoot(IntVec3 cell, Map map)
    {
        return cell.InBounds(map)
            && cell.GetEdifice(map) is null
            && !cell.Roofed(map);
    }

    private static bool TryFindOutdoorEdgeReachableRoot(
        District district,
        Map map,
        TraverseParms edgeTraversal,
        out IntVec3 root)
    {
        if (district.TouchesMapEdge)
        {
            foreach (IntVec3 cell in district.Cells)
            {
                if (IsOutdoorEdgeReachableRoot(cell, map, edgeTraversal))
                {
                    root = cell;
                    return true;
                }
            }
        }

        foreach (IntVec3 cell in district.Cells)
        {
            if (IsOutdoorEdgeReachableRoot(cell, map, edgeTraversal))
            {
                root = cell;
                return true;
            }
        }

        root = IntVec3.Invalid;
        return false;
    }

    private static bool IsOutdoorEdgeReachableRoot(IntVec3 cell, Map map, TraverseParms edgeTraversal)
    {
        return cell.InBounds(map)
            && cell.Standable(map)
            && !cell.Roofed(map)
            && map.reachability.CanReachMapEdge(cell, edgeTraversal);
    }
}
