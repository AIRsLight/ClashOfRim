using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal static class ClientRaidBattlefield
{
    internal static bool TryEnter(ActiveRaidBattleSession session, Map map, IRaidAttackForceSource source,
        IReadOnlyList<RaidAttackPawnLoad> pawns, out string failure)
    {
        PrepareDefender(session, map);
        var entry = source.TryEnterMapViaCompatibility(map, pawns.Select(load => load.Pawn).ToList());
        failure = entry.FailureReason ?? string.Empty;
        if (entry.Kind == RaidCaravanMapEntryResultKind.Failed)
        { Log.Warning("[ClashOfRim][Raid] Compatibility handler failed to move attack caravan: " + failure); return false; }
        if (entry.Kind == RaidCaravanMapEntryResultKind.Success) session.AttackPawnThingIds.AddRange(entry.AttackPawnThingIds);
        else if (!source.TryPlaceAttackersOnMap(session, map, pawns, out failure))
        { Log.Warning("[ClashOfRim][Raid] Failed to move attack force: " + failure); return false; }

        source.CleanupAfterSuccessfulEntry();
        ClashOfRimGameComponent.SetActiveRaidBattleSession(session);
        return true;
    }

    private static void PrepareDefender(ActiveRaidBattleSession session, Map map)
    {
        Faction? defender = PlayerFactionProxyUtility.EnsureProxyForUser(session.DefenderUserId);
        if (defender is null) return;
        defender.SetRelationDirect(Faction.OfPlayer, FactionRelationKind.Hostile, canSendHostilityLetter: false);
        Faction.OfPlayer?.SetRelationDirect(defender, FactionRelationKind.Hostile, canSendHostilityLetter: false);
        RemoteSessionMapAreaSanitizer.Apply(map);
        RemoteSessionMapConstructionSanitizer.Apply(map);
        var result = RemoteSessionPawnStateSanitizer.DropLoadedCarriedThings(map);
        RemoteSessionMapThingAccessSanitizer.ForbidLoadedHaulables(map);
        RaidDefenderMapUtility.ConvertPlayerOwnedMapObjectsToDefenderProxy(map, defender);
        RaidDefenderLootProtectionUtility.Apply(map, defender);
        DefensePointRaidAiApplicator.Apply(map, defender);
        ClashOfRimCompatibilityApi.NotifyRemoteDefenderMapPrepared(map, defender);
        RaidGuardDeploymentUtility.DeployIfNeeded(map, defender, session);
        ClashLog.Message("[ClashOfRim][Raid] Prepared defender map pawn state: carriedThingsDropped=" + result.DroppedThings
            + ", carriedThingJobsInterrupted=" + result.InterruptedJobs + ", carriedThingsDestroyed=" + result.DestroyedThings + ".");
    }
}
