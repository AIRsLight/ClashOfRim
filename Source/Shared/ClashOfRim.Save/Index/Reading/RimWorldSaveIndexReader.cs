using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.Save;

public static class RimWorldSaveIndexReader
{
    private const string BattlefieldPawnMarkerHediffDefName = "ClashOfRim_RaidLootDissolver";

    public static SaveSnapshotIndex Read(string path, SaveIndexReadOptions? options = null)
    {
        options ??= new SaveIndexReadOptions();

        var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        XElement root = Required(document.Root, "savegame");
        XElement game = Required(root.Element("game"), "game");
        XElement? world = game.Element("world");
        SaveMetaSummary meta = ReadMeta(root);

        string? playerFactionUniqueLoadId = ReadPlayerFactionUniqueLoadId(game, world);
        var maps = ReadMaps(game, playerFactionUniqueLoadId).ToList();
        var things = new List<ThingSummary>();
        var pawns = new List<PawnSummary>();

        foreach (XElement mapElement in game.Element("maps")?.Elements("li") ?? Enumerable.Empty<XElement>())
        {
            string? mapUniqueId = Text(mapElement, "uniqueID");
            foreach (XElement thingElement in mapElement.Element("things")?.Elements("thing") ?? Enumerable.Empty<XElement>())
            {
                ThingSummary thing = ReadThing(thingElement, mapUniqueId, options.Identity);
                things.Add(thing);
                foreach (ThingSummary containedThing in ReadContainedThings(thingElement, thing, mapUniqueId, options, meta))
                {
                    things.Add(containedThing);
                }

                if (thing.IsPawn)
                {
                    pawns.Add(ReadPawn(thingElement, mapUniqueId, "map", options.Identity));
                }
            }
        }

        if (world?.Element("worldPawns") is XElement worldPawns)
        {
            foreach (XElement group in worldPawns.Elements())
            {
                foreach (XElement pawnElement in group.Elements("li"))
                {
                    string? localId = Text(pawnElement, "id");
                    if (!string.IsNullOrWhiteSpace(localId))
                    {
                        pawns.Add(ReadPawn(pawnElement, null, "worldPawns/" + group.Name.LocalName, options.Identity));
                    }
                }
            }
        }

        return new SaveSnapshotIndex(
            path,
            meta,
            ReadFactions(world).ToList(),
            ReadIndexExtensions(world, options, meta).ToList(),
            ReadWorldObjects(world).ToList(),
            maps,
            things,
            pawns)
        {
            HistoryPlayerColonistCount = ReadHistoryRoundedNonNegative(game, "Population", "FreeColonists"),
            HistoryPrisonerCount = ReadHistoryRoundedNonNegative(game, "Population", "Prisoners"),
            HistoryWealthItems = ReadHistoryRoundedNonNegative(game, "Wealth", "Wealth_Items"),
            HistoryWealthBuildings = ReadHistoryRoundedNonNegative(game, "Wealth", "Wealth_Buildings"),
            HistoryWealthPawns = ReadHistoryRoundedNonNegative(game, "Wealth", "Wealth_Pawns"),
            HistoryColonistMood = ReadHistoryRoundedNonNegative(game, "ColonistMood", "ColonistMood"),
            StoryColonistsLaunched = ReadStoryWatcherStatsNonNegative(game, "colonistsLaunched")
        };
    }

    private static IEnumerable<ThingSummary> ReadContainedThings(
        XElement thingElement,
        ThingSummary container,
        string? mapUniqueId,
        SaveIndexReadOptions options,
        SaveMetaSummary meta)
    {
        IReadOnlyList<ISaveIndexExtension> extensions = options.Extensions ?? Array.Empty<ISaveIndexExtension>();
        if (extensions.Count == 0)
        {
            yield break;
        }

        var context = new SaveIndexReadContext(options.Identity, mapUniqueId, meta);
        foreach (ISaveIndexExtension extension in extensions)
        {
            IReadOnlyList<ThingSummary> contained;
            try
            {
                contained = (extension.ReadContainedThings(thingElement, container, context)
                    ?? Enumerable.Empty<ThingSummary>()).ToList();
            }
            catch
            {
                continue;
            }

            foreach (ThingSummary thing in contained)
            {
                yield return thing;
            }
        }
    }

