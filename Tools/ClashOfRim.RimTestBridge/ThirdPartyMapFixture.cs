using System.Collections;
using AIRsLight.ClashOfRim.Raids;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace ClashOfRim.RimTestBridge;

internal static class ThirdPartyMapFixture
{
    internal static void Storage(Map map)
    {
        var cell = Clear(map, new IntVec3(-15, 0, 0));
        var shelf = (Building_Storage)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("sbz_Crate"), ThingDefOf.WoodLog);
        shelf.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(shelf, cell, map);
        if (shelf.GetType().FullName != "AdaptiveStorage.ThingClass") throw new InvalidOperationException("Storage fixture requires the real Adaptive Storage replacement.");
        shelf.GetStoreSettings().filter.SetAllowAll(null);
        for (int i = 0; i < 4; i++)
        {
            var stack = ThingMaker.MakeThing(ThingDefOf.Steel); stack.stackCount = 75;
            GenSpawn.Spawn(stack, cell, map); stack.SetForbidden(false);
        }
    }
    internal static void Vehicle(Map map)
    {
        var cell = Clear(map, new IntVec3(15, 0, 0));
        var def = DefDatabase<ThingDef>.GetNamed("VVE_Mule");
        var factory = AccessTools.TypeByName("Vehicles.VehicleSpawner").GetMethods().Single(m => m.Name == "GenerateVehicle" && m.GetParameters().Length == 2);
        var vehicle = (Pawn)factory.Invoke(null, [def, Faction.OfPlayer])!;
        GenSpawn.Spawn(vehicle, cell, map);
        var cargo = ThingMaker.MakeThing(ThingDefOf.Steel); cargo.stackCount = 17; vehicle.inventory.innerContainer.TryAdd(cargo);
        var passenger = Crew(map, "RimTestVehicle passenger", cell + new IntVec3(5, 0, 0));
        if (!(bool)AccessTools.Method(vehicle.GetType(), "TryAddPawn", [typeof(Pawn)]).Invoke(vehicle, [passenger])!) throw new InvalidOperationException("Vehicle refused native boarding.");
        var assigned = Crew(map, "RimTestVehicle assigned", cell + new IntVec3(6, 0, 0));
        var point = (Building_ClashDefensePoint)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("ClashOfRim_DefensePoint"));
        point.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(point, cell + new IntVec3(5, 0, 3), map);
        point.GetComp<CompAssignableToPawn>().TryAssignPawn(assigned);
        var toggle = point.GetGizmos().OfType<Command_Toggle>().Single(g => g.Label == "ClashOfRim.VehicleFramework.DefensePoint.EnterVehicle".Translate());
        toggle.toggleAction();
        if (!toggle.isActive()) throw new InvalidOperationException("Vehicle defense-point action did not activate.");
    }
    private static Pawn Crew(Map map, string name, IntVec3 cell)
    {
        var p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true,
            canGeneratePawnRelations: false, mustBeCapableOfViolence: true));
        p.Name = new NameSingle(name); GenSpawn.Spawn(p, cell, map); return p;
    }
    internal static void Animation(Map map)
    {
        var rendererType = AccessTools.TypeByName("AM.AnimRenderer");
        var def = GenDefDatabase.GetDef(AccessTools.TypeByName("AM.AnimDef"), "AM_Duel_Swords", true);
        var renderer = Activator.CreateInstance(rendererType, [def, map])!;
        var cell = Clear(map, new IntVec3(0, 0, 20));
        BridgeReflection.Set(renderer, "RootTransform", UnityEngine.Matrix4x4.TRS(cell.ToVector3Shifted(), UnityEngine.Quaternion.identity, UnityEngine.Vector3.one));
        BridgeReflection.Set(renderer, "Loop", true); BridgeReflection.Set(renderer, "IsFriendlyDuel", true);
        for (int i = 0; i < 2; i++)
        {
            var p = Crew(map, "RimTestAnimation " + i, cell + new IntVec3(i, 0, 0));
            p.equipment.DestroyAllEquipment(); p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("MeleeWeapon_LongSword"), ThingDefOf.Steel));
            AccessTools.Method(rendererType, "AddPawn", [typeof(Pawn), typeof(int), typeof(bool)]).Invoke(renderer, [p, i, true]);
        }
        if (!(bool)BridgeReflection.Invoke(renderer, "Register")!) throw new InvalidOperationException("Native animation registration refused fixture.");
    }
    internal static object Read(Map map) => new
    {
        storage = map.listerThings.AllThings.Where(t => t.GetType().FullName == "AdaptiveStorage.ThingClass").Select(t => new
        {
            t.ThingID, defName = t.def.defName, cellLimit = GridsUtility.GetMaxItemsAllowedInCell(t.Position, map),
            contents = ((IEnumerable)AccessTools.PropertyGetter(t.GetType(), "StoredThings").Invoke(t, null)!).Cast<Thing>()
                .Select(s => new { s.ThingID, s.stackCount, forbidden = s.IsForbidden(Faction.OfPlayer), spawned = s.Spawned, atContainer = s.Position == t.Position }).OrderBy(s => s.ThingID).ToArray()
        }).ToArray(),
        vehicles = map.mapPawns.AllPawnsSpawned.Where(p => p.GetType().FullName == "Vehicles.VehiclePawn").Select(p => new
        {
            p.ThingID, faction = p.Faction?.GetUniqueLoadID(), player = p.Faction == Faction.OfPlayer, cargo = p.inventory.innerContainer.Sum(t => t.stackCount),
            lord = p.GetLord()?.LordJob?.GetType().FullName, duty = p.mindState?.duty?.def?.defName,
            passengers = ((IEnumerable)BridgeReflection.Field<object>(p, "handlers")).Cast<object>()
                .SelectMany(h => ((IEnumerable)BridgeReflection.Field<object>(h, "thingOwner")).Cast<Pawn>())
                .Select(c => new { name = c.Name?.ToStringFull, faction = c.Faction?.GetUniqueLoadID(), player = c.Faction == Faction.OfPlayer, c.Spawned }).OrderBy(c => c.name).ToArray()
        }).ToArray(),
        animations = Animations(map).Count()
    };
    private static IEnumerable<object> Animations(Map map)
    {
        var type = AccessTools.TypeByName("AM.AnimRenderer");
        return type is null ? [] : ((IEnumerable)AccessTools.PropertyGetter(type, "ActiveRenderers").Invoke(null, null)!).Cast<object>()
            .Where(r => ReferenceEquals(BridgeReflection.Field<object>(r, "Map"), map));
    }
    internal static void StopAnimations(Map map) { foreach (var r in Animations(map).ToArray()) BridgeReflection.Invoke(r, "Destroy"); }
    private static IntVec3 Clear(Map map, IntVec3 offset)
    {
        var center = map.Center + offset;
        foreach (var c in CellRect.CenteredOn(center, 10, 10))
        {
            foreach (var t in c.GetThingList(map).ToArray()) if (t.def.destroyable && t.def.category is ThingCategory.Building or ThingCategory.Plant) t.Destroy(DestroyMode.Vanish);
            map.terrainGrid.SetTerrain(c, TerrainDefOf.Concrete); map.fogGrid.Unfog(c);
        }
        return center;
    }
}
