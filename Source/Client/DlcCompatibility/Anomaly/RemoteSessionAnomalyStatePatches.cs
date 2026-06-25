using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.RemoteMaps;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

[HarmonyPatch(typeof(StudyManager), nameof(StudyManager.UpdateStudiableCache))]
public static class RemoteSessionAnomalyStudyCachePatch
{
    public static bool Prefix(Map map)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteMap(map);
    }
}

[HarmonyPatch(typeof(StudyManager), nameof(StudyManager.GetStudiableThingsAndPlatforms))]
public static class RemoteSessionAnomalyStudyCacheReadPatch
{
    public static bool Prefix(Map map, ref HashSet<Thing> __result)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        if (!RemoteSessionGlobalStateGuard.IsRemoteMap(map))
        {
            return true;
        }

        __result = new HashSet<Thing>();
        return false;
    }
}

[HarmonyPatch(typeof(CompStudiable), nameof(CompStudiable.CurrentlyStudiable))]
public static class RemoteSessionAnomalyCurrentlyStudiablePatch
{
    public static void Postfix(CompStudiable __instance, ref bool __result)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return;
        }

        if (RemoteSessionGlobalStateGuard.IsRemoteThing(__instance.parent))
        {
            __result = false;
        }
    }
}

[HarmonyPatch(typeof(CompStudiable), nameof(CompStudiable.Study))]
public static class RemoteSessionAnomalyStudyPatch
{
    public static bool Prefix(CompStudiable __instance)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(__instance.parent);
    }
}

[HarmonyPatch(typeof(AnomalyUtility), nameof(AnomalyUtility.ShouldNotifyCodex))]
public static class RemoteSessionAnomalyCodexNotificationPatch
{
    public static bool Prefix(Thing thing, ref List<EntityCodexEntryDef>? entries, ref bool __result)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        if (!RemoteSessionGlobalStateGuard.IsRemoteThing(thing))
        {
            return true;
        }

        entries = null;
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(AnomalyUtility), nameof(AnomalyUtility.OpenCodexGizmo))]
public static class RemoteSessionAnomalyCodexGizmoPatch
{
    public static bool Prefix(Thing thing, ref Gizmo? __result)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        if (!RemoteSessionGlobalStateGuard.IsRemoteThing(thing))
        {
            return true;
        }

        __result = null;
        return false;
    }
}

[HarmonyPatch(typeof(EntityCodex), nameof(EntityCodex.SetDiscovered), new[] { typeof(List<EntityCodexEntryDef>), typeof(ThingDef), typeof(Thing) })]
public static class RemoteSessionAnomalyCodexListDiscoveryPatch
{
    public static bool Prefix(Thing discoveredThing)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(discoveredThing);
    }
}

[HarmonyPatch(typeof(EntityCodex), nameof(EntityCodex.SetDiscovered), new[] { typeof(EntityCodexEntryDef), typeof(ThingDef), typeof(Thing) })]
public static class RemoteSessionAnomalyCodexDiscoveryPatch
{
    public static bool Prefix(Thing discoveredThing)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(discoveredThing);
    }
}

[HarmonyPatch(typeof(GameComponent_Anomaly), nameof(GameComponent_Anomaly.RegisterUnnaturalCorpse))]
public static class RemoteSessionAnomalyUnnaturalCorpsePatch
{
    public static bool Prefix(Pawn pawn, UnnaturalCorpse corpse)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(pawn)
            && !RemoteSessionGlobalStateGuard.IsRemoteThing(corpse);
    }
}

[HarmonyPatch(typeof(GameComponent_Anomaly), nameof(GameComponent_Anomaly.Notify_MapRemoved))]
public static class RemoteSessionAnomalyMapRemovedPatch
{
    public static bool Prefix(Map map)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteMap(map);
    }
}

[HarmonyPatch(typeof(Building_HoldingPlatform), nameof(Building_HoldingPlatform.SpawnSetup))]
public static class RemoteSessionAnomalyHoldingPlatformSpawnPatch
{
    public static void Prefix(out bool __state)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            __state = false;
            return;
        }

        __state = Find.Anomaly?.hasBuiltHoldingPlatform ?? false;
    }

    public static void Postfix(Building_HoldingPlatform __instance, bool __state)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return;
        }

        if (RemoteSessionGlobalStateGuard.IsRemoteThing(__instance) && Find.Anomaly is not null)
        {
            Find.Anomaly.hasBuiltHoldingPlatform = __state;
        }
    }
}

[HarmonyPatch(typeof(FilthGrayFleshNoticeable), "Tick")]
public static class RemoteSessionAnomalyGrayFleshNoticePatch
{
    public static bool Prefix(FilthGrayFleshNoticeable __instance)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        return !RemoteSessionGlobalStateGuard.IsRemoteThing(__instance);
    }
}

[HarmonyPatch(typeof(MapPortal), nameof(MapPortal.IsEnterable))]
public static class RemoteSessionAnomalyMapPortalEnterablePatch
{
    public static void Postfix(MapPortal __instance, ref string reason, ref bool __result)
    {
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return;
        }

        if (!RemoteSessionGlobalStateGuard.IsRemoteThing(__instance))
        {
            return;
        }

        __result = false;
        reason = ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.PocketMapBlocked");
    }
}

[HarmonyPatch(typeof(PocketMapUtility), nameof(PocketMapUtility.GeneratePocketMap))]
public static class RemoteSessionAnomalyGeneratePocketMapPatch
{
    public static bool Prefix(
        Map sourceMap,
        ref Map __result,
        out bool __state)
    {
        __state = false;
        if (!AnomalyTradeThingReferenceCompatibility.HasRemoteStateGuards)
        {
            return true;
        }

        if (!RemoteSessionPocketMapUtility.IsRemoteSessionSourceMap(sourceMap))
        {
            return true;
        }

        __state = true;
        __result = sourceMap;
        Messages.Message(
            ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.PocketMapBlocked"),
            MessageTypeDefOf.RejectInput,
            historical: false);
        return false;
    }

    public static void Finalizer(bool __state)
    {
        if (__state)
        {
            PocketMapUtility.currentlyGeneratingPortal = null;
        }
    }
}
