using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

internal static class ClientWorldFeaturesRuntimeTests
{
    private static readonly List<int> seeds = new();
    private static int popped, currentSeed;
    private static bool failNames;
    private static string language = "French";
    private static Action? naming;
    internal static void Run()
    {
        var defs = DefDatabase<FeatureDef>.AllDefsListForReading.ToArray();
        var game = Current.Game; var creating = Current.CreatingWorld; var culture = CultureInfo.CurrentCulture;
        var harmony = new Harmony("ClashOfRim.Tests.WorldFeaturesRuntime");
        void Patch(System.Reflection.MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientWorldFeaturesRuntimeTests), prefix));
        Patch(AccessTools.Method(typeof(ClientWorldNameRuntime), nameof(ClientWorldNameRuntime.ReadCurrentGameLanguage)), nameof(Language));
        Patch(AccessTools.Method(typeof(Rand), nameof(Rand.PushState), new[] { typeof(int) }), nameof(Push));
        Patch(AccessTools.Method(typeof(Rand), nameof(Rand.PopState)), nameof(Pop));
        foreach (var method in typeof(NameGenerator).GetMethods().Where(method => method.Name == nameof(NameGenerator.GenerateName)
            && method.GetParameters().Length > 1 && method.GetParameters()[1].ParameterType == typeof(Predicate<string>))) Patch(method, nameof(Name));
        Patch(AccessTools.PropertyGetter(typeof(PlanetLayer), nameof(PlanetLayer.Selected)), nameof(Selected));
        try
        {
            var def = new FeatureDef { defName = "ClashFeatureTest", label = "default local label", nameMaker = new RulePackDef() };
            DefDatabase<FeatureDef>.Add(def);
            foreach (string name in new[] { "en-US", "fr-FR", "tr-TR", "zh-CN" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Topology(def); Naming(def); Failures(def);
            }
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id); DefDatabase<FeatureDef>.Clear(); foreach (var def in defs) DefDatabase<FeatureDef>.Add(def);
            Current.Game = game; Current.CreatingWorld = creating; CultureInfo.CurrentCulture = culture; naming = null; failNames = false;
        }
    }
    private static void Topology(FeatureDef def)
    {
        var f = new Fixture(def); var before = f.World.features.features.ToArray(); var links = f.Layers.Select(layer => layer.Tiles[0].feature).ToArray();
        seeds.Clear(); popped = 0;
        Check(ClientWorldFeaturesRuntime.Apply(f.World, f.Config, () => true) is null, "Accepted catalog was resubmitted.");
        Check(f.World.features.features.SequenceEqual(before) && f.World.features.features[0].uniqueID == 42 && f.World.features.features[0].layer == f.Layers[1]
            && f.World.features.features[0].drawAngle == 2.5f && f.Layers.Select(layer => layer.Tiles[0].feature).SequenceEqual(links), "Configuration rebuilt topology.");
        Check(before[0].name == "catalog-1" && before[1].name == "catalog-0", "Reordered feature labels were misassigned.");
        var catalog = ClientWorldFeaturesRuntime.Restore(f.World, f.Xml(), f.Config, () => true);
        var restored = f.World.features.features;
        Check(catalog is null && seeds.Count == 0 && restored[0].uniqueID == 42 && restored[1].uniqueID == 0
            && restored[0].layer == f.Layers[1] && restored[1].layer == f.Layers[0] && restored[0].drawAngle == 2.5f, "Substrate lost IDs, layer, angle or ignored language catalog.");
        Check(f.Layers[0].Tiles[0].feature == restored[1] && f.Layers[1].Tiles[0].feature == restored[0]
            && f.Layers.All(layer => layer.Tiles[1].feature is null && layer.Tiles[2].feature is null), "Raw feature links retained stale objects or crossed layers.");
        var extra = XElement.Parse(f.Xml());
        extra.Add(new XElement("unrelated", new XElement(extra.Descendants("li").First())));
        ClientWorldFeaturesRuntime.Restore(f.World, extra.ToString(), f.Config, () => true);
        Check(f.World.features.features.Count == 2, "Nested unrelated records were parsed as world features.");
        // IDs may coincide on different layers; links remain layer-qualified.
        f = new Fixture(def, sameIds: true); ClientWorldFeaturesRuntime.Restore(f.World, f.Xml(), f.Config, () => true);
        Check(f.Layers.All(layer => layer.Tiles[0].feature?.layer == layer), "A repeated ID linked to another layer.");
    }
    private static void Naming(FeatureDef def)
    {
        var f = new Fixture(def); f.Config.FeatureNameCatalogs.Clear(); seeds.Clear(); popped = 0;
        var generated = ClientWorldFeaturesRuntime.Restore(f.World, f.Xml(), f.Config, () => true);
        Check(generated is not null && generated.Features.Select(feature => feature.DrawCenterX).SequenceEqual(new[] { 0f, 1f }), "Generated catalog lost baseline order.");
        string[] names = f.World.features.features.Select(feature => feature.name).ToArray(); int[] firstSeeds = seeds.ToArray();
        seeds.Clear();
        var refresh = ClientWorldFeaturesRuntime.Apply(f.World, f.Config, () => true);
        Check(refresh is not null && seeds.SequenceEqual(firstSeeds) && f.World.features.features.Select(feature => feature.name).SequenceEqual(names)
            && popped == 4, "Restore and refresh use different naming or leak Rand state.");
        f.Config.FeatureNameCatalogs.Add(new() { WorldConfigurationId = "world", Language = "French", Features = generated!.Features.ToList() });
        seeds.Clear(); Check(ClientWorldFeaturesRuntime.Apply(f.World, f.Config, () => true) is null && seeds.Count == 0, "Accepted generated catalog did not stop regeneration.");
        f.Config.FeatureNameCatalogs.Clear(); failNames = true; seeds.Clear(); popped = 0;
        try
        {
            Check(ClientWorldFeaturesRuntime.Restore(f.World, f.Xml(), f.Config, () => true) is null
                && f.World.features.features.Select(feature => feature.name).SequenceEqual(new[] { "source-1", "source-0" }) && seeds.Count == popped,
                "Failed naming lost source labels, published a failed language catalog or leaked Rand state.");
        }
        finally { failNames = false; }
        var maker = def.nameMaker; def.nameMaker = null;
        try { Check(ClientWorldFeaturesRuntime.Apply(f.World, f.Config, () => true) is null && f.World.features.features[0].name == "source-1", "Missing name maker destroyed the supplied name."); }
        finally { def.nameMaker = maker; }
    }
    private static void Failures(FeatureDef def)
    {
        foreach (string mode in new[] { "duplicate", "negative-id", "nan", "vector" })
        {
            var f = new Fixture(def); var old = f.World.features.features.ToArray(); var xml = XElement.Parse(f.Xml()); var first = xml.Descendants("li").First();
            if (mode == "duplicate") first.Parent!.Add(new XElement(first));
            if (mode == "negative-id") first.SetElementValue("uniqueID", "-1");
            if (mode == "nan") first.SetElementValue("maxDrawSizeInTiles", "NaN");
            if (mode == "vector") first.SetElementValue("drawCenter", "(1,2)");
            try { ClientWorldFeaturesRuntime.Restore(f.World, xml.ToString(), f.Config, () => true); throw new Exception("Bad substrate accepted: " + mode); }
            catch (InvalidDataException) { }
            Check(f.World.features.features.SequenceEqual(old) && f.Layers[1].Tiles[0].feature == old[0], "Malformed feature XML partially changed the world.");
        }
        {
            var f = new Fixture(def); f.Config.FeatureNameCatalogs.Clear(); var old = f.World.features.features.ToArray(); bool current = true;
            naming = () => current = false;
            try
            {
                Check(ClientWorldFeaturesRuntime.Restore(f.World, f.Xml(), f.Config, () => current) is null && f.World.features.features.SequenceEqual(old), "Naming callback published into a replaced world.");
                Check(ClientWorldFeaturesRuntime.Apply(f.World, f.Config, () => current) is null && old[0].name == "live-1", "Stale projection mutated old labels.");
            }
            finally { naming = null; }
        }
        {
            var f = new Fixture(def); f.Config.FeatureNameCatalogs.Clear(); var old = f.World.features.features.ToArray();
            naming = () => language = "Italian";
            try
            {
                try { ClientWorldFeaturesRuntime.Restore(f.World, f.Xml(), f.Config, () => true); throw new Exception("Mixed-language restore accepted."); }
                catch (OperationCanceledException) { }
                Check(f.World.features.features.SequenceEqual(old), "Language change partially replaced substrate features.");
                language = "French";
                Check(ClientWorldFeaturesRuntime.Apply(f.World, f.Config, () => true) is null && old[0].name == "live-1", "Language change published mixed labels.");
            }
            finally { naming = null; language = "French"; }
        }
        {
            var f = new Fixture(def); SetRaw(f.Layers[0], Array.Empty<ushort>());
            ClientWorldFeaturesRuntime.Restore(f.World, f.Xml(), f.Config, () => true);
            Check(f.Layers[0].Tiles.All(tile => tile.feature is null), "Absent raw links retained generated-world references.");
        }
    }
    private sealed class Fixture
    {
        internal readonly World World = New<World>();
        internal readonly SurfaceLayer[] Layers = { Layer(0), Layer(7) };
        internal readonly ModWorldConfigurationDto Config = new() { WorldConfigurationId = "world", GameLanguage = "English" };
        internal Fixture(FeatureDef def, bool sameIds = false)
        {
            World.grid = New<WorldGrid>(); World.features = New<WorldFeatures>(); World.features.features = new();
            AccessTools.Field(typeof(WorldGrid), "planetLayers").SetValue(World.grid, new Dictionary<int, PlanetLayer> { [0] = Layers[0], [7] = Layers[1] });
            AccessTools.Field(typeof(WorldGrid), "surface").SetValue(World.grid, Layers[0]);
            for (int i = 0; i < 2; i++)
            {
                var feature = new WorldFeature { def = def, uniqueID = i == 0 || sameIds ? 0 : 42, layer = Layers[i], name = "live-" + i,
                    drawCenter = new Vector3(i, .3f, .5f), drawAngle = 2.5f, maxDrawSizeInTiles = 1.37f };
                World.features.features.Insert(0, feature);
                foreach (var tile in Layers[i].Tiles) tile.feature = feature;
                SetRaw(Layers[i], new ushort[] { (ushort)feature.uniqueID, ushort.MaxValue });
                Config.Features.Add(new() { DefName = def.defName, Label = "source-" + i, DrawCenterX = i, DrawCenterY = .3f, DrawCenterZ = .5f, MaxDrawSizeInTiles = 1.37f });
            }
            Config.FeatureNameCatalogs.Add(new() { WorldConfigurationId = "world", Language = "French", Features = Config.Features.Select((f, i) => new ModWorldFeatureDto
                { DefName = f.DefName, Label = "catalog-" + i, DrawCenterX = f.DrawCenterX, DrawCenterY = f.DrawCenterY, DrawCenterZ = f.DrawCenterZ, MaxDrawSizeInTiles = f.MaxDrawSizeInTiles }).ToList() });
        }
        internal string Xml() => new XElement("features", new XElement("features", World.features.features.Select(feature => new XElement("li",
            new XElement("def", feature.def.defName), feature.uniqueID == 0 ? null : new XElement("uniqueID", feature.uniqueID),
            new XElement("name", "source-" + (feature.layer.LayerID == 0 ? 0 : 1)), new XElement("layer", "PlanetLayer_" + feature.layer.LayerID),
            new XElement("drawCenter", "(" + feature.drawCenter.x.ToString(CultureInfo.InvariantCulture) + ",0.3,0.5)"), new XElement("drawAngle", "2.5"), new XElement("maxDrawSizeInTiles", "1.37"))))).ToString();
    }
    private static SurfaceLayer Layer(int id)
    {
        var layer = New<SurfaceLayer>(); AccessTools.Field(typeof(PlanetLayer), "layerId").SetValue(layer, id);
        AccessTools.Field(typeof(PlanetLayer), "tiles").SetValue(layer, new List<Tile> { new SurfaceTile(), new SurfaceTile(), new SurfaceTile() }); return layer;
    }
    private static void SetRaw(PlanetLayer layer, ushort[] ids) => AccessTools.Field(typeof(SurfaceLayer), "tileFeature").SetValue(layer, DataSerializeUtility.SerializeUshort(ids));
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static bool Language(ref string __result) { __result = language; return false; }
    private static bool Push(int __0) { seeds.Add(__0); currentSeed = __0; return false; }
    private static bool Pop() { popped++; return false; }
    private static bool Name(object[] __args, ref string __result)
    {
        naming?.Invoke(); if (failNames) throw new InvalidOperationException("expected name failure");
        __result = "localized-" + currentSeed.ToString(CultureInfo.InvariantCulture);
        Check(((Predicate<string>)__args[1])(__result), "Name predicate rejected fixture candidate."); return false;
    }
    private static bool Selected() => throw new InvalidOperationException("Feature application must not read the selected UI layer.");
}
