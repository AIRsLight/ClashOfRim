using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

internal static class RemotePocketMapAdmission
{
    internal static bool Allows(MapPortal portal)
    {
        // Native exits return to their entrance/source map; they never generate
        // a child and intentionally have no MapPortal.PocketMap of their own.
        if (portal is PocketMapExit) return true;
        if (!RemoteSessionPocketMapUtility.IsRemoteSessionSourceMap(portal.Map) || portal.PocketMap is not null) return true;
        // Only generators exercised without the source world's quest manager may
        // create a new remote map. Existing imported maps use vanilla admission.
        if (portal.GetType() == typeof(PitGate))
            return portal.def.defName == "PitGate" && portal.def.portal.pocketMapGenerator.defName == "Undercave"
                && portal.Map.listerThings.AllThings.OfType<PitGate>().Count() == 1;
        if (portal.GetType() == typeof(AncientHatch) && portal is AncientHatch hatch)
            return portal.def.defName == "AncientHatch" && portal.def.portal.pocketMapGenerator.defName == "AncientStockpile"
                && Enum.IsDefined(typeof(TileMutatorWorker_Stockpile.StockpileType), hatch.stockpileType)
                && (hatch.layout is null || hatch.layout.defName is "AncientStockpile" or "AncientStockpileFake");
        return false;
    }
}

[HarmonyPatch(typeof(MapPortal), nameof(MapPortal.IsEnterable))]
internal static class RemotePocketMapEnterabilityPatch
{
    internal static void Postfix(MapPortal __instance, ref bool __result, ref string reason)
    {
        if (__result && !RemotePocketMapAdmission.Allows(__instance))
        {
            __result = false;
            reason = ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.PocketMapNotGenerated");
        }
    }
}

[HarmonyPatch(typeof(MapPortal), nameof(MapPortal.GetOtherMap))]
internal static class RemotePocketMapGenerationAdmissionPatch
{
    internal static bool Prefix(MapPortal __instance, ref Map? __result)
    {
        if (RemotePocketMapAdmission.Allows(__instance)) return true;
        __result = null;
        return false;
    }
}

[HarmonyPatch(typeof(MapPortal), "GeneratePocketMap")]
internal static class RemotePocketMapGeneratorContextPatch
{
    internal static void Prefix(out MapPortal? __state) => __state = PocketMapUtility.currentlyGeneratingPortal;
    internal static void Finalizer(MapPortal? __state) => PocketMapUtility.currentlyGeneratingPortal = __state;
}
