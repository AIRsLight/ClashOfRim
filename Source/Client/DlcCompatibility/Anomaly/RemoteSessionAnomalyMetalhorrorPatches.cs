using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.RemoteMaps;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

[HarmonyPatch(typeof(MetalhorrorUtility), nameof(MetalhorrorUtility.Infect))]
public static class RemoteSessionAnomalyMetalhorrorInfectPatch
{
    public static bool Prefix(Pawn pawn)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(pawn);
    }
}

[HarmonyPatch(typeof(MetalhorrorUtility), nameof(MetalhorrorUtility.Detect))]
public static class RemoteSessionAnomalyMetalhorrorDetectPatch
{
    public static bool Prefix(Pawn infected)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(infected);
    }
}
