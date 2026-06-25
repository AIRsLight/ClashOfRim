using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.RemoteMaps;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

[HarmonyPatch(typeof(Pawn_PsychicEntropyTracker), nameof(Pawn_PsychicEntropyTracker.Notify_Meditated))]
public static class RemoteSessionRoyaltyMeditationNotifyPatch
{
    public static bool Prefix(Pawn_PsychicEntropyTracker __instance)
    {
        if (!RoyaltyPawnReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemotePawn(__instance.Pawn);
    }
}

[HarmonyPatch(typeof(Pawn_PsychicEntropyTracker), nameof(Pawn_PsychicEntropyTracker.GainPsyfocus_NewTemp))]
public static class RemoteSessionRoyaltyPsyfocusGainPatch
{
    public static bool Prefix(Pawn_PsychicEntropyTracker __instance, Thing focus)
    {
        if (!RoyaltyPawnReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemotePawn(__instance.Pawn)
            && !RemoteSessionGlobalStateGuard.IsRemoteThing(focus);
    }
}

[HarmonyPatch(typeof(CompSpawnSubplant), nameof(CompSpawnSubplant.AddProgress))]
public static class RemoteSessionRoyaltyAnimaGrassProgressPatch
{
    public static bool Prefix(CompSpawnSubplant __instance)
    {
        if (!RoyaltyPawnReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(__instance.parent);
    }
}

[HarmonyPatch(typeof(CompPsylinkable), nameof(CompPsylinkable.FinishLinkingRitual))]
public static class RemoteSessionRoyaltyAnimaLinkingPatch
{
    public static bool Prefix(CompPsylinkable __instance, Pawn pawn)
    {
        if (!RoyaltyPawnReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(__instance.parent)
            && !RemoteSessionGlobalStateGuard.IsRemotePawn(pawn);
    }
}

[HarmonyPatch(typeof(CompPsylinkable), "OnGrassGrown")]
public static class RemoteSessionRoyaltyAnimaGrassLetterPatch
{
    public static bool Prefix(CompPsylinkable __instance)
    {
        if (!RoyaltyPawnReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(__instance.parent);
    }
}

[HarmonyPatch(typeof(RoyalTitlePermitWorker_CallLaborers), "CallLaborers")]
public static class RemoteSessionRoyaltyCallLaborersPatch
{
    public static bool Prefix(RoyalTitlePermitWorker_CallLaborers __instance)
    {
        if (!RoyaltyPawnReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        Map? map = Traverse.Create(__instance).Field("map").GetValue<Map>();
        return !RemoteSessionGlobalStateGuard.IsRemoteMap(map);
    }
}

[HarmonyPatch(typeof(PawnUtility), nameof(PawnUtility.ChangePsylinkLevel))]
public static class RemoteSessionRoyaltyPsylinkLevelPatch
{
    public static bool Prefix(Pawn pawn)
    {
        if (!RoyaltyPawnReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemotePawn(pawn);
    }
}
