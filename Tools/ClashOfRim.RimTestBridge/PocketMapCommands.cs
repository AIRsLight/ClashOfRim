using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.RemoteMaps;
using RimTest.Protocol;
using RimTest.Runtime;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

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
                gravcores = p.Map.listerThings.AllThings.Where(t => t.def.defName == "Gravcore").Sum(t => t.stackCount),
                corpses = p.Map.listerThings.AllThings.OfType<Corpse>().OrderBy(c => c.ThingID, StringComparer.Ordinal)
                    .Select(c => new { id = c.ThingID, pawnId = c.InnerPawn?.ThingID, kind = c.InnerPawn?.kindDef?.defName, valid = !c.Bugged }).ToArray(),
                markers = p.Map.listerThings.AllThings.Where(t => t.questTags?.Any(q => q.EndsWith("RimTestPocketMarker")) == true)
                    .Select(t => new { def = t.def.defName, count = t.stackCount, hp = t.HitPoints, cell = t.Position.ToString() }).ToArray(),
                layouts = p.Map.layoutStructureSketches.Select(s => new { id = s.GetUniqueLoadID(),
                    valid = s.structureLayout.Rooms.All(r => ReferenceEquals(r.sketch, s)), rooms = s.structureLayout.Rooms.Select(r => r.GetUniqueLoadID()).ToArray() }).ToArray(),
                pawns = p.Map.mapPawns.AllPawnsSpawned.OrderBy(pawn => pawn.ThingID, StringComparer.Ordinal).Select(pawn => new { id = pawn.ThingID, player = pawn.Faction == Faction.OfPlayer,
                    marked = RemoteSessionThingCleanup.HasRemoteSessionCleanupTag(pawn) }).ToArray() }).ToArray(),
            portals = Find.Maps.SelectMany(m => m.listerThings.AllThings.OfType<MapPortal>()).Select(p => new
            {
                reference = objects.Add(p), enterLabel = p.EnterString, id = p.ThingID, mapId = Id(p.Map), childId = Id(p.PocketMap), exitId = p.exit?.ThingID,
                kind = p.def.defName, layout = (p as AncientHatch)?.layout?.defName,
                hacked = p.GetComp<CompHackable>()?.IsHacked, enterable = p.IsEnterable(out _)
            }).ToArray(),
            players = Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned).Select(p => new { reference = objects.Add(p), id = p.ThingID, mapId = Id(p.Map) }).ToArray(),
            currentMapId = Id(Find.CurrentMap),
            quests = Find.QuestManager.QuestsListForReading.Select(q => new { id = q.id, state = q.State.ToString() }).ToArray(),
            fixtureLetters = Find.LetterStack.LettersListForReading.Where(l => l.Label.ToString().StartsWith("RimTest pocket signal")).Select(l => l.Label.ToString()).ToArray(),
            removable = RemoteSessionMapUtility.FindActiveSessionMap()?.ShouldRemoveMapNow(out _) ?? false,
            pendingMaps = RemoteSessionMapUtility.FindActiveSessionMap()?.DeferredPockets.Count ?? 0,
            temporaryWorldPawns = Find.WorldPawns.AllPawnsAliveOrDead.Where(RemoteSessionThingCleanup.HasRemoteSessionCleanupTag).Select(p => p.ThingID).ToArray()
        });
        router.Register("clash.fixture.pocket", "FIXTURE: prepare native pits, quest-generated hatches, source markers and signal controls; optionally unlock or generate a selected portal.", "fixture", a =>
        {
            policy.RequireFixture(a);
            if (!ModsConfig.AnomalyActive) throw new InvalidOperationException("Anomaly is required.");
            var map = BridgeMaps.Select(a);
            if (a.OptionalString("kind") == "quest-hatches") return PocketMapFixture.QuestHatches(map, a.OptionalString("nestedMapId"));
            if (a.OptionalString("kind") == "signal-sentinel") return PocketMapFixture.SignalSentinel(map);
            if (a.OptionalString("kind") == "travel-control") return PocketMapFixture.TravelControl(map);
            var portal = a.OptionalString("portalId") is { } id
                ? map.listerThings.AllThings.OfType<MapPortal>().Single(p => p.ThingID == id) : SpawnGate(map);
            if (a.TryGetProperty("unlock", out var unlock) && unlock.GetBoolean()) portal.GetComp<CompHackable>()?.HackNow();
            if (a.TryGetProperty("blockedProbe", out var probe) && probe.GetBoolean()) return PocketMapFixture.BlockedProbe(portal);
            var pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            bool generate = !a.TryGetProperty("generate", out var value) || value.GetBoolean();
            Map? child = generate ? portal.GetOtherMap() : null;
            if (child is not null && a.TryGetProperty("stamp", out var stamp) && stamp.GetBoolean()) PocketMapFixture.Stamp(child);
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
        var portal = Place(map, (MapPortal)ThingMaker.MakeThing(def));
        if (portal.IsEnterable(out _)) throw new InvalidOperationException("Fresh pit gate bypassed native settling admission.");
        BridgeReflection.Set(portal, "tickOpened", Find.TickManager.TicksGame - 450);
        // Portal lifecycle after the initial emergence wave.
        BridgeReflection.Set(portal, "lastIncidentTick", Find.TickManager.TicksGame);
        if (!portal.IsEnterable(out var reason)) throw new InvalidOperationException("Native portal denied entry: " + reason);
        return portal;
    }
    internal static MapPortal Place(Map map, MapPortal portal)
    {
        var def = portal.def;
        var pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
        var anchor = pawn?.Position ?? map.listerThings.AllThings.OfType<PocketMapExit>().FirstOrDefault()?.Position ?? map.Center;
        var cell = map.AllCells.Where(c => c.InBounds(map) && c.Standable(map)
            && map.reachability.CanReach(anchor, c, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly))
            && GenAdj.OccupiedRect(c, Rot4.North, def.size).All(v => v.InBounds(map)
                && !v.GetThingList(map).Any(t => t is Pawn || t is MapPortal || t.def.category == ThingCategory.Item
                    || t is Building && !t.def.destroyable))
            && GenAdj.OccupiedRect(c, Rot4.North, def.size).ExpandedBy(2).All(v => v.InBounds(map)
                && !v.GetThingList(map).Any(t => t is Pawn || t is MapPortal)))
            .OrderBy(c => GenAdj.OccupiedRect(c, Rot4.North, def.size).Count(v => v.GetEdifice(map) is not null))
            .ThenBy(c => c.DistanceToSquared(anchor)).First();
        // Undercaves need not contain an empty 8x8 area. Clear only the fixture
        // footprint, preserving pawns and existing entrance/exit links.
        foreach (var building in GenAdj.OccupiedRect(cell, Rot4.North, def.size)
            .SelectMany(v => v.GetThingList(map)).OfType<Building>().Distinct().ToList())
            if (!building.Destroyed) building.Destroy(DestroyMode.Vanish); // a wall can also destroy its attached light
        var placed = (MapPortal)GenSpawn.Spawn(portal, cell, map);
        if (!map.reachability.CanReach(anchor, placed, PathEndMode.Touch, TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly)))
            throw new InvalidOperationException("Fixture portal is disconnected from the native exit or colonist.");
        return placed;
    }
    private static string? Id(Map? map) => map is null ? null : "Map_" + map.uniqueID;
}
