using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.RemoteMaps;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

[HarmonyPatch(typeof(Building_GravEngine), nameof(Building_GravEngine.Inspect))]
public static class RemoteSessionOdysseyGravEngineInspectPatch
{
    public static bool Prefix(Building_GravEngine __instance)
    {
        if (!OdysseyCompatibilityPackage.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(__instance);
    }
}

[HarmonyPatch(typeof(Building_GravEngine), nameof(Building_GravEngine.CanLaunch))]
public static class RemoteSessionOdysseyGravEngineLaunchCheckPatch
{
    public static bool Prefix(Building_GravEngine __instance, ref AcceptanceReport __result)
    {
        if (!OdysseyCompatibilityPackage.HasRemoteStateGuards)
        {
            return true;
        }

        if (!RemoteSessionGlobalStateGuard.IsRemoteThing(__instance))
        {
            return true;
        }

        __result = new AcceptanceReport("CannotLaunchGravship".Translate());
        return false;
    }
}

[HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.InitiateTakeoff))]
public static class RemoteSessionOdysseyGravshipTakeoffPatch
{
    public static bool Prefix(Building_GravEngine engine, PlanetTile targetTile)
    {
        if (!OdysseyCompatibilityPackage.HasRemoteStateGuards)
        {
            return true;
        }

        if (RemoteSessionGlobalStateGuard.IsRemoteThing(engine))
        {
            return false;
        }

        try
        {
            ClashOfRimMod mod = LoadedModManager.GetMod<ClashOfRimMod>();
            return mod.TryBeginImplicitColonyRelocationTakeoff(engine.Map, targetTile);
        }
        catch (System.Exception ex)
        {
            Log.Warning("[ClashOfRim][Odyssey] Failed to start gravship relocation atomic section: " + ex);
            return true;
        }
    }
}

[HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.GenerateGravship))]
public static class RemoteSessionOdysseyGenerateGravshipPatch
{
    public static bool Prefix(Building_GravEngine engine, ref Gravship? __result)
    {
        if (!OdysseyCompatibilityPackage.HasRemoteStateGuards)
        {
            return true;
        }

        if (!RemoteSessionGlobalStateGuard.IsRemoteThing(engine))
        {
            return true;
        }

        __result = null;
        return false;
    }
}

[HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.AbandonMap))]
public static class RemoteSessionOdysseyAbandonMapPatch
{
    public static bool Prefix(Map map)
    {
        if (!OdysseyCompatibilityPackage.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteMap(map);
    }
}

[HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.ArriveExistingMap))]
public static class RemoteSessionOdysseyArriveExistingMapPatch
{
    public static bool Prefix(Gravship gravship)
    {
        if (!OdysseyCompatibilityPackage.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteMapParent(Find.WorldObjects.MapParentAt(gravship.destinationTile));
    }
}

[HarmonyPatch(typeof(CompOrbitalScanner), "LocateSignal")]
public static class RemoteSessionOdysseyOrbitalScannerPatch
{
    public static bool Prefix(CompOrbitalScanner __instance)
    {
        if (!OdysseyCompatibilityPackage.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(__instance.parent);
    }
}

[HarmonyPatch(typeof(CompOrbitalScanner), nameof(CompOrbitalScanner.ReceiveSignal))]
public static class RemoteSessionOdysseyOrbitalScannerSignalPatch
{
    public static bool Prefix(CompOrbitalScanner __instance)
    {
        if (!OdysseyCompatibilityPackage.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(__instance.parent);
    }
}

[HarmonyPatch(typeof(CompAncientUplink), nameof(CompAncientUplink.Notify_Hacked))]
public static class RemoteSessionOdysseyAncientUplinkPatch
{
    public static bool Prefix(CompAncientUplink __instance)
    {
        if (!OdysseyCompatibilityPackage.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(__instance.parent);
    }
}
