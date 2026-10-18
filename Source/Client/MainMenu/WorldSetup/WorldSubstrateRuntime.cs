using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Modules.World;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim;

internal static class WorldSubstrateRuntime
{
    public static bool TryCapture(
        World? world,
        ModWorldTileGeometryDto? geometry,
        Func<bool> current,
        out byte[]? payload,
        out string? failure)
    {
        payload = null;
        failure = null;
        var grid = world?.grid; var features = world?.features; var landmarks = world?.landmarks;
        if (!current() || grid is null || features is null
            || (ModsConfig.OdysseyActive && landmarks is null))
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
            void RequireOwner() { if (!current()) throw new OperationCanceledException("World substrate source was replaced."); }
            int seed = world!.info?.persistentRandomValue ?? 0;
            string gridXml = CaptureXml(grid, "grid"); RequireOwner();
            string featuresXml = CaptureXml(features, "features"); RequireOwner();
            // A generated Core-only world has no landmark manager.
            string landmarksXml = landmarks is null ? "<landmarks />" : CaptureXml(landmarks, "landmarks"); RequireOwner();
            var package = new WorldSubstratePackage(seed, gridXml, featuresXml, landmarksXml, ModWorldTileGeometryBinaryCodec.Encode(geometry));
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
        out ClientWorldFeatureNames? names,
        out string? failure)
    {
        names = null; failure = null;
        var world = Find.World; var game = Current.Game;
        bool IsCurrent() => ReferenceEquals(world, Find.World) && ReferenceEquals(game, Current.Game);
        void RequireOwner() { if (!IsCurrent()) throw new OperationCanceledException("World substrate target was replaced."); }
        if (package is null || world?.grid is null) { failure = "World substrate or generated world is unavailable."; return false; }
        if (Scribe.mode != LoadSaveMode.Inactive) { failure = "Scribe is active."; return false; }
        string? temporaryPath = null;
        try
        {
            WorldGrid donor = LoadDonorGrid(package.GridXml, out temporaryPath);
            RequireOwner();
            CopyGridRawData(donor, world.grid);
            names = ClientWorldFeaturesRuntime.Restore(world, package.FeaturesXml, configuration, IsCurrent);
            RequireOwner();
            ApplyLandmarks(world, package.LandmarksXml, configuration, IsCurrent);
            RequireOwner();
            if (world.info is not null) world.info.persistentRandomValue = package.PersistentRandomValue;
            world.grid.StandardizeTileData();
            RequireOwner();
            foreach (PlanetLayer layer in world.grid.PlanetLayers.Values)
            {
                layer.FastTileFinder.RegenerateCache();
                layer.SetAllLayersDirty();
            }
            world.features.textsCreated = false;
            return true;
        }
        catch (Exception ex) { names = null; failure = ex.GetType().Name + ": " + ex.Message; return false; }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath))
                try { File.Delete(temporaryPath); } catch (IOException) { }
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
        try
        {
            Scribe.loader.InitLoading(temporaryPath);
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

    private static void ApplyLandmarks(World world, string landmarksXml, ModWorldConfigurationDto configuration, Func<bool> current)
    {
        if (!ModsConfig.OdysseyActive || world.landmarks is null)
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

        var landmarks = new Dictionary<PlanetTile, Landmark>();
        string? language = ClientWorldNameRuntime.ReadCurrentGameLanguage();
        bool localize = ClientWorldFeatureLabels.ShouldLocalize(configuration.GameLanguage, language);
        for (int index = 0; index < keys.Count; index++)
        {
            if (!TryParsePlanetTile(world, keys[index].Value, out PlanetTile tile))
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
                ? GenerateLandmarkName(def, tile, language) ?? values[index].Element("name")?.Value ?? def.LabelCap
                : values[index].Element("name")?.Value ?? def.LabelCap;
            landmark.isComboLandmark = bool.TryParse(values[index].Element("isComboLandmark")?.Value, out bool combo) && combo;
            landmarks[tile] = landmark;
        }
        if (!current()) return;
        if (!ClientWorldFeatureLabels.SameLanguage(language, ClientWorldNameRuntime.ReadCurrentGameLanguage()))
            throw new OperationCanceledException("World name language changed during landmark restoration.");
        world.landmarks.landmarks.Clear();
        foreach (var pair in landmarks) world.landmarks.landmarks.Add(pair.Key, pair.Value);
    }

    private static bool TryParsePlanetTile(World world, string value, out PlanetTile tile)
    {
        tile = PlanetTile.Invalid;
        string[] parts = value.Split(',');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tileId)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int layerId)
            || !world.grid.PlanetLayers.TryGetValue(layerId, out PlanetLayer? layer)
            || tileId < 0
            || tileId >= layer.TilesCount)
        {
            return false;
        }

        tile = new PlanetTile(tileId, layer);
        return true;
    }

    private static string? GenerateLandmarkName(LandmarkDef def, PlanetTile tile, string? language)
        => ClientWorldNameRuntime.Generate(def.nameMaker, string.Join("\u001f", def.defName,
            tile.Layer.LayerID.ToString(CultureInfo.InvariantCulture), tile.tileId.ToString(CultureInfo.InvariantCulture),
            language?.ToUpperInvariant()));
}
