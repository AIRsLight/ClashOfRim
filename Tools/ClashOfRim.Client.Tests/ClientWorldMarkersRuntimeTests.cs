using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.WorldObjects;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

internal static class ClientWorldMarkersRuntimeTests
{
    private static readonly List<(Action Run, Action Discard)> queued = new();
    private static readonly List<(string User, int Count)> applied = new();
    private static int created, removed, failed;
    internal static void Run()
    {
        var game = Current.Game; var creating = Current.CreatingWorld;
        var harmony = new Harmony("ClashOfRim.Tests.WorldMarkersRuntime");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(RemoteRuntimeWorldObjectRegistry), nameof(RemoteRuntimeWorldObjectRegistry.Apply)),
                prefix: new HarmonyMethod(typeof(ClientWorldMarkersRuntimeTests), nameof(Apply)));
            harmony.Patch(AccessTools.Method(typeof(ClientMainThreadDispatcher), nameof(ClientMainThreadDispatcher.Enqueue)),
                prefix: new HarmonyMethod(typeof(ClientWorldMarkersRuntimeTests), nameof(Enqueue)));
            Current.Game = null; Current.CreatingWorld = null;
            var runtime = new ClientWorldMarkersRuntime(); var menu = runtime.Bind();
            Check(menu.IsCurrent && !menu.HasWorldObjects, "Menu unexpectedly owns engine world objects.");
            var live = New<Game>(); var world = New<World>(); live.World = world; Current.Game = live;
            var bound = runtime.Bind(); Check(!menu.IsCurrent && bound.IsCurrent && !bound.HasWorldObjects, "Target did not bind exact Game/World.");
            world.worldObjects = New<WorldObjectsHolder>(); Check(bound.HasWorldObjects, "New holder not visible to bound world.");
            live.World = New<World>(); Check(!bound.IsCurrent, "Replaced world retained projection target."); live.World = world;
            Current.Game = null; Current.CreatingWorld = world; var generating = runtime.Bind();
            Check(generating.IsCurrent && generating.HasWorldObjects, "World generation did not bind CreatingWorld.");
            Current.CreatingWorld = New<World>(); Check(!generating.IsCurrent, "CreatingWorld replacement retained projection target."); Current.CreatingWorld = null;
            Current.Game = New<Game>(); Current.Game.World = world; Check(!bound.IsCurrent, "Replaced game retained projection target."); Current.Game = live;

            var sites = new ClientOccupiedColonySitesTests.Fixture();
            var flow = new ClientWorldMarkers(sites.Session, sites, sites.Cache, runtime, new View());
            flow.Observe(flow.CaptureObservation(), new() { Markers = new() { Marker(0), Marker(7) } });
            Check(queued.Count == 1 && applied.Count == 0, "Projection bypassed the shared dispatcher.");
            queued[0].Run(); Check(applied.Single() == ("alice", 2), "Runtime failed to pass frozen user/catalog to existing registry.");
            var mod = New<ClashOfRimMod>(); Set(mod, "worldMarkers", flow);
            var caravan = New<Caravan>(); Set(caravan, "tile", new PlanetTile(10, 7));
            Check(mod.FindRaidableTargetsForCaravan(caravan).Single().TileLayerId == 7
                && mod.FindReinforceableTargetsForCaravan(caravan).Single().TileLayerId == 7, "Host forwarding flattened PlanetTile to surface.");
            flow.Observe(flow.CaptureObservation(), new(), true); Check(applied.Last() == ("alice", 0), "Empty delivery did not reach registry removal path.");
            created = removed = failed = 0;
            Check(!bound.Apply(Array.Empty<ModWorldMapMarkerDto>(), "alice").Changed, "No-change result produced noisy replay diagnostics.");
            foreach (string kind in new[] { "created", "removed", "failed" })
            {
                created = kind == "created" ? 1 : 0; removed = kind == "removed" ? 1 : 0; failed = kind == "failed" ? 1 : 0;
                var result = bound.Apply(Array.Empty<ModWorldMapMarkerDto>(), "alice");
                Check(result.Changed && result.Description.Contains(kind + "=1", StringComparison.Ordinal), "Registry result mapping lost " + kind);
            }
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id); Current.Game = game; Current.CreatingWorld = creating;
            queued.Clear(); applied.Clear(); created = removed = failed = 0;
            ClientSessionWork.Reset();
        }
    }
    private static ModWorldMapMarkerDto Marker(int layer) => new()
    { MarkerId = "m" + layer, Kind = "RuntimeCaravan", OwnerUserId = "bob", OwnerColonyId = "home", SnapshotId = "snap", MapId = "map", WorldObjectId = "object", Tile = 10, TileLayerId = layer, CanRaid = true, CanReinforce = true };
    private static bool Apply(IReadOnlyCollection<ModWorldMapMarkerDto> markers, string currentUserId, ref RemoteWorldObjectApplyResult __result)
    { applied.Add((currentUserId, markers.Count)); __result = new(markers.Count, markers.Count, 0, created, 0, removed, 0, 0, failed, null); return false; }
    private static bool Enqueue(Action action, Action onDiscard) { queued.Add((action, onDiscard)); return false; }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Set(object target, string name, object value) => AccessTools.Field(target.GetType(), name).SetValue(target, value);
    private static void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); }
    private sealed class View : IClientWorldMarkersPresentation
    { public string Summary(IReadOnlyCollection<ModWorldMapMarkerDto> markers) => "summary"; public void Status(string text) { } public void Diagnose(string text, bool warning) { } }
}
