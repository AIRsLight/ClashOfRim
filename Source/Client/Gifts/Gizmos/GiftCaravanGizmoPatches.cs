using System.Collections.Generic;
using AIRsLight.ClashOfRim.Caravans;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Visuals;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Gifts;

[HarmonyPatch(typeof(Caravan), nameof(Caravan.GetGizmos))]
public static class GiftCaravanGizmoPatches
{
    private sealed record Binding(ClientSessionLifecycle Session, ClientGiftActions Actions);
    private static Binding? binding;
    internal static void Attach(ClientSessionLifecycle session, ClientGiftActions actions) => binding = new(session, actions);

    public static void Postfix(Caravan __instance, ref IEnumerable<Gizmo> __result)
    {
        var current = binding;
        if (current is null) return;
        var scope = ClientCaravanGizmoContext.Capture(current.Session, __instance, () => ReferenceEquals(binding, current));
        if (scope is null || !ClientCaravanGizmoContext.HasItems(__instance)) return;
        for (int mode = 0; mode < 2; mode++)
        {
            bool forced = mode == 1;
            IReadOnlyList<ModWorldMapMarkerDto> targets = System.Array.Empty<ModWorldMapMarkerDto>();
            if (!scope.Step(() => targets = current.Actions.FindTargets(__instance, forced))) return;
            scope.Append(ref __result, targets.Count,
                forced ? "ClashOfRim.GiftDelivery.ForcedCaravanGizmo" : "ClashOfRim.GiftDelivery.CaravanGizmo",
                () => forced ? ClashCommandIcons.ForcedDelivery : ClashCommandIcons.GiftDelivery,
                () => current.Actions.OpenMenu(__instance, targets, forced),
                () => ClientCaravanGizmoContext.HasItems(__instance));
        }
    }
}
