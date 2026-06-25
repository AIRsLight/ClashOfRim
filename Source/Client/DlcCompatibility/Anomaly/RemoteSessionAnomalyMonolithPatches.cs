using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.RemoteMaps;
using HarmonyLib;
using RimWorld;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

[HarmonyPatch(typeof(Building_VoidMonolith), "Tick")]
public static class RemoteSessionAnomalyVoidMonolithTickPatch
{
    public static bool Prefix(Building_VoidMonolith __instance)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(__instance);
    }
}

[HarmonyPatch(typeof(Building_VoidMonolith), nameof(Building_VoidMonolith.Activate))]
public static class RemoteSessionAnomalyVoidMonolithActivatePatch
{
    public static bool Prefix(Building_VoidMonolith __instance)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(__instance);
    }
}

[HarmonyPatch(typeof(Building_VoidMonolith), nameof(Building_VoidMonolith.CheckAndGenerateQuest))]
public static class RemoteSessionAnomalyVoidMonolithQuestPatch
{
    public static bool Prefix(Building_VoidMonolith __instance)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(__instance);
    }
}

[HarmonyPatch(typeof(Building_VoidMonolith), nameof(Building_VoidMonolith.Investigate))]
public static class RemoteSessionAnomalyVoidMonolithInvestigatePatch
{
    public static bool Prefix(Building_VoidMonolith __instance)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(__instance);
    }
}

[HarmonyPatch(typeof(Building_VoidMonolith), nameof(Building_VoidMonolith.OrderForceTarget))]
public static class RemoteSessionAnomalyVoidMonolithOrderPatch
{
    public static bool Prefix(Building_VoidMonolith __instance)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(__instance);
    }
}

[HarmonyPatch(typeof(Building_VoidMonolith), nameof(Building_VoidMonolith.AutoActivate))]
public static class RemoteSessionAnomalyVoidMonolithAutoActivatePatch
{
    public static bool Prefix(Building_VoidMonolith __instance)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(__instance);
    }
}
