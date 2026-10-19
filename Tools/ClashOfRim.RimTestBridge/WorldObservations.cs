using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIRsLight.ClashOfRim;
using HarmonyLib;
using RimTest.RimWorld;
using RimTest.Protocol;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ClashOfRim.RimTestBridge;

/// <summary>Independent game observations. Never calls the mod's world serializer to compute expected data.</summary>
internal static class WorldObservations
{
    public static void Register(GameBridge bridge)
    {
        bridge.Router.Register("clash.labels", "Localized original labels used by this suite's gizmos and explicit dialog choices.", "observe", _ => new
        {
            steamClientMissing = "SteamClientMissing".Translate().ToString(),
            ignore = "Ignore".Translate().ToString(),
            quit = "Quit".Translate().ToString(),
            gift = "ClashOfRim.GiftDelivery.CaravanGizmo".Translate().ToString(),
            support = "ClashOfRim.Support.CaravanGizmo".Translate().ToString(),
            raid = "ClashOfRim.Raid.CaravanGizmo".Translate().ToString(),
            load = "CommandLoadTransporterSingle".Translate().ToString()
        });
        bridge.Router.Register("clash.world.details", "Actual world parameters, complete layer hashes, difficulty and stable culture references.", "observe", _ => Read());
        bridge.Router.Register("clash.difficulty", "Actual in-game storyteller and every scalar difficulty field.", "observe", _ => new
        {
            storyteller = Current.Game.storyteller.def.defName,
            difficulty = Current.Game.storyteller.difficultyDef.defName,
            values = Scalars(Current.Game.storyteller.difficulty)
        });
        bridge.Router.Register("clash.pawn.identity", "Observe a discovered pawn or a spawned pawn by ThingID, including culture and ideology contents.", "observe", a =>
        {
            Pawn pawn = a.OptionalString("pawn") is { } handle ? bridge.Objects.Get<Pawn>(handle) :
                Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned).Single(p => p.ThingID == a.String("thingId"));
            return new
            {
                name = pawn.Name?.ToStringFull,
                race = pawn.def.defName,
                kind = pawn.kindDef.defName,
                gender = pawn.gender.ToString(),
                biologicalAge = pawn.ageTracker.AgeBiologicalYears,
                ideology = Ideology(pawn.Ideo),
                ideologyRegistered = pawn.Ideo is null || Find.IdeoManager.IdeosListForReading.Contains(pawn.Ideo)
            };
        });
        bridge.Router.Register("clash.world.tiles", "Bounded tile diagnostics for locating full-layer digest mismatches.", "observe", a =>
        {
            int id = a.Int("layerId"), start = a.Int("start"), count = a.Int("count", 64);
            var layer = Find.World.grid.PlanetLayers[id];
            if (start < 0 || count < 1 || count > 1024 || start + count > layer.TilesCount) throw new ArgumentOutOfRangeException(nameof(start));
            return Enumerable.Range(start, count).Select(index => new { index, biome = new PlanetTile(index, layer).Tile.PrimaryBiome.defName, values = Scalars(new PlanetTile(index, layer).Tile) }).ToArray();
        });
        bridge.Router.Register("clash.storyteller.open", "Open the original in-game storyteller page, retaining multiplayer permission patches.", "semantic", _ =>
        {
            var page = new Page_SelectStorytellerInGame();
            Find.WindowStack.Add(page);
            return new { opened = Find.WindowStack.Windows.Contains(page), reference = Find.WindowStack.Windows.Contains(page) ? bridge.Objects.Add(page) : null };
        });
    }

    private static object? Read()
    {
        if (Find.World?.grid is null) return null;
        var mod = LoadedModManager.GetMod<ClashOfRimMod>();
        return new
        {
            worldConfigurationId = mod.WorldConfigurationId,
            info = Scalars(Find.World.info),
            storyteller = Current.Game.storyteller.def.defName,
            difficulty = Current.Game.storyteller.difficultyDef.defName,
            difficultyValues = Scalars(Current.Game.storyteller.difficulty),
            layers = Find.World.grid.PlanetLayers.Values.OrderBy(l => l.LayerID).Select(Layer).ToArray(),
            features = Find.World.features.features.OrderBy(f => f.uniqueID).Select(f => new
            {
                id = f.uniqueID,
                defName = f.def.defName,
                name = f.name,
                center = new[] { f.drawCenter.x, f.drawCenter.y, f.drawCenter.z },
                size = f.maxDrawSizeInTiles
            }).ToArray(),
            factions = Find.FactionManager.AllFactionsListForReading.Where(f => !f.IsPlayer && f.def.defName != "ClashOfRim_PlayerProxy").OrderBy(f => f.loadID).Select(f => new
            {
                id = f.loadID,
                defName = f.def.defName,
                name = f.Name,
                ideology = ModsConfig.IdeologyActive ? Ideology(f.ideos?.PrimaryIdeo) : null
            }).ToArray(),
            ideologies = ModsConfig.IdeologyActive ? Find.IdeoManager.IdeosListForReading.OrderBy(i => i.id).Select(Ideology).ToArray() : [],
            pawns = Find.Maps.Where(m => m.IsPlayerHome).SelectMany(m => m.mapPawns.FreeColonistsSpawned).OrderBy(p => p.ThingID).Select(p => new
            {
                id = p.ThingID,
                name = p.Name?.ToStringFull,
                ideology = Ideology(p.Ideo),
                ideologyRegistered = p.Ideo is null || Find.IdeoManager.IdeosListForReading.Contains(p.Ideo)
            }).ToArray()
        };
    }

    private static object? Ideology(Ideo? i) => i is null ? null : new
    {
        id = i.id,
        name = i.name,
        culture = i.culture?.defName,
        foundation = i.foundation?.def?.defName,
        icon = i.iconDef?.defName,
        color = i.colorDef?.defName,
        memes = i.memes.Select(m => m.defName).Order(StringComparer.Ordinal).ToArray(),
        precepts = i.PreceptsListForReading.Select(p => p.def.defName).Order(StringComparer.Ordinal).ToArray()
    };

    private static object Layer(PlanetLayer layer)
    {
        using var geometry = new MemoryStream();
        using var writer = new BinaryWriter(geometry, Encoding.UTF8, true);
        writer.Write(layer.LayerID); writer.Write(layer.TilesCount);
        using var tiles = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var block = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var blocks = new List<string>();
        var neighbors = new List<PlanetTile>();
        for (int index = 0; index < layer.TilesCount; index++)
        {
            var center = layer.GetTileCenter(index);
            writer.Write(index); writer.Write(center.x); writer.Write(center.y); writer.Write(center.z);
            var tile = new PlanetTile(index, layer);
            neighbors.Clear(); Find.WorldGrid.GetTileNeighbors(tile, neighbors);
            var surface = tile.Tile as SurfaceTile;
            var data = new
            {
                index,
                biome = tile.Tile.PrimaryBiome.defName,
                values = CanonicalTileValues(tile.Tile),
                feature = tile.Tile.feature?.uniqueID,
                neighbors = neighbors.Select(n => n.tileId).Order().ToArray(),
                roads = surface?.Roads?.Select(r => new { defName = r.road.defName, neighbor = r.neighbor.tileId }).OrderBy(r => r.neighbor).ThenBy(r => r.defName, StringComparer.Ordinal).ToArray(),
                rivers = surface?.Rivers?.Select(r => new { defName = r.river.defName, neighbor = r.neighbor.tileId }).OrderBy(r => r.neighbor).ThenBy(r => r.defName, StringComparer.Ordinal).ToArray()
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(data);
            tiles.AppendData(bytes); block.AppendData(bytes);
            if ((index + 1) % 1024 == 0 || index == layer.TilesCount - 1) blocks.Add(Convert.ToHexString(block.GetHashAndReset()));
        }
        writer.Flush();
        return new
        {
            id = layer.LayerID,
            defName = layer.Def.defName,
            count = layer.TilesCount,
            geometrySha256 = Convert.ToHexString(SHA256.HashData(geometry.ToArray())),
            tilesSha256 = Convert.ToHexString(tiles.GetHashAndReset()),
            blocks
        };
    }

    private static SortedDictionary<string, object?> CanonicalTileValues(Tile tile)
    {
        var values = Scalars(tile);
        if (tile is SurfaceTile surface)
        {
            // 1.7 SurfaceLayer persists these as ushort/byte values. Compare the
            // representable data, not fresh-generation floats with loaded floats.
            // Keep raw floats available in clash.world.tiles for diagnostics.
            static int Quantize(float value, int max) => (int)Math.Clamp(MathF.Round(value), 0, max);
            values["elevation"] = Quantize((tile.WaterCovered ? tile.elevation : Math.Max(tile.elevation, 1f)) + 8192f, 65535);
            values["temperature"] = Quantize((tile.temperature + 300f) * 10f, 65535);
            values["rainfall"] = Quantize(tile.rainfall, 65535);
            values["swampiness"] = Quantize(tile.swampiness * 255f, 255);
            values["pollution"] = Quantize(tile.pollution * 65535f, 65535);
            values["riverDist"] = (byte)surface.riverDist;
        }
        return values;
    }

    internal static SortedDictionary<string, object?> Scalars(object value)
    {
        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            var type = field.FieldType;
            if (type.IsPrimitive || type == typeof(string) || type.IsEnum)
                result[field.Name] = type.IsEnum ? field.GetValue(value)?.ToString() : field.GetValue(value);
        }
        return result;
    }
}
