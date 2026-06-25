using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Visuals;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Gifts;

[HarmonyPatch(typeof(Caravan), nameof(Caravan.GetGizmos))]
public static class GiftCaravanGizmoPatches
{
    public static void Postfix(Caravan __instance, ref IEnumerable<Gizmo> __result)
    {
        if (__instance == null || __instance.Destroyed || !__instance.Spawned || !__instance.IsPlayerControlled)
        {
            return;
        }

        ClashOfRimMod mod = LoadedModManager.GetMod<ClashOfRimMod>();
        bool hasItem = false;
        foreach (Thing thing in CaravanInventoryUtility.AllInventoryItems(__instance))
        {
            if (thing?.def?.category == ThingCategory.Item)
            {
                hasItem = true;
                break;
            }
        }

        if (!hasItem)
        {
            return;
        }

        IReadOnlyList<ModWorldMapMarkerDto> giftTargets = mod.FindGiftDeliverableTargetsForCaravan(__instance);
        if (giftTargets.Count > 0)
        {
            __result = AppendGizmo(__result, new Command_Action
            {
                defaultLabel = giftTargets.Count == 1
                    ? ClashOfRimText.Key("ClashOfRim.GiftDelivery.CaravanGizmo")
                    : ClashOfRimText.Key("ClashOfRim.GiftDelivery.CaravanGizmoCount", giftTargets.Count.Named("COUNT")),
                defaultDesc = ClashOfRimText.Key("ClashOfRim.GiftDelivery.CaravanGizmoDesc"),
                icon = ClashCommandIcons.GiftDelivery,
                action = () => mod.OpenCaravanGiftDeliveryMenu(__instance, giftTargets, forcedDelivery: false)
            });
        }

        IReadOnlyList<ModWorldMapMarkerDto> forcedTargets = mod.FindForcedGiftDeliverableTargetsForCaravan(__instance);
        if (forcedTargets.Count == 0)
        {
            return;
        }

        __result = AppendGizmo(__result, new Command_Action
        {
            defaultLabel = forcedTargets.Count == 1
                ? ClashOfRimText.Key("ClashOfRim.GiftDelivery.ForcedCaravanGizmo")
                : ClashOfRimText.Key("ClashOfRim.GiftDelivery.ForcedCaravanGizmoCount", forcedTargets.Count.Named("COUNT")),
            defaultDesc = ClashOfRimText.Key("ClashOfRim.GiftDelivery.ForcedCaravanGizmoDesc"),
            icon = ClashCommandIcons.ForcedDelivery,
            action = () => mod.OpenCaravanGiftDeliveryMenu(__instance, forcedTargets, forcedDelivery: true)
        });
    }

    private static IEnumerable<Gizmo> AppendGizmo(IEnumerable<Gizmo> source, Gizmo appended)
    {
        foreach (Gizmo gizmo in source)
        {
            yield return gizmo;
        }

        yield return appended;
    }
}
