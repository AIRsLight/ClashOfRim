using RimTest.Protocol;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;

namespace ClashOfRim.RimTestBridge;

internal sealed partial class DomainCommands
{
    private readonly HashSet<Window> submittedGravshipDialogs = new();
    private void RegisterGravship()
    {
        bridge.Router.Register("clash.gravship", "Observe flight/cutscene, landing marker and ship cargo identities without initiating a flight.", "observe", _ => new
        {
            active = ModsConfig.OdysseyActive,
            travelling = ModsConfig.OdysseyActive && Find.GravshipController.IsGravshipTravelling,
            cutscene = ModsConfig.OdysseyActive && WorldComponent_GravshipController.CutsceneInProgress,
            choosingTile = Find.TilePicker.Active,
            ritualDialog = Find.WindowStack.Windows.Any(w => w is Dialog_BeginRitual),
            landing = ModsConfig.OdysseyActive && Find.GravshipController.LandingAreaConfirmationInProgress,
            markers = Find.Maps.SelectMany(m => m.listerThings.AllThings.OfType<GravshipLandingMarker>()).Select(m => new { reference = bridge.Objects.Add(m), tile = m.Map.Tile.tileId }).ToArray(),
            engines = Find.Maps.SelectMany(m => m.listerThings.AllThings.OfType<Building_GravEngine>()).Select(e => new { reference = bridge.Objects.Add(e), id = e.ThingID, tile = e.Map.Tile.tileId,
                fuel = Field<List<CompGravshipFacility>>(e, "gravshipComponents").Sum(c => c.Props.providesFuel ? c.parent.GetComp<CompRefuelable>()?.Fuel ?? 0 : 0),
                substructure = Field<HashSet<IntVec3>>(e, "validSubstructure").Count, e.cooldownCompleteTick }).ToArray()
        });
        bridge.Router.Register("clash.fixture.gravship", "FIXTURE: construct a fueled researched ship, board all three colonists and move silver/steel onto it; no launch/settlement callbacks or controller state are injected.", "fixture", a =>
        {
            Fixture(a); if (!ModsConfig.OdysseyActive) throw new CommandException("unsupported", "Odyssey required.");
            var map = StabilityMap(a); var center = map.Center; var pawns = map.mapPawns.FreeColonistsSpawned.ToArray();
            if (pawns.Length != 3) throw new CommandException("invalid_context", "Expected three colonists.");
            var cargo = map.listerThings.AllThings.Where(t => t.def == ThingDefOf.Silver || t.def == ThingDefOf.Steel).ToArray();
            foreach (var t in cargo) t.DeSpawn();
            foreach (var cell in CellRect.CenteredOn(center, 18).Cells)
            {
                foreach (var t in cell.GetThingList(map).Where(t => t is not Pawn).ToArray())
                    if (t.def.destroyable) t.Destroy(); else t.DeSpawn();
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil); map.roofGrid.SetRoof(cell, null);
            }
            foreach (var cell in CellRect.CenteredOn(center, 7).Cells) map.terrainGrid.SetFoundation(cell, DefDatabase<TerrainDef>.GetNamed("Substructure"));
            Thing Spawn(string def, IntVec3 offset)
            {
                var t = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(def)); t.SetFaction(Faction.OfPlayer); return GenSpawn.Spawn(t, center + offset, map, Rot4.North);
            }
            var engine = (Building_GravEngine)Spawn("GravEngine", IntVec3.Zero);
            var console = Spawn("PilotConsole", new IntVec3(0, 0, 4));
            Spawn("ChemfuelTank", new IntVec3(4, 0, 0)).TryGetComp<CompRefuelable>().Refuel(100);
            var thruster = Spawn("SmallThruster", new IntVec3(-5, 0, -7)).TryGetComp<CompGravshipThruster>();
            var exhaust = new List<IntVec3>(); thruster.Props.GetExclusionZone(thruster.parent.Position, thruster.parent.Rotation, ref exhaust);
            foreach (var cell in exhaust)
                if (map.terrainGrid.FoundationAt(cell) is not null) map.terrainGrid.RemoveFoundation(cell, false);
            Set(engine, "haveShownNameDialog", true);
            Find.ResearchManager.FinishProject(DefDatabase<ResearchProjectDef>.GetNamed("BasicGravtech"), false);
            foreach (var t in cargo) GenPlace.TryPlaceThing(t, center + new IntVec3(3, 0, 3), map, ThingPlaceMode.Near);
            for (int i = 0; i < pawns.Length; i++)
            {
                var p = pawns[i]; p.drafter.Drafted = false; p.jobs.StopAll(); p.Position = center + new IntVec3(i - 1, 0, 2); p.Notify_Teleported();
                foreach (var need in p.needs.AllNeeds) need.CurLevelPercentage = 1;
            }
            map.fogGrid.ClearAllFog(); engine.ForceSubstructureDirty();
            var tile = RelocationTile(map);
            if (a.Bool("site"))
            {
                var site = SiteMaker.MakeSite(DefDatabase<SitePartDef>.GetNamed("ItemStash"), tile, null, false, 0f);
                Find.WorldObjects.Add(site); MapGenerator.GenerateMap(new IntVec3(100, 1, 100), site, site.MapGeneratorDef);
            }
            Current.Game.CurrentMap = map;
            return new { fixture = true, engine = engine.ThingID, tile = tile.tileId, oldTile = map.Tile.tileId, console = bridge.Objects.Add(console),
                pawn = bridge.Objects.Add(pawns[0]), pawns = pawns.Select(p => p.ThingID).Order().ToArray(), cargo = cargo.Select(t => t.ThingID).Order().ToArray() };
        });
        bridge.Router.Register("clash.gravship.pilot", "Choose the console's original Launch gravship pawn menu option; the pawn must walk to the console and open its launch ceremony.", "semantic", a =>
        {
            Loopback(); var p = bridge.Objects.Get<Pawn>(a.String("pawn")); var t = bridge.Objects.Get<Thing>(a.String("console"));
            if (!p.Spawned || !t.Spawned || p.Map != t.Map || p.Faction != Faction.OfPlayer) throw new CommandException("invalid_context", "Expected local pilot and console.");
            var options = t.TryGetComp<CompPilotConsole>().CompFloatMenuOptions(p).ToArray();
            var option = options.SingleOrDefault(o => o.Label == "PilotGravship".Translate().ToString());
            if (option is null || option.Disabled) throw new CommandException("disabled", string.Join("; ", options.Select(o => o.Label)));
            option.action(); return new { invoked = true };
        });
        bridge.Router.Register("clash.gravship.begin", "Submit the visible original gravship ritual dialog with its CanBegin validation; confirmations remain explicit.", "semantic", _ =>
        {
            Loopback(); var d = Find.WindowStack.Windows.OfType<Dialog_BeginRitual>().Single();
            if (Field<Precept_Ritual>(d, "ritual").def != PreceptDefOf.GravshipLaunch || submittedGravshipDialogs.Contains(d) || !d.CanBegin)
                throw new CommandException("disabled", "Gravship ceremony unavailable or already submitted.");
            submittedGravshipDialogs.Add(d); Invoke(d, "Start"); return new { submitted = true };
        });
        bridge.Router.Register("clash.gravship.destination", "Choose a same-surface flight tile through the current original tile picker validator and confirm callback.", "semantic", a =>
        {
            Loopback(); var picker = Find.TilePicker;
            if (!picker.Active || !picker.ForGravship) throw new CommandException("invalid_context", "No gravship tile picker.");
            var tile = new PlanetTile(a.Int("tile"), Find.WorldGrid.Surface);
            if (!Field<Func<PlanetTile, bool>>(picker, "validator")(tile)) throw new CommandException("disabled", "Original destination validator rejected tile.");
            var chosen = Field<Action<PlanetTile>>(picker, "tileChosen"); Invoke(picker, "StopTargetingInt"); chosen(tile); return new { invoked = true };
        });
        bridge.Router.Register("clash.gravship.land", "Confirm the current placed landing zone using the original button action, including designator guard, marker release and landing sound.", "semantic", _ =>
        {
            Loopback(); var controller = Find.GravshipController;
            if (!controller.LandingAreaConfirmationInProgress) throw new CommandException("invalid_context", "No landing confirmation.");
            var marker = Field<GravshipLandingMarker>(controller, "landingMarker");
            if (!marker.Spawned || Find.DesignatorManager.SelectedDesignator == controller.MoveDesignator())
                throw new CommandException("disabled", "Finish placing the landing zone first.");
            // WorldComponentOnGUI's confirm branch also releases this marker after BeginLanding.
            marker.BeginLanding(controller); Set(controller, "landingMarker", null!);
            SoundDefOf.Gravship_Land.PlayOneShotOnCamera(); return new { invoked = true };
        });
        bridge.Router.Register("clash.gravship.placeLanding", "Choose the first center-near cell accepted by the original landing designator; does not force invalid cells or start landing.", "semantic", _ =>
        {
            Loopback(); if (!Find.GravshipController.LandingAreaConfirmationInProgress) throw new CommandException("invalid_context", "No landing confirmation.");
            var d = Find.GravshipController.MoveDesignator();
            var cell = CellRect.CenteredOn(d.map.Center, 25).Cells.OrderBy(c => c.DistanceToSquared(d.map.Center)).FirstOrDefault(c => d.CanDesignateCell(c).Accepted, IntVec3.Invalid);
            if (!cell.IsValid) throw new CommandException("disabled", "No valid landing position near center.");
            Find.DesignatorManager.Select(d); d.DesignateSingleCell(cell);
            return new { placed = d.marker.Spawned, x = cell.x, z = cell.z };
        });
    }
}
