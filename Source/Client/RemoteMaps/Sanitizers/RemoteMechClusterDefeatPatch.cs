using HarmonyLib;
using RimWorld;

namespace AIRsLight.ClashOfRim.RemoteMaps;

[HarmonyPatch(typeof(LordJob_MechanoidDefendBase), "OnDefeat")]
internal static class RemoteMechClusterDefeatPatch
{
    internal static bool Prefix(LordJob_MechanoidDefendBase __instance, ref bool ___mechClusterDefeated)
    {
        var parent = RemoteSessionPocketMapUtility.FindSessionParent(__instance.lord?.lordManager?.map);
        if (parent is null || (parent.IsRaidBattle && !RemoteSessionGlobalStateGuard.SuppressRemoteMapRemovalGlobalEffects))
            return true;

        // Observation cannot earn a victory; removing a remote map is not combat.
        // Guard both native tick and lord-destruction paths at their common entry,
        // before quest signals, defeat listeners, history, memories or UI effects.
        ___mechClusterDefeated = true;
        return false;
    }
}
