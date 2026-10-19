using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim;
using static ClashOfRim.RimTestBridge.BridgeReflection;
using System.Text.Json;
using RimTest.Protocol;
using RimTest.Runtime;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ClashOfRim.RimTestBridge;

internal sealed class RelocationCommands
{
    internal RelocationCommands(CommandRouter router, ObjectRegistry objects, BridgeActionPolicy policy)
    {
        router.Register("clash.fixture.relocationEnemy", "FIXTURE: add/remove one hostile mech on an existing destination map to test settle threat validation.", "fixture", a =>
        {
            policy.RequireFixture(a); var map = objects.Get<Map>(a.String("map"));
            if (map.IsPlayerHome || !Find.Maps.Contains(map)) throw new CommandException("invalid_context", "Expected a destination site map.");
            if (a.OptionalString("remove") is { } id)
                map.mapPawns.AllPawnsSpawned.Single(p => p.ThingID == id && p.Faction == Faction.OfMechanoids).Destroy();
            else
            {
                var p = PawnGenerator.GeneratePawn(PawnKindDefOf.Mech_Scyther, Faction.OfMechanoids);
                GenSpawn.Spawn(p, CellFinder.RandomClosewalkCellNear(map.Center, map, 15), map);
                return new { fixture = true, id = p.ThingID };
            }
            return new { fixture = true, id = "" };
        });
        router.Register("clash.relocation", "Observe local map anchors, pawn identities and original settle command availability.", "observe", _ => new
        {
            label = "ClashOfRim.ColonyRelocation.CommandLabel".Translate().ToString(),
            maps = Find.Maps.Select(m => new { id = "Map_" + m.uniqueID, reference = objects.Add(m), tile = m.Tile.tileId,
                layer = m.Tile.Layer.LayerID, home = m.IsPlayerHome, parent = m.Parent.def.defName,
                playerOwned = m.Parent.Faction == Faction.OfPlayer,
                pawns = m.mapPawns.FreeColonistsSpawned.Select(p => p.ThingID).Order().ToArray(),
                settle = m.IsPlayerHome ? null : SettleState(SettleInExistingMapUtility.SettleCommand(m, true)) }).ToArray()
        });
        router.Register("clash.relocation.settleMap", "Invoke the current existing-map settle command with normal enemy and colony limit checks.", "semantic", a =>
        {
            policy.RequireLoopback(); var map = objects.Get<Map>(a.String("map"));
            if (!Find.Maps.Contains(map) || map.IsPlayerHome) throw new CommandException("invalid_context", "Expected current non-home map.");
            var command = SettleInExistingMapUtility.SettleCommand(map, true);
            if (command.Disabled) throw new CommandException("disabled", command.disabledReason);
            ((Command_Action)command).action(); return new { invoked = true };
        });
        router.Register("clash.relocation.confirm", "Choose the visible mod relocation confirmation's original confirm callback.", "semantic", _ =>
        {
            policy.RequireLoopback(); var window = Find.WindowStack.Windows.Single(w => w.GetType().Name == "ColonyRelocationConfirmationWindow");
            var action = Field<Action>(window, "onConfirm"); window.Close(); action(); return new { invoked = true };
        });
        router.Register("clash.fixture.relocation", "FIXTURE: move current colonists and silver/10 steel into a nearby caravan or generated ItemStash site; settlement and server registration remain untouched.", "fixture", a =>
        {
            policy.RequireFixture(a); var home = BridgeMaps.Select(a); string kind = a.String("kind");
            if (kind is not ("caravan" or "site")) throw new CommandException("invalid_arguments", "kind: caravan/site");
            var tile = BridgeMaps.NearbySettlement(home);
            var pawns = home.mapPawns.FreeColonistsSpawned.ToArray();
            if (pawns.Length != 3) throw new CommandException("invalid_context", "Expected three fixture colonists.");
            foreach (var silver in home.listerThings.ThingsOfDef(ThingDefOf.Silver).ToArray())
            { silver.DeSpawn(); if (!pawns[0].inventory.innerContainer.TryAdd(silver)) throw new InvalidOperationException("Could not pack silver."); }
            var steel = home.listerThings.ThingsOfDef(ThingDefOf.Steel).First(t => t.stackCount >= 10).SplitOff(10);
            if (steel.Spawned) steel.DeSpawn(); pawns[0].inventory.innerContainer.TryAdd(steel);
            foreach (var p in pawns) { p.jobs.StopAll(); p.DeSpawn(); }
            if (kind == "caravan")
            {
                var caravan = CaravanMaker.MakeCaravan(pawns, Faction.OfPlayer, tile, true);
                return new { fixture = true, kind, tile = tile.tileId, target = objects.Add(caravan), pawns = pawns.Select(p => p.ThingID).Order().ToArray() };
            }
            var site = SiteMaker.MakeSite(DefDatabase<SitePartDef>.GetNamed("ItemStash"), tile, null, false, 0f);
            Find.WorldObjects.Add(site);
            var map = MapGenerator.GenerateMap(new IntVec3(100, 1, 100), site, site.MapGeneratorDef);
            foreach (var p in pawns) GenSpawn.Spawn(p, CellFinder.RandomClosewalkCellNear(map.Center, map, 8), map);
            Current.Game.CurrentMap = map;
            return new { fixture = true, kind, tile = tile.tileId, target = objects.Add(map), pawns = pawns.Select(p => p.ThingID).Order().ToArray() };
        });
    }
    private static object SettleState(Command c) => new { label = c.Label, disabled = c.Disabled, reason = c.disabledReason };
}
