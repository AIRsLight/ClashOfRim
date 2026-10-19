using AIRsLight.ClashOfRim.Raids;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace ClashOfRim.RimTestBridge;

// Only constructs the acknowledged initial scene. Projection, duty selection,
// ticking, save/load and settlement are always performed by the product/game.
internal static class CombatFixture
{
    private const string Prefix = "RimTestCombat ";
    internal static bool IsTagged(Pawn pawn) => pawn.Name?.ToStringFull.StartsWith(Prefix, StringComparison.Ordinal) == true;
    internal static void Create(Map map)
    {
        if (map.mapPawns.AllPawnsSpawned.Any(IsTagged)) throw new InvalidOperationException("Combat fixture already exists.");
        IntVec3 center = map.Center;
        foreach (var cell in CellRect.CenteredOn(center, 62, 72).Cells)
        {
            foreach (var thing in cell.GetThingList(map).ToArray())
                if (thing.def.destroyable && thing.def.category is ThingCategory.Building or ThingCategory.Plant) thing.Destroy(DestroyMode.Vanish);
            map.terrainGrid.SetTerrain(cell, TerrainDefOf.Concrete);
            map.roofGrid.SetRoof(cell, null); map.fogGrid.Unfog(cell);
        }
        int i = 0;
        Building_ClashDefensePoint? first = null;
        foreach (var mode in new[] { DefensePointAiMode.Hold, DefensePointAiMode.Defend, DefensePointAiMode.Assault, DefensePointAiMode.OperateEquipment })
        {
            var cell = center + new IntVec3(-36 + i++ * 24, 0, -25);
            var point = (Building_ClashDefensePoint)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("ClashOfRim_DefensePoint"));
            point.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(point, cell, map);
            // Exercise the original mode menu action, not a test copy of its setter.
            BridgeReflection.Invoke(point, "OpenModeMenu");
            var menu = Find.WindowStack.WindowOfType<FloatMenu>();
            BridgeReflection.Field<List<FloatMenuOption>>(menu, "options")[(int)mode].action(); menu.Close(false);
            BridgeReflection.Set(point, "actionRadius", 8f);
            var pawn = Spawn(map, mode.ToString(), PawnKindDefOf.Colonist, Faction.OfPlayer, cell + new IntVec3(0, 0, -12));
            pawn.equipment.DestroyAllEquipment();
            pawn.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle")));
            first ??= point;
            if (point != first) first.GetComp<CompAssignableToPawn>().TryAssignPawn(pawn);
            point.GetComp<CompAssignableToPawn>().TryAssignPawn(pawn);
            if (map.listerThings.AllThings.OfType<Building_ClashDefensePoint>().Count(p => p.GetComp<CompAssignableToPawn>().AssignedPawnsForReading.Contains(pawn)) != 1)
                throw new InvalidOperationException("Defense assignment was not exclusive.");
            if (mode == DefensePointAiMode.OperateEquipment)
            {
                var mortar = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Turret_Mortar"), ThingDefOf.Steel);
                mortar.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(mortar, cell + new IntVec3(4, 0, 0), map);
                if (mortar.TryGetComp<CompRefuelable>() is { } fuel) fuel.Refuel(fuel.Props.fuelCapacity);
                var shells = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Shell_HighExplosive")); shells.stackCount = 10;
                GenSpawn.Spawn(shells, cell + new IntVec3(6, 0, 0), map);
            }
        }
        // Restore Hold, displaced by the assignment exclusivity checks above.
        first!.GetComp<CompAssignableToPawn>().TryAssignPawn(map.mapPawns.AllPawnsSpawned.Single(p => p.Name?.ToStringFull == Prefix + "Hold"));
        Spawn(map, "Fallback", PawnKindDefOf.Colonist, Faction.OfPlayer, center + new IntVec3(-36, 0, -45));
        var hostile = Find.FactionManager.AllFactionsListForReading.First(f => !f.IsPlayer && !f.temporary && f.def.humanlikeFaction && f.HostileTo(Faction.OfPlayer));
        var friendly = Find.FactionManager.AllFactionsListForReading.First(f => !f.IsPlayer && !f.temporary && f.def.humanlikeFaction && !f.HostileTo(Faction.OfPlayer));
        var raider = Spawn(map, "Raider", PawnKindDefOf.Pirate, hostile, center + new IntVec3(-55, 0, 65));
        var visitor = Spawn(map, "Visitor", PawnKindDefOf.Colonist, friendly, center + new IntVec3(55, 0, 65));
        LordMaker.MakeNewLord(hostile, new LordJob_AssaultColony(hostile, canKidnap: false, canTimeoutOrFlee: false, canSteal: false), map, new[] { raider }).CurLordToil.UpdateAllDuties();
        LordMaker.MakeNewLord(friendly, new LordJob_DefendBase(friendly, visitor.Position, 180000, attackWhenPlayerBecameEnemy: false), map, new[] { visitor }).CurLordToil.UpdateAllDuties();
        if (ModsConfig.BiotechActive)
        {
            var cell = center + new IntVec3(0, 0, 15);
            var point = (Building_ClashDefensePoint)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("ClashOfRim_MechDefensePoint"));
            point.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(point, cell, map);
            var mech = Spawn(map, "Mech", DefDatabase<PawnKindDef>.GetNamed("Mech_Militor"), Faction.OfPlayer, cell + new IntVec3(0, 0, -12));
            point.GetComp<CompAssignableToPawn>().TryAssignPawn(mech);
            if (point.AssignedPawn != mech) throw new InvalidOperationException("Mech defense point rejected player mech.");
        }
    }
    private static Pawn Spawn(Map map, string role, PawnKindDef kind, Faction faction, IntVec3 cell)
    {
        var pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, faction, forceGenerateNewPawn: true, canGeneratePawnRelations: false, mustBeCapableOfViolence: true));
        pawn.Name = new NameSingle(Prefix + role);
        if (pawn.needs.food is { } food) food.CurLevelPercentage = 1;
        if (pawn.needs.rest is { } rest) rest.CurLevelPercentage = 1;
        GenSpawn.Spawn(pawn, cell, map);
        if (faction == Faction.OfPlayer && pawn.RaceProps.Humanlike) pawn.drafter.Drafted = true;
        return pawn;
    }
}
