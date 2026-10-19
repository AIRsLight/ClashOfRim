using System.Collections.Generic;
using AIRsLight.ClashOfRim.Caravans;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Visuals;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

[HarmonyPatch(typeof(Caravan), nameof(Caravan.GetGizmos))]
public static class RaidCaravanGizmoPatches
{
    private sealed record Binding(ClientSessionLifecycle Session, ClientRaidCommands Commands, ClientWorldMarkers Markers);
    private static Binding? binding;
    internal static void Attach(ClientSessionLifecycle session, ClientRaidCommands commands, ClientWorldMarkers markers) => binding = new(session, commands, markers);

    public static void Postfix(Caravan __instance, ref IEnumerable<Gizmo> __result)
    {
        var current = binding;
        if (current is null) return;
        var scope = ClientCaravanGizmoContext.Capture(current.Session, __instance, () => ReferenceEquals(binding, current));
        if (scope is null) return;
        IReadOnlyList<ModWorldMapMarkerDto> targets = System.Array.Empty<ModWorldMapMarkerDto>();
        if (!scope.Step(() => targets = ClientCaravanGizmoContext.FindTargets(current.Markers, __instance, ClientColonyTargetKind.Raid))
            || !ClientCaravanGizmoContext.HasOwnedPawn(__instance)) return;
        scope.Append(ref __result, targets.Count, "ClashOfRim.Raid.CaravanGizmo", () => ClashCommandIcons.LaunchRaid,
            () => current.Commands.OpenCaravanMenu(ClientRaidCommandSource.ForCaravan(__instance), targets),
            () => ClientCaravanGizmoContext.HasOwnedPawn(__instance));
    }
}