    private static SaveMetaSummary ReadMeta(XElement root)
    {
        XElement? meta = root.Element("meta");
        return new SaveMetaSummary(
            Text(meta, "gameVersion"),
            ListValues(meta?.Element("modIds")),
            ListValues(meta?.Element("modSteamIds")),
            ListValues(meta?.Element("modNames")));
    }

    private static IEnumerable<FactionSummary> ReadFactions(XElement? world)
    {
        IEnumerable<XElement> factions = world?
            .Element("factionManager")?
            .Element("allFactions")?
            .Elements("li") ?? Enumerable.Empty<XElement>();

        int index = 0;
        foreach (XElement faction in factions)
        {
            string loadId = Text(faction, "loadID") ?? index.ToString();
            yield return new FactionSummary(
                loadId,
                "Faction_" + loadId,
                Text(faction, "def"),
                Text(faction, "name"),
                Text(faction, "leader"),
                Bool(faction, "temporary"),
                Bool(faction, "hidden"),
                Bool(faction, "deactivated"));
            index++;
        }
    }

    private static IEnumerable<SaveIndexExtensionData> ReadIndexExtensions(
        XElement? world,
        SaveIndexReadOptions options,
        SaveMetaSummary meta)
    {
        IReadOnlyList<ISaveIndexExtension> extensions = options.Extensions ?? Array.Empty<ISaveIndexExtension>();
        if (extensions.Count == 0)
        {
            yield break;
        }

        var context = new SaveIndexReadContext(options.Identity, null, meta);
        foreach (ISaveIndexDataExtension extension in extensions.OfType<ISaveIndexDataExtension>())
        {
            IReadOnlyList<SaveIndexExtensionData> data;
            try
            {
                data = (extension.ReadIndexExtensions(world, context)
                    ?? Enumerable.Empty<SaveIndexExtensionData>()).ToList();
            }
            catch
            {
                continue;
            }

            foreach (SaveIndexExtensionData item in data)
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<WorldObjectSummary> ReadWorldObjects(XElement? world)
    {
        IEnumerable<XElement> objects = world?
            .Element("worldObjects")?
            .Element("worldObjects")?
            .Elements("li") ?? Enumerable.Empty<XElement>();

        foreach (XElement worldObject in objects)
        {
            string? id = Text(worldObject, "ID");
            yield return new WorldObjectSummary(
                id,
                string.IsNullOrWhiteSpace(id) ? null : "WorldObject_" + id,
                ClassName(worldObject),
                Text(worldObject, "def"),
                Text(worldObject, "tile"),
                Text(worldObject, "faction"),
                Text(worldObject, "nameInt") ?? Text(worldObject, "name"),
                Bool(worldObject, "destroyed"))
            {
                ClashOfRimRelatedEventId = Text(worldObject, "clashOfRimRelatedEventId")
            };
        }
    }

    private static IEnumerable<MapSummary> ReadMaps(XElement game, string? playerFactionUniqueLoadId)
    {
        float? historyWealthTotal = ReadLatestHistoryRecorderValue(game, "Wealth", "Wealth_Total");
        bool assignedHistoryWealth = false;
        IEnumerable<XElement> maps = game.Element("maps")?.Elements("li") ?? Enumerable.Empty<XElement>();
        foreach (XElement map in maps)
        {
            XElement? mapInfo = map.Element("mapInfo");
            IEnumerable<XElement> things = map.Element("things")?.Elements("thing") ?? Enumerable.Empty<XElement>();
            List<XElement> thingElements = things.ToList();
            IReadOnlyList<string> growingZoneCells = ReadGrowingZoneCells(map).ToList();
            float? wealthTotal = ReadMapWealthTotal(map);
            if (wealthTotal is null && !assignedHistoryWealth && historyWealthTotal is not null)
            {
                wealthTotal = historyWealthTotal;
                assignedHistoryWealth = true;
            }

            yield return new MapSummary(
                Text(map, "uniqueID"),
                Text(map, "generatedId"),
                Text(mapInfo, "parent"),
                Text(mapInfo, "size"),
                map.Element("compressedThingMapDeflate") != null || map.Element("compressedThingMap") != null,
                map.Element("terrainGrid") != null,
                map.Element("roofGrid") != null,
                map.Element("fogGrid") != null,
                thingElements.Count,
                thingElements.Count(IsPawnElement),
                growingZoneCells,
                wealthTotal,
                Bool(map, "wasSpawnedViaGravShipLanding"),
                thingElements.Count(thing => IsPlayerColonistElement(thing, playerFactionUniqueLoadId)));
        }
    }

    private static string? ReadPlayerFactionUniqueLoadId(XElement game, XElement? world)
    {
        string? playerFactionDef = Text(game.Element("scenario")?.Element("playerFaction"), "factionDef")
            ?? "PlayerColony";
        XElement? faction = world?
            .Element("factionManager")?
            .Element("allFactions")?
            .Elements("li")
            .FirstOrDefault(candidate => string.Equals(Text(candidate, "def"), playerFactionDef, StringComparison.Ordinal))
            ?? world?
                .Element("factionManager")?
                .Element("allFactions")?
                .Elements("li")
                .FirstOrDefault(candidate => string.Equals(Text(candidate, "def"), "PlayerColony", StringComparison.Ordinal));
        string? loadId = Text(faction, "loadID");
        return string.IsNullOrWhiteSpace(loadId) ? null : "Faction_" + loadId;
    }

    private static bool IsPlayerColonistElement(XElement thing, string? playerFactionUniqueLoadId)
    {
        return !string.IsNullOrWhiteSpace(playerFactionUniqueLoadId)
            && IsPawnElement(thing)
            && thing.Element("story") is not null
            && string.Equals(Text(thing, "faction"), playerFactionUniqueLoadId, StringComparison.Ordinal)
            && ReadPawnDead(thing, "map") != true;
    }

    private static float? ReadMapWealthTotal(XElement map)
    {
        string? value = Text(map.Element("wealthWatcher"), "wealthTotal")
            ?? Text(map.Element("wealthWatcher"), "wealthTotalCached");
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            ? Math.Max(0f, parsed)
            : null;
    }

    private static int? ReadHistoryRoundedNonNegative(
        XElement game,
        string recorderGroupDefName,
        string recorderDefName)
    {
        float? value = ReadLatestHistoryRecorderValue(game, recorderGroupDefName, recorderDefName);
        return value.HasValue
            ? Math.Max(0, (int)Math.Round(value.Value, MidpointRounding.AwayFromZero))
            : null;
    }

    private static int? ReadStoryWatcherStatsNonNegative(XElement game, string statName)
    {
        string? value = Text(game.Element("storyWatcher")?.Element("statsRecord"), statName);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? Math.Max(0, parsed)
            : null;
    }

    private static float? ReadLatestHistoryRecorderValue(
        XElement game,
        string recorderGroupDefName,
        string recorderDefName)
    {
        XElement? recorder = game
            .Element("history")
            ?.Element("autoRecorderGroups")
            ?.Elements("li")
            .Where(group => string.Equals(Text(group, "def"), recorderGroupDefName, StringComparison.Ordinal))
            .Elements("recorders")
            .Elements("li")
            .FirstOrDefault(candidate => string.Equals(Text(candidate, "def"), recorderDefName, StringComparison.Ordinal));
        try
        {
            byte[]? bytes = ReadHistoryRecorderBytes(recorder);
            if (bytes is null)
            {
                return null;
            }

            if (bytes.Length < sizeof(float))
            {
                return null;
            }

            int offset = bytes.Length - sizeof(float);
            float value = BitConverter.ToSingle(bytes, offset);
            return float.IsFinite(value)
                ? Math.Max(0f, value)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static byte[]? ReadHistoryRecorderBytes(XElement? recorder)
    {
        string? encodedDeflatedRecords = Text(recorder, "recordsDeflate");
        if (!string.IsNullOrWhiteSpace(encodedDeflatedRecords))
        {
            byte[] compressed = Convert.FromBase64String(encodedDeflatedRecords.Trim());
            using var source = new MemoryStream(compressed);
            using var deflate = new DeflateStream(source, CompressionMode.Decompress);
            using var target = new MemoryStream();
            deflate.CopyTo(target);
            return target.ToArray();
        }

        string? encodedRecords = Text(recorder, "records");
        return string.IsNullOrWhiteSpace(encodedRecords)
            ? null
            : Convert.FromBase64String(encodedRecords.Trim());
    }

    private static IEnumerable<string> ReadGrowingZoneCells(XElement map)
    {
        foreach (XElement zone in map
            .Element("zoneManager")?
            .Element("allZones")?
            .Elements("li") ?? Enumerable.Empty<XElement>())
        {
            string className = ClassName(zone) ?? string.Empty;
            if (!className.EndsWith("Zone_Growing", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (XElement cell in zone.Element("cells")?.Elements("li") ?? Enumerable.Empty<XElement>())
            {
                string normalized = NormalizeCell(cell.Value);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    yield return normalized;
                }
            }
        }
    }

    private static ThingSummary ReadThing(XElement thing, string? mapUniqueId, SnapshotIdentity identity)
    {
        string localId = Text(thing, "id") ?? "<missing>";
        string globalKey = identity.ThingKey(mapUniqueId, localId);
        if (IsPawnElement(thing) && HasBattlefieldPawnMarker(thing))
        {
            globalKey += "/battlefield-copy";
        }

        return new ThingSummary(
            localId,
            globalKey,
            mapUniqueId,
            ClassName(thing),
            Text(thing, "def"),
            Text(thing, "pos"),
            Text(thing, "faction"),
            Text(thing, "stackCount"),
            Text(thing, "health"),
            Text(thing, "stuff"),
            ReadThingQuality(thing),
            IsPawnElement(thing))
        {
            ClashOfRimOriginalThingId = Text(thing, "clashOfRimOriginalThingId")
                ?? Text(thing, "clashOfRimOriginalTrapId")
        };
    }

    private static string? ReadThingQuality(XElement thing)
    {
        XElement? compQuality = thing
            .Descendants("li")
            .FirstOrDefault(element => (ClassName(element) ?? string.Empty)
                .IndexOf("CompQuality", StringComparison.OrdinalIgnoreCase) >= 0);
        return Text(compQuality, "quality");
    }

    private static PawnSummary ReadPawn(XElement pawn, string? mapUniqueId, string source, SnapshotIdentity identity)
    {
        string localId = Text(pawn, "id") ?? "<missing>";
        string globalKey = identity.ThingKey(mapUniqueId, localId);
        if (HasBattlefieldPawnMarker(pawn))
        {
            globalKey += "/battlefield-copy";
        }

        return new PawnSummary(
            localId,
            globalKey,
            mapUniqueId,
            source,
            Text(pawn, "def"),
            Text(pawn, "kindDef"),
            ReadPawnName(pawn.Element("name")),
            ReadPawnDead(pawn, source),
            Text(pawn, "faction"),
            Text(pawn.Element("guest"), "hostFaction"));
    }

    private static bool IsPawnElement(XElement element)
    {
        return ClassName(element) == "Pawn" || element.Element("kindDef") != null;
    }

    private static bool HasBattlefieldPawnMarker(XElement pawn)
    {
        return pawn
            .Descendants("def")
            .Any(element => string.Equals(
                element.Value?.Trim(),
                BattlefieldPawnMarkerHediffDefName,
                StringComparison.Ordinal));
    }

    private static string? ReadPawnName(XElement? name)
    {
        if (name == null)
        {
            return null;
        }

        string? first = Text(name, "first");
        string? nick = Text(name, "nick");
        string? last = Text(name, "last");
        string? single = Text(name, "name");

        string joined = string.Join(" ", new[] { first, string.IsNullOrWhiteSpace(nick) ? null : $"\"{nick}\"", last }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        return string.IsNullOrWhiteSpace(joined) ? single : joined;
    }

    private static bool? ReadPawnDead(XElement pawn, string source)
    {
        if (source.EndsWith("/pawnsDead", StringComparison.Ordinal)
            || string.Equals(source, "worldPawns/pawnsDead", StringComparison.Ordinal))
        {
            return true;
        }

        string? healthState = Text(pawn.Element("healthTracker"), "healthState")
            ?? Text(pawn, "healthState");
        if (string.IsNullOrWhiteSpace(healthState))
        {
            return null;
        }

        return string.Equals(healthState, "Dead", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ClassName(XElement element)
    {
        return element.Attribute("Class")?.Value;
    }

    private static string? Text(XElement? element, string name)
    {
        string? value = element?.Element(name)?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        return value == "null" ? null : value;
    }

    private static string NormalizeCell(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray());
    }

    private static bool Bool(XElement element, string name)
    {
        string? value = Text(element, name);
        return bool.TryParse(value, out bool parsed) && parsed;
    }

    private static IReadOnlyList<string> ListValues(XElement? element)
    {
        return element?.Elements("li").Select(li => li.Value.Trim()).Where(value => value.Length > 0).ToList()
            ?? [];
    }

    private static XElement Required(XElement? element, string name)
    {
        return element ?? throw new InvalidDataException($"Missing required RimWorld save node: {name}");
    }
}
