using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.RemoteMaps;
using RimTest.Protocol;
using RimTest.Runtime;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ClashOfRim.RimTestBridge;

internal sealed class PocketMapCommands
{
    internal PocketMapCommands(CommandRouter router, ObjectRegistry objects, BridgeActionPolicy policy)
    {
        router.Register("clash.pocket.state", "Observe native pocket links, remote ownership, pawn placement and cleanup residue.", "observe", args => new
        {
            pockets = Find.World.pocketMaps.Select(p => new { mapId = Id(p.Map), sourceMapId = Id(p.sourceMap),
                owner = RemoteSessionPocketMapUtility.FindSessionParent(p.Map)?.SessionId,
                guarded = RemoteSessionGlobalStateGuard.IsRemoteMap(p.Map),
                pawns = p.Map.mapPawns.AllPawnsSpawned.Select(pawn => new { id = pawn.ThingID, player = pawn.Faction == Faction.OfPlayer,
                    marked = RemoteSessionThingCleanup.HasRemoteSessionCleanupTag(pawn) }).ToArray() }).ToArray(),
            portals = Find.Maps.SelectMany(m => m.listerThings.AllThings.OfType<MapPortal>()).Select(p => new
            {
                reference = objects.Add(p), enterLabel = p.EnterString, id = p.ThingID, mapId = Id(p.Map), childId = Id(p.PocketMap), exitId = p.exit?.ThingID,
                enterable = p.IsEnterable(out _)
            }).ToArray(),
            players = Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned).Select(p => new { reference = objects.Add(p), id = p.ThingID, mapId = Id(p.Map) }).ToArray(),
            currentMapId = Id(Find.CurrentMap),
            removable = RemoteSessionMapUtility.FindActiveSessionMap()?.ShouldRemoveMapNow(out _) ?? false,
            temporaryWorldPawns = Find.WorldPawns.AllPawnsAliveOrDead.Where(RemoteSessionThingCleanup.HasRemoteSessionCleanupTag).Select(p => p.ThingID).ToArray()
        });
        router.Register("clash.fixture.pocket", "FIXTURE: spawn and mature a pit gate, checking settling admission, and optionally generate its native child map.", "fixture", a =>
        {
            policy.RequireFixture(a);
            if (!ModsConfig.AnomalyActive) throw new InvalidOperationException("Anomaly is required.");
            var map = BridgeMaps.Select(a);
            var portal = a.OptionalString("portalId") is { } id
                ? map.listerThings.AllThings.OfType<MapPortal>().Single(p => p.ThingID == id) : SpawnGate(map);
            var pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            bool generate = !a.TryGetProperty("generate", out var value) || value.GetBoolean();
            Map? child = generate ? portal.GetOtherMap() : null;
            if (child is not null) Current.Game.CurrentMap = child;
            return new { fixture = true, portalId = portal.ThingID, childMapId = Id(child), pawnId = pawn?.ThingID };
        });
        router.Register("clash.fixture.pocketDestroy", "FIXTURE: invoke native pocket destruction to test collapse cleanup without waiting for the cave event.", "fixture", a =>
        {
            policy.RequireFixture(a); var map = BridgeMaps.Select(a);
            if (map.Parent is not PocketMapParent pocket) throw new InvalidOperationException("Expected a pocket map.");
            var entrances = pocket.sourceMap.listerThings.AllThings.OfType<MapPortal>().Where(p => p.PocketMap == map).ToList();
            PocketMapUtility.DestroyPocketMap(map);
            bool previous = Thing.allowDestroyNonDestroyable;
            try
            {
                Thing.allowDestroyNonDestroyable = true;
                foreach (var entrance in entrances) entrance.Destroy(DestroyMode.Vanish);
            }
            finally { Thing.allowDestroyNonDestroyable = previous; }
            return new { fixture = true };
        });
    }
    private static MapPortal SpawnGate(Map map)
    {
        var def = DefDatabase<ThingDef>.GetNamed("PitGate");
        var pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
        var anchor = pawn?.Position ?? map.Center;
        var cell = map.AllCells.Where(c => c.InBounds(map) && c.Standable(map)
            && GenAdj.OccupiedRect(c, Rot4.North, def.size).All(v => v.InBounds(map)
                && !v.GetThingList(map).Any(t => t is Pawn || t is MapPortal || t.def.category == ThingCategory.Item
                    || t is Building && !t.def.destroyable)))
            .OrderBy(c => GenAdj.OccupiedRect(c, Rot4.North, def.size).Count(v => v.GetEdifice(map) is not null))
            .ThenBy(c => c.DistanceToSquared(anchor)).First();
        // Undercaves need not contain an empty 8x8 area. Clear only the fixture
        // footprint, preserving pawns and existing entrance/exit links.
        foreach (var building in GenAdj.OccupiedRect(cell, Rot4.North, def.size)
            .SelectMany(v => v.GetThingList(map)).OfType<Building>().Distinct().ToList())
            building.Destroy(DestroyMode.Vanish);
        var portal = (MapPortal)GenSpawn.Spawn(ThingMaker.MakeThing(def), cell, map);
        if (portal.IsEnterable(out _)) throw new InvalidOperationException("Fresh pit gate bypassed native settling admission.");
        BridgeReflection.Set(portal, "tickOpened", Find.TickManager.TicksGame - 450);
        // This is a portal-lifecycle fixture after the initial emergence wave.
        // Keep a fresh gate from immediately spawning a second combat scenario.
        BridgeReflection.Set(portal, "lastIncidentTick", Find.TickManager.TicksGame);
        if (!portal.IsEnterable(out var reason)) throw new InvalidOperationException("Native portal denied entry: " + reason);
        return portal;
    }
    private static string? Id(Map? map) => map is null ? null : "Map_" + map.uniqueID;
}
