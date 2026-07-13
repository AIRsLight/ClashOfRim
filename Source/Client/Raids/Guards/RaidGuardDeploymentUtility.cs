using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace AIRsLight.ClashOfRim.Raids;

internal static class RaidGuardDeploymentUtility
{
    private const string GuardFactionDefName = "ClashOfRim_MercenaryGuards";

    public static void DeployIfNeeded(Map map, Faction defenderFaction, ActiveRaidBattleSession session)
    {
        if (map is null
            || defenderFaction is null
            || session is null
            || string.IsNullOrWhiteSpace(session.GuardDeploymentId)
            || session.GuardDeploymentPoints <= 0)
        {
            return;
        }

        try
        {
            Faction? guardFaction = EnsureGuardFaction();
            if (guardFaction is null)
            {
                Log.Warning("[ClashOfRim][RaidGuard] Cannot deploy guard team because the guard faction is unavailable.");
                return;
            }

            SetGuardRelations(guardFaction, defenderFaction);
            List<Pawn> guards = GenerateGuards(map, guardFaction, session.GuardDeploymentPoints, session.GuardDeploymentSeed);
            if (guards.Count == 0)
            {
                Log.Warning("[ClashOfRim][RaidGuard] Guard pawn generation returned no pawns.");
                return;
            }

            LordMaker.MakeNewLord(
                guardFaction,
                new LordJob_AssaultColony(
                    guardFaction,
                    canKidnap: false,
                    canTimeoutOrFlee: false,
                    sappers: false,
                    useAvoidGridSmart: false,
                    canSteal: false,
                    breachers: false,
                    canPickUpOpportunisticWeapons: false),
                map,
                guards);
            IntVec3 dropCenter = ResolveDropCenter(map);
            DropPodUtility.DropThingsNear(
                dropCenter,
                map,
                guards.Cast<Thing>(),
                openDelay: 110,
                canInstaDropDuringInit: false,
                leaveSlag: false,
                canRoofPunch: true,
                forbid: false,
                allowFogged: true,
                faction: guardFaction);

            ClashLog.Message("[ClashOfRim][RaidGuard] Deployed guard team: contract="
                + session.GuardDeploymentId
                + ", tier="
                + session.GuardDeploymentTier
                + ", points="
                + session.GuardDeploymentPoints
                + ", pawns="
                + guards.Count
                + ", dropCenter="
                + dropCenter
                + ".");
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][RaidGuard] Guard deployment failed: " + ex);
        }
    }

    private static IntVec3 ResolveDropCenter(Map map)
    {
        if (DropCellFinder.TryFindDropSpotNear(
                map.Center,
                map,
                out IntVec3 dropCenter,
                allowFogged: true,
                canRoofPunch: true,
                maxRadius: 48,
                allowIndoors: true,
                size: null,
                mustBeReachableFromCenter: false))
        {
            return dropCenter;
        }

        return DropCellFinder.RandomDropSpot(map);
    }

    private static List<Pawn> GenerateGuards(Map map, Faction faction, int points, int seed)
    {
        var parms = new PawnGroupMakerParms
        {
            groupKind = PawnGroupKindDefOf.Combat,
            faction = faction,
            tile = map.Tile,
            points = Math.Max(35, points),
            inhabitants = false,
            generateFightersOnly = true,
            seed = seed
        };
        return PawnGroupMakerUtility.GeneratePawns(parms, warnOnZeroResults: true)
            .Where(pawn => pawn is not null && !pawn.Dead && !pawn.Destroyed)
            .ToList();
    }

    private static Faction? EnsureGuardFaction()
    {
        if (Find.World?.factionManager is null)
        {
            return null;
        }

        Faction? existing = Find.World.factionManager.AllFactionsListForReading.FirstOrDefault(faction =>
            string.Equals(faction.def?.defName, GuardFactionDefName, StringComparison.Ordinal));
        if (existing is not null)
        {
            existing.hidden = true;
            return existing;
        }

        FactionDef? factionDef = DefDatabase<FactionDef>.GetNamedSilentFail(GuardFactionDefName);
        if (factionDef is null)
        {
            return null;
        }

        Faction faction = new()
        {
            def = factionDef,
            loadID = Find.UniqueIDsManager.GetNextFactionID(),
            temporary = true,
            hidden = true,
            allowGoodwillRewards = false,
            allowRoyalFavorRewards = false,
            color = new Color(0.78f, 0.66f, 0.32f)
        };
        faction.Name = ClashOfRimText.Key("ClashOfRim.Mercenary.GuardFactionName");
        foreach (Faction other in Find.World.factionManager.AllFactionsListForReading.ToList())
        {
            faction.SetRelation(new FactionRelation(other, FactionRelationKind.Neutral));
            other.SetRelation(new FactionRelation(faction, FactionRelationKind.Neutral));
        }

        Find.World.factionManager.Add(faction);
        return faction;
    }

    private static void SetGuardRelations(Faction guardFaction, Faction defenderFaction)
    {
        guardFaction.SetRelationDirect(defenderFaction, FactionRelationKind.Ally, canSendHostilityLetter: false);
        defenderFaction.SetRelationDirect(guardFaction, FactionRelationKind.Ally, canSendHostilityLetter: false);
        if (Faction.OfPlayer is not null)
        {
            guardFaction.SetRelationDirect(Faction.OfPlayer, FactionRelationKind.Hostile, canSendHostilityLetter: false);
            Faction.OfPlayer.SetRelationDirect(guardFaction, FactionRelationKind.Hostile, canSendHostilityLetter: false);
        }
    }
}
