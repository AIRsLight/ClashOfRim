using AIRsLight.ClashOfRim.RemoteMaps;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;

namespace ClashOfRim.RimTestBridge;

internal static class PocketMapFixture
{
    private const string Signal = "RimTestPocketCollision.Hacked";
    internal static object TravelControl(Map map)
    {
        var owner = RemoteSessionPocketMapUtility.FindSessionParent(map);
        if (owner?.IsRaidBattle != true) throw new InvalidOperationException("Travel control requires a remote raid map.");
        var targets = RemoteSessionPocketMapUtility.MapTree(owner.Map)
            .SelectMany(m => m.mapPawns.AllPawnsSpawned).Where(p => p.Faction != Faction.OfPlayer).ToArray();
        foreach (var target in targets) target.stances.stunner.StunFor(10000, null);
        return new { fixture = true, stunned = targets.Select(p => p.ThingID).ToArray() };
    }
    internal static object QuestHatches(Map home, string? nestedMapId)
    {
        var root = DefDatabase<QuestScriptDef>.GetNamed("Gravcore_AncientStockpile");
        var slate = new Slate(); slate.Set("points", 500f);
        if (!root.CanRun(slate, home)) throw new InvalidOperationException("Native stockpile quest cannot select a site.");
        var quest = QuestUtility.GenerateQuestAndMakeAvailable(root, slate);
        var site = quest.PartsListForReading.OfType<QuestPart_SpawnWorldObject>().Select(p => p.worldObject).OfType<Site>().Single();
        string before = quest.State.ToString();
        var surface = GetOrGenerateMapUtility.GetOrGenerateMap(site.Tile, new IntVec3(150, 1, 150), null);
        var hatches = surface.listerThings.AllThings.OfType<AncientHatch>().ToArray();
        if (before != "Ongoing" || quest.State.ToString() != "EndedSuccess" || hatches.Length != 3)
            throw new InvalidOperationException("Native quest did not create its three stockpile entrances and finish on surface generation.");
        // Move genuine quest-generated entrances to the test colony so the
        // multiplayer marker selects them. Preserve native parameters and locks.
        var selected = nestedMapId is null ? hatches.Where(h => h.stockpileType == TileMutatorWorker_Stockpile.StockpileType.Gravcore) : hatches;
        foreach (var hatch in selected)
        {
            var destination = nestedMapId is not null && hatch.stockpileType == TileMutatorWorker_Stockpile.StockpileType.Gravcore
                ? Find.Maps.Single(m => "Map_" + m.uniqueID == nestedMapId) : home;
            hatch.DeSpawn(); PocketMapCommands.Place(destination, hatch);
            if (hatch.layout is not null) BridgeReflection.Set(hatch.GetComp<CompHackable>(), "hackingCompletedSignal", Signal);
        }
        if (nestedMapId is not null) AddSignalLetter(home, "remote");
        Current.Game.CurrentMap = home;
        Current.Game.DeinitAndRemoveMap(surface, notifyPlayer: false);
        if (Find.WorldObjects.Contains(site)) Find.WorldObjects.Remove(site);
        return new { fixture = true, questId = quest.id, before, after = quest.State.ToString(),
            hatches = hatches.Select(h => new { id = h.ThingID, layout = h.layout?.defName, stockpile = h.stockpileType.ToString() }).ToArray() };
    }
    internal static object SignalSentinel(Map map)
    {
        AddSignalLetter(map, "local");
        return new { fixture = true };
    }
    private static void AddSignalLetter(Map map, string side)
    {
        var action = (SignalAction_Letter)ThingMaker.MakeThing(ThingDefOf.SignalAction_Letter);
        action.signalTag = Signal; action.letterDef = LetterDefOf.NeutralEvent;
        BridgeReflection.Set(action, "letterLabel", "RimTest pocket signal " + side);
        BridgeReflection.Set(action, "letterMessage", "RimTest pocket signal " + side);
        GenSpawn.Spawn(action, map.Center, map);
    }
    internal static object BlockedProbe(MapPortal portal)
    {
        var hatch = (AncientHatch)portal; var layout = hatch.layout;
        int before = Find.Maps.Count; var generator = PocketMapUtility.currentlyGeneratingPortal;
        try
        {
            hatch.layout = new LayoutDef { defName = "RimTestUnverifiedLayout" };
            bool denied = !hatch.IsEnterable(out var reason) && hatch.GetOtherMap() is null;
            if (!denied || Find.Maps.Count != before || PocketMapUtility.currentlyGeneratingPortal != generator)
                throw new InvalidOperationException("Unsupported remote generation was admitted or changed map/generator state.");
            return new { fixture = true, denied, reason };
        }
        finally { hatch.layout = layout; }
    }
    internal static void Stamp(Map map)
    {
        var marker = ThingMaker.MakeThing(ThingDef.Named("Gun_Autopistol")); marker.HitPoints = 29;
        marker.questTags = new List<string> { "RimTestPocketMarker" };
        GenSpawn.Spawn(marker, map.AllCells.First(c => c.Standable(map) && !c.GetThingList(map).Any(t => t is MapPortal || t.def.category == ThingCategory.Item)), map);
    }
}
