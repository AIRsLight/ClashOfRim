using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.World;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim;

internal static class ClientWorldFeaturesRuntime
{
    private static readonly ClientWorldFeatureLabels Labels = new(new ClientWorldNameRuntime());
    internal static ClientWorldFeatureNames? Apply(World? world, ModWorldConfigurationDto configuration, Func<bool> current)
    {
        if (world?.features?.features is null) return null;
        var features = world.features.features.ToArray();
        if (features.Any(feature => feature.def is null || feature.layer is null)) return null;
        string? language = ClientWorldNameRuntime.ReadCurrentGameLanguage();
        var plan = Resolve(configuration, features, language);
        if (!plan.Matched || !current() || !ClientWorldFeatureLabels.SameLanguage(language, ClientWorldNameRuntime.ReadCurrentGameLanguage())) return null;
        bool changed = false;
        for (int i = 0; i < features.Length; i++)
            if (features[i].name != plan.Labels[i]) { features[i].name = plan.Labels[i]; changed = true; }
        if (changed) world.features.textsCreated = false;
        return plan.Catalog;
    }
    internal static ClientWorldFeatureNames? Restore(World world, string xml, ModWorldConfigurationDto configuration, Func<bool> current)
    {
        string? language = ClientWorldNameRuntime.ReadCurrentGameLanguage();
        var features = new List<WorldFeature>(); var byId = new Dictionary<(int Layer, int Id), WorldFeature>();
        var root = XElement.Parse(xml); var entries = (root.Element("features") ?? root).Elements("li");
        foreach (var element in entries.Where(element => element.Element("def") is not null))
        {
            var def = DefDatabase<FeatureDef>.GetNamedSilentFail(element.Element("def")?.Value);
            var layer = ResolveLayer(world, element.Element("layer")?.Value);
            if (def is null || layer is null) continue;
            if (!int.TryParse(element.Element("uniqueID")?.Value ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id < 0 || id >= ushort.MaxValue)
                throw new InvalidDataException("Invalid world feature ID.");
            var feature = new WorldFeature { def = def, uniqueID = id, layer = layer, name = element.Element("name")?.Value ?? def.label ?? def.defName,
                drawCenter = Vector(element.Element("drawCenter")?.Value), drawAngle = Number(element.Element("drawAngle")?.Value),
                maxDrawSizeInTiles = Number(element.Element("maxDrawSizeInTiles")?.Value) };
            if (!byId.TryAdd((layer.LayerID, id), feature)) throw new InvalidDataException("Duplicate world feature ID in a layer.");
            features.Add(feature);
        }
        var plan = Resolve(configuration, features, language);
        for (int i = 0; i < features.Count; i++) features[i].name = plan.Labels[i];
        // Decode before publishing, so a malformed layer does not partially replace links.
        var ids = world.grid.PlanetLayers.Values.ToDictionary(layer => layer.LayerID, layer =>
        {
            var raw = AccessTools.Field(layer.GetType(), "tileFeature")?.GetValue(layer) as byte[];
            return raw is { Length: > 0 } ? DataSerializeUtility.DeserializeUshort(raw) : Array.Empty<ushort>();
        });
        if (!current()) return null;
        if (!ClientWorldFeatureLabels.SameLanguage(language, ClientWorldNameRuntime.ReadCurrentGameLanguage()))
            throw new OperationCanceledException("World name language changed during restoration.");
        world.features.features.Clear(); world.features.features.AddRange(features);
        foreach (var layer in world.grid.PlanetLayers.Values)
        {
            var featureIds = ids[layer.LayerID];
            for (int i = 0; i < layer.Tiles.Count; i++)
                layer.Tiles[i].feature = i < featureIds.Length && featureIds[i] != ushort.MaxValue && byId.TryGetValue((layer.LayerID, featureIds[i]), out var feature) ? feature : null;
        }
        world.features.textsCreated = false;
        return plan.Catalog;
    }
    private static ClientWorldFeatureLabelsPlan Resolve(ModWorldConfigurationDto configuration, IReadOnlyList<WorldFeature> features, string? language)
        => Labels.Resolve(configuration, language, features.Select(feature =>
            new ClientWorldFeature(feature.uniqueID, feature.layer.LayerID, Capture(feature), feature.def.label ?? feature.def.defName)).ToArray());
    internal static List<ModWorldFeatureDto> ReadCurrentWorldFeatures()
        => Find.WorldFeatures?.features?.Where(feature => feature.def is not null).Select(Capture).ToList() ?? new();
    private static ModWorldFeatureDto Capture(WorldFeature feature) => new()
    { DefName = feature.def.defName, Label = feature.name, MaxDrawSizeInTiles = feature.maxDrawSizeInTiles,
        DrawCenterX = feature.drawCenter.x, DrawCenterY = feature.drawCenter.y, DrawCenterZ = feature.drawCenter.z };
    private static PlanetLayer? ResolveLayer(World world, string? loadId)
    {
        const string prefix = "PlanetLayer_";
        return loadId is not null && loadId.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(loadId.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
            && world.grid.PlanetLayers.TryGetValue(id, out var layer) ? layer : null;
    }
    private static Vector3 Vector(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Vector3.zero;
        var parts = text!.Trim('(', ')').Split(',');
        if (parts.Length != 3) throw new InvalidDataException("Invalid world feature draw center.");
        return new(Number(parts[0]), Number(parts[1]), Number(parts[2]));
    }
    private static float Number(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0f;
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && !float.IsNaN(value) && !float.IsInfinity(value)) return value;
        throw new InvalidDataException("Invalid world feature number: " + text);
    }
}
