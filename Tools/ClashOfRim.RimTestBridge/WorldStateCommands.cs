using RimTest.Runtime;
using Verse;

namespace ClashOfRim.RimTestBridge;

internal sealed class WorldStateCommands
{
    internal WorldStateCommands(CommandRouter router, ObjectRegistry objects, Func<string> worldConfigurationId)
    {
        router.Register("clash.world", "Read world identity and generated topology counts without credentials or payload contents.", "observe", _ => new
        {
            worldConfigurationId = worldConfigurationId(),
            seed = Find.World?.info?.seedString,
            coverage = Find.World?.info?.planetCoverage,
            persistentRandomValue = Find.World?.info?.persistentRandomValue,
            layers = Find.World?.grid?.PlanetLayers.Values.Select(l => new { id = l.LayerID, defName = l.Def.defName, tiles = l.TilesCount }).ToArray()
        });
        router.Register("clash.inventory", "Count spawned home-map stock and colonists; excludes in-flight delivery containers.", "observe", _ => new
        {
            maps = Find.Maps.Where(m => m.IsPlayerHome).Select(m => new
            {
                reference = objects.Add(m),
                mapId = "Map_" + m.uniqueID,
                tileId = m.Tile.tileId,
                layerId = m.Tile.Layer.LayerID,
                items = m.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item)
                    .GroupBy(t => t.def.defName).ToDictionary(g => g.Key, g => g.Sum(t => t.stackCount)),
                colonists = m.mapPawns.FreeColonistsSpawned.Select(p => new { reference = objects.Add(p), id = p.ThingID }).ToArray()
            }).ToArray()
        });
    }
}
