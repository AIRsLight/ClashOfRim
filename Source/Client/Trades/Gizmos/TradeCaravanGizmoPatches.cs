using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Visuals;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

[HarmonyPatch(typeof(Caravan), nameof(Caravan.GetGizmos))]
public static class TradeCaravanGizmoPatches
{
    public static void Postfix(Caravan __instance, ref IEnumerable<Gizmo> __result)
    {
        if (__instance == null || __instance.Destroyed || !__instance.Spawned || !__instance.IsPlayerControlled)
        {
            return;
        }

        ClashOfRimMod mod = LoadedModManager.GetMod<ClashOfRimMod>();
        IReadOnlyList<ModTradeOrderSummaryDto> orders = mod.FindFulfillableTradeOrdersForCaravan(__instance);
        if (orders.Count == 0)
        {
            return;
        }

        var command = new Command_Action
        {
            defaultLabel = orders.Count == 1
                ? ClashOfRimText.Key("ClashOfRim.Trade.CaravanFulfillGizmo")
                : ClashOfRimText.Key("ClashOfRim.Trade.CaravanFulfillGizmoCount", orders.Count.Named("COUNT")),
            defaultDesc = ClashOfRimText.Key("ClashOfRim.Trade.CaravanFulfillGizmoDesc"),
            icon = ClashCommandIcons.TradeFulfill,
            action = () => mod.OpenCaravanTradeFulfillmentMenu(__instance, orders)
        };

        __result = AppendGizmo(__result, command);
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
