using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Caravans;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Visuals;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Support;

[HarmonyPatch(typeof(Caravan), nameof(Caravan.GetGizmos))]
public static class SupportCaravanGizmoPatches
{
    private sealed record Binding(ClientSessionLifecycle Session, ClientSupportActions Actions, ClientWorldMarkers Markers);
    private static Binding? binding;
    internal static void Attach(ClientSessionLifecycle session, ClientSupportActions actions, ClientWorldMarkers markers) => binding = new(session, actions, markers);

    public static void Postfix(Caravan __instance, ref IEnumerable<Gizmo> __result)
    {
        var current = binding;
        if (current is null) return;
        var scope = ClientCaravanGizmoContext.Capture(current.Session, __instance, () => ReferenceEquals(binding, current));
        if (scope is null) return;
        IReadOnlyList<ModWorldMapMarkerDto> targets = System.Array.Empty<ModWorldMapMarkerDto>();
        Pawn[] pawns = System.Array.Empty<Pawn>();
        if (!scope.Step(() => targets = ClientCaravanGizmoContext.FindTargets(current.Markers, __instance, ClientColonyTargetKind.Reinforcement))
            || targets.Count == 0 || !scope.Step(() => pawns = ClientCaravanGizmoContext.OwnedPawns(__instance)) || pawns.Length == 0) return;
        scope.Append(ref __result, targets.Count, "ClashOfRim.Support.CaravanGizmo", () => ClashCommandIcons.SendSupport,
            () => current.Actions.OpenMenu(__instance, targets, pawns.Where(p => ClientCaravanGizmoContext.OwnsPawn(__instance, p)).ToArray()),
            () => pawns.Any(p => ClientCaravanGizmoContext.OwnsPawn(__instance, p)));
    }
}
