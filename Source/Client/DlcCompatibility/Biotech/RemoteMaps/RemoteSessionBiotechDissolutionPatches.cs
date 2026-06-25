using AIRsLight.ClashOfRim.RemoteMaps;
using HarmonyLib;
using RimWorld;
using System.Collections;
using System.Reflection;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

[HarmonyPatch(typeof(CompDissolution), nameof(CompDissolution.Notify_AbandonedAtTile))]
public static class RemoteSessionBiotechDissolutionAbandonedPatch
{
    public static bool Prefix()
    {
        if (!RemoteSessionGlobalStateGuard.SuppressRemoteMapRemovalGlobalEffects)
        {
            return true;
        }

        RemoteSessionBiotechDissolutionEffectState.MarkSuppressedAbandonedEffect();

        return false;
    }
}

[HarmonyPatch(typeof(PollutionInfo), nameof(PollutionInfo.MapRemoved))]
public static class RemoteSessionBiotechPollutionInfoRemovedPatch
{
    public static bool Prefix(PollutionInfo __instance)
    {
        return !RemoteSessionGlobalStateGuard.IsRemoteMap(__instance?.map);
    }
}

[HarmonyPatch(typeof(CompDissolutionEffect_Goodwill), nameof(CompDissolutionEffect_Goodwill.DoDissolutionEffectMap))]
public static class RemoteSessionBiotechDissolutionGoodwillMapPatch
{
    public static bool Prefix(CompDissolutionEffect_Goodwill __instance)
    {
        return !RemoteSessionGlobalStateGuard.IsRemoteThing(__instance?.parent);
    }
}

[HarmonyPatch(typeof(CompDissolutionEffect_Pollution), nameof(CompDissolutionEffect_Pollution.DoDissolutionEffectWorld))]
public static class RemoteSessionBiotechDissolutionPollutionWorldPatch
{
    public static bool Prefix(CompDissolutionEffect_Pollution __instance)
    {
        return !RemoteSessionGlobalStateGuard.SuppressRemoteMapRemovalGlobalEffects
            && !RemoteSessionGlobalStateGuard.IsRemoteThing(__instance?.parent);
    }
}

[HarmonyPatch(typeof(CompDissolutionEffect_Goodwill), nameof(CompDissolutionEffect_Goodwill.WorldUpdate))]
public static class RemoteSessionBiotechDissolutionGoodwillWorldUpdatePatch
{
    public static bool Prefix()
    {
        return !RemoteSessionBiotechDissolutionEffectState.ClearPendingRemoteEffectsIfNeeded();
    }
}

[HarmonyPatch(typeof(CompDissolutionEffect_Pollution), nameof(CompDissolutionEffect_Pollution.WorldUpdate))]
public static class RemoteSessionBiotechDissolutionPollutionWorldUpdatePatch
{
    public static bool Prefix()
    {
        return !RemoteSessionBiotechDissolutionEffectState.ClearPendingRemoteEffectsIfNeeded();
    }
}

internal static class RemoteSessionBiotechDissolutionEffectState
{
    private static readonly FieldInfo? PendingGoodwillEventsField =
        AccessTools.Field(typeof(CompDissolutionEffect_Goodwill), "pendingGoodwillEvents");
    private static readonly FieldInfo? PendingWorldEventsField =
        AccessTools.Field(typeof(CompDissolutionEffect_Pollution), "pendingWorldEvents");
    private static int suppressedCount;
    private static bool needsPendingEffectClear;

    public static void MarkSuppressedAbandonedEffect()
    {
        suppressedCount++;
        needsPendingEffectClear = true;
        if (suppressedCount == 1 || suppressedCount % 100 == 0)
        {
            Log.Message("[ClashOfRim][RemoteSession][Biotech] Suppressed remote map dissolution world effects: count="
                + suppressedCount
                + ".");
        }
    }

    public static bool ClearPendingRemoteEffectsIfNeeded()
    {
        if (!needsPendingEffectClear)
        {
            return false;
        }

        needsPendingEffectClear = false;
        int goodwillCount = ClearPendingList(PendingGoodwillEventsField);
        int worldCount = ClearPendingList(PendingWorldEventsField);
        Log.Message("[ClashOfRim][RemoteSession][Biotech] Cleared pending remote map dissolution effects: goodwill="
            + goodwillCount
            + ", world="
            + worldCount
            + ".");
        return true;
    }

    public static void ClearPendingRemoteEffectsAfterSuppression()
    {
        ClearPendingRemoteEffectsIfNeeded();
    }

    private static int ClearPendingList(FieldInfo? field)
    {
        if (field?.GetValue(null) is not IList list)
        {
            return 0;
        }

        int count = list.Count;
        list.Clear();
        return count;
    }
}
