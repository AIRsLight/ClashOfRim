using System.Collections.Generic;
using AIRsLight.ClashOfRim.Caravans;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Visuals;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

[HarmonyPatch(typeof(Caravan), nameof(Caravan.GetGizmos))]
public static class TradeCaravanGizmoPatches
{
    private sealed record Binding(ClientSessionLifecycle Session, ClientTradeActions Actions);
    private static Binding? binding;
    internal static void Attach(ClientSessionLifecycle session, ClientTradeActions actions) => binding = new(session, actions);

    public static void Postfix(Caravan __instance, ref IEnumerable<Gizmo> __result)
    {
        var current = binding;
        if (current is null) return;
        var scope = ClientCaravanGizmoContext.Capture(current.Session, __instance, () => ReferenceEquals(binding, current));
        if (scope is null) return;
        IReadOnlyList<ModTradeOrderSummaryDto> orders = System.Array.Empty<ModTradeOrderSummaryDto>();
        if (!scope.Step(() => orders = current.Actions.FindFulfillable(__instance))) return;
        scope.Append(ref __result, orders.Count, "ClashOfRim.Trade.CaravanFulfillGizmo", () => ClashCommandIcons.TradeFulfill,
            () => current.Actions.OpenCaravanMenu(__instance, orders));
    }
}
