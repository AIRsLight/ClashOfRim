using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim;

internal static class WorldSubstrateRuntime
{
    public static bool TryCapture(
        ModWorldTileGeometryDto? geometry,
        out byte[]? payload,
        out string? failure)
    {
        payload = null;
        failure = null;
        if (Find.World?.grid is null || Find.World.features is null || Find.World.landmarks is null)
        {
            failure = "World structures are unavailable.";
            return false;
        }

        if (geometry is null || geometry.Layers.Count == 0)
        {
            failure = "World tile geometry is unavailable.";
            return false;
        }

        if (Scribe.mode != LoadSaveMode.Inactive)
        {
            failure = "Scribe is active.";
            return false;
        }

        try
        {
            var package = new WorldSubstratePackage(
                Find.World.info?.persistentRandomValue ?? 0,
                CaptureXml(Find.World.grid, "grid"),
                CaptureXml(Find.World.features, "features"),
                CaptureXml(Find.World.landmarks, "landmarks"),
                ModWorldTileGeometryBinaryCodec.Encode(geometry));
            payload = WorldSubstratePackageCodec.Encode(package);
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    public static bool TryApply(
        WorldSubstratePackage package,
        ModWorldConfigurationDto configuration,
        out string? failure)
    {
        failure = null;
        if (package is null || Find.World?.grid is null)
        {
            failure = "World substrate or generated world is unavailable.";
            return false;
        }

        if (Scribe.mode != LoadSaveMode.Inactive)
        {
            failure = "Scribe is active.";
            return false;
        }

        string? temporaryPath = null;
        try
        {
            WorldGrid donor = LoadDonorGrid(package.GridXml, out temporaryPath);
            CopyGridRawData(donor, Find.World.grid);
            ApplyFeatures(package.FeaturesXml, configuration);
            ApplyLandmarks(package.LandmarksXml, configuration);
            if (Find.World.info is not null)
            {
                Find.World.info.persistentRandomValue = package.PersistentRandomValue;
            }

            Find.World.grid.StandardizeTileData();
            foreach (PlanetLayer layer in Find.World.grid.PlanetLayers.Values)
            {
                layer.FastTileFinder.RegenerateCache();
                layer.SetAllLayersDirty();
            }

            Find.WorldFeatures.textsCreated = false;
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally
        {
            if (Scribe.mode != LoadSaveMode.Inactive)
            {
                Scribe.ForceStop();
            }

            if (!string.IsNullOrWhiteSpace(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static string CaptureXml(IExposable value, string expectedRoot)
    {
        string xml = Scribe.saver.DebugOutputFor(value);
        XDocument document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        XElement root = document.Root ?? throw new InvalidDataException("Scribe output has no root.");
        return new XElement(expectedRoot, root.Elements()).ToString(SaveOptions.DisableFormatting);
    }

    private static WorldGrid LoadDonorGrid(string gridXml, out string temporaryPath)
    {
        temporaryPath = Path.Combine(Path.GetTempPath(), "ClashOfRimWorldSubstrate-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(temporaryPath, "<savegame>" + gridXml + "</savegame>");
        var grid = new WorldGrid();
        Scribe.loader.InitLoading(temporaryPath);
        try
        {
            Scribe_Deep.Look(ref grid, "grid");
            Scribe.loader.FinalizeLoading();
            return grid;
        }
        catch
        {
            Scribe.ForceStop();
            throw;
        }
    }

    private static void CopyGridRawData(WorldGrid donor, WorldGrid target)
    {
        foreach (KeyValuePair<int, PlanetLayer> entry in donor.PlanetLayers)
        {
            if (!target.PlanetLayers.TryGetValue(entry.Key, out PlanetLayer? destination)
                || destination.GetType() != entry.Value.GetType()
                || destination.TilesCount != entry.Value.TilesCount
                || !string.Equals(destination.Def?.defName, entry.Value.Def?.defName, StringComparison.Ordinal))
            {
                throw new InvalidDataException("World substrate topology does not match the generated world.");
            }

            CopyRawByteArrays(entry.Value, destination);
            MethodInfo? rawDataToTiles = AccessTools.Method(destination.GetType(), "RawDataToTiles");
            if (rawDataToTiles is null)
            {
                throw new MissingMethodException(destination.GetType().FullName, "RawDataToTiles");
            }

            rawDataToTiles.Invoke(destination, null);
        }
    }

    private static void CopyRawByteArrays(PlanetLayer source, PlanetLayer destination)
    {
        for (Type? type = source.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType != typeof(byte[]))
                {
                    continue;
                }

                byte[]? data = field.GetValue(source) as byte[];
                field.SetValue(destination, data is null ? null : (byte[])data.Clone());
            }
        }
    }

    private static void ApplyFeatures(string featuresXml, ModWorldConfigurationDto configuration)
    {
        if (Find.WorldFeatures?.features is null)
        {
            return;
        }

        XElement root = XElement.Parse(featuresXml);
        List<XElement> elements = root.Descendants("li")
            .Where(element => element.Element("def") is not null && element.Element("uniqueID") is not null)
            .ToList();
        Find.WorldFeatures.features.Clear();
        var byId = new Dictionary<int, WorldFeature>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        bool localize = !string.Equals(configuration.GameLanguage, ReadLanguage(), StringComparison.OrdinalIgnoreCase);
        foreach (XElement element in elements)
        {
            FeatureDef? def = DefDatabase<FeatureDef>.GetNamedSilentFail(element.Element("def")?.Value);
            if (def is null || !int.TryParse(element.Element("uniqueID")?.Value, out int uniqueId))
            {
                continue;
            }

            PlanetLayer? layer = ResolveLayer(element.Element("layer")?.Value);
            if (layer is null)
            {
                continue;
            }

            string sourceName = element.Element("name")?.Value ?? def.label ?? def.defName;
            string name = localize ? GenerateFeatureName(def, uniqueId, usedNames) : sourceName;
            var feature = new WorldFeature
            {
                def = def,
                uniqueID = uniqueId,
                name = name,
                drawCenter = ParseVector(element.Element("drawCenter")?.Value),
                drawAngle = ParseFloat(element.Element("drawAngle")?.Value),
                maxDrawSizeInTiles = ParseFloat(element.Element("maxDrawSizeInTiles")?.Value),
                layer = layer
            };
            Find.WorldFeatures.features.Add(feature);
            byId[uniqueId] = feature;
            usedNames.Add(name);
        }

        foreach (PlanetLayer layer in Find.World.grid.PlanetLayers.Values)
        {
            byte[]? rawFeatureIds = AccessTools.Field(layer.GetType(), "tileFeature")?.GetValue(layer) as byte[];
            if (rawFeatureIds is null || rawFeatureIds.Length == 0)
            {
                continue;
            }

            ushort[] featureIds = DataSerializeUtility.DeserializeUshort(rawFeatureIds);
            for (int tile = 0; tile < Math.Min(layer.Tiles.Count, featureIds.Length); tile++)
            {
                layer.Tiles[tile].feature = featureIds[tile] == ushort.MaxValue
                    ? null
                    : byId.TryGetValue(featureIds[tile], out WorldFeature? feature) ? feature : null;
            }
        }
    }

    private static void ApplyLandmarks(string landmarksXml, ModWorldConfigurationDto configuration)
    {
        if (!ModsConfig.OdysseyActive || Find.World?.landmarks is null)
        {
            return;
        }

        XElement root = XElement.Parse(landmarksXml);
        List<XElement> keys = root.Descendants("keys").Elements("li").ToList();
        List<XElement> values = root.Descendants("values").Elements("li").ToList();
        if (keys.Count != values.Count)
        {
            throw new InvalidDataException("World substrate landmark keys and values do not match.");
        }

        Find.World.landmarks.landmarks.Clear();
        bool localize = !string.Equals(configuration.GameLanguage, ReadLanguage(), StringComparison.OrdinalIgnoreCase);
        for (int index = 0; index < keys.Count; index++)
        {
            if (!TryParsePlanetTile(keys[index].Value, out PlanetTile tile))
            {
                continue;
            }

            LandmarkDef? def = DefDatabase<LandmarkDef>.GetNamedSilentFail(values[index].Element("def")?.Value);
            if (def is null)
            {
                continue;
            }

            Landmark landmark = (Landmark)Activator.CreateInstance(def.workerClass, new object[] { def });
            landmark.name = localize
                ? GenerateLandmarkName(def, tile)
                : values[index].Element("name")?.Value ?? def.LabelCap;
            landmark.isComboLandmark = bool.TryParse(values[index].Element("isComboLandmark")?.Value, out bool combo) && combo;
            Find.World.landmarks.landmarks[tile] = landmark;
        }
    }

    private static PlanetLayer? ResolveLayer(string? savedLoadId)
    {
        const string prefix = "PlanetLayer_";
        if (string.IsNullOrWhiteSpace(savedLoadId))
        {
            return null;
        }

        string loadId = savedLoadId!;
        if (!loadId.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(loadId.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
        {
            return null;
        }

        return Find.World.grid.PlanetLayers.TryGetValue(id, out PlanetLayer? layer) ? layer : null;
    }

    private static bool TryParsePlanetTile(string value, out PlanetTile tile)
    {
        tile = PlanetTile.Invalid;
        string[] parts = value.Split(',');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tileId)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int layerId)
            || !Find.World.grid.PlanetLayers.TryGetValue(layerId, out PlanetLayer? layer)
            || tileId < 0
            || tileId >= layer.TilesCount)
        {
            return false;
        }

        tile = new PlanetTile(tileId, layer);
        return true;
    }

    private static Vector3 ParseVector(string? text)
    {
        string[] values = (text ?? string.Empty).Trim('(', ')').Split(',');
        return values.Length == 3
            ? new Vector3(ParseFloat(values[0]), ParseFloat(values[1]), ParseFloat(values[2]))
            : Vector3.zero;
    }

    private static float ParseFloat(string? text)
    {
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : 0f;
    }

    private static string GenerateFeatureName(FeatureDef def, int uniqueId, HashSet<string> usedNames)
    {
        if (def.nameMaker is null)
        {
            return def.label ?? def.defName;
        }

        Rand.PushState(GenText.StableStringHash(def.defName + "\u001f" + uniqueId.ToString(CultureInfo.InvariantCulture) + "\u001f" + ReadLanguage()));
        try
        {
            return NameGenerator.GenerateName(def.nameMaker, usedNames, false, "r_name");
        }
        finally
        {
            Rand.PopState();
        }
    }

    private static string GenerateLandmarkName(LandmarkDef def, PlanetTile tile)
    {
        if (def.nameMaker is null)
        {
            return def.LabelCap;
        }

        Rand.PushState(GenText.StableStringHash(def.defName + "\u001f" + tile.Layer.LayerID.ToString(CultureInfo.InvariantCulture) + "\u001f" + tile.tileId.ToString(CultureInfo.InvariantCulture) + "\u001f" + ReadLanguage()));
        try
        {
            return NameGenerator.GenerateName(def.nameMaker, null, false, "r_name");
        }
        finally
        {
            Rand.PopState();
        }
    }

    private static string ReadLanguage()
    {
        return LanguageDatabase.activeLanguage?.folderName ?? Prefs.LangFolderName ?? string.Empty;
    }
}
