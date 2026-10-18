using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Protocol;

internal static class ClientWorldFeatureLabelsTests
{
    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture; int count = 0;
        try
        {
            foreach (string name in new[] { "en-US", "fr-FR", "tr-TR", "zh-CN", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string world in new[] { "world", " world ", "WORLD", "other", "" }) { Catalog(world); count++; }
                foreach (string? baseline in new string?[] { "en-US", " fr-fr ", null, " " })
                    foreach (string? current in new string?[] { " FR-fr ", "en-US", null, " " }) { Language(baseline, current); count++; }
                foreach (float delta in new[] { 0.00005f, 0.0005f, float.NaN, float.PositiveInfinity, float.NegativeInfinity }) { Fingerprint(delta); count++; }
                foreach (string mode in new[] { "missing", "extra", "ambiguous", "nonfinite", "reordered" }) { Mapping(mode); count++; }
                foreach (string mode in new[] { "null", "blank", "duplicate", "success" }) { Generation(mode); count++; }
                StableSeeds(); count++;
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Console.WriteLine($"PASS: world feature labels ({count} scenarios)");
    }
    private static void Catalog(string world)
    {
        var f = new Fixture(); f.Config.FeatureNameCatalogs.Add(f.Catalog(world));
        var plan = f.Resolve(); bool valid = world.Trim() == "world";
        Check(plan.Matched && (plan.Catalog is null) == valid && (f.Generator.Seeds.Count == 0) == valid, "Catalog did not bind exact world identity: " + world);
        Check(plan.Labels.All(label => label.StartsWith(valid ? "catalog-" : "generated-", StringComparison.Ordinal)), "Wrong catalog labels applied.");
        if (!valid)
        {
            f.Config.FeatureNameCatalogs.Add(f.Catalog("world")); f.Generator.Seeds.Clear(); plan = f.Resolve();
            Check(plan.Catalog is null && f.Generator.Seeds.Count == 0 && plan.Labels[0] == "catalog-0", "Invalid first catalog hid a later valid catalog.");
        }
    }
    private static void Language(string? baseline, string? current)
    {
        var f = new Fixture(); f.Config.GameLanguage = baseline;
        var plan = f.Resolve(current);
        bool localize = !string.IsNullOrWhiteSpace(baseline) && !string.IsNullOrWhiteSpace(current)
            && !string.Equals(baseline.Trim(), current.Trim(), StringComparison.OrdinalIgnoreCase);
        Check(plan.Matched && (plan.Catalog is not null) == localize && f.Generator.Seeds.Count == (localize ? 3 : 0), "Unknown/equal language triggered localization.");
        if (localize) Check(plan.Catalog!.Language == current!.Trim(), "Catalog language not normalized.");
        else Check(plan.Labels.SequenceEqual(new[] { "source-0", "source-1", "source-2" }), "Original language lost supplied labels.");
    }
    private static void Fingerprint(float delta)
    {
        var f = new Fixture(); var catalog = f.Catalog("world"); catalog.Features[0].DrawCenterX = delta; f.Config.FeatureNameCatalogs.Add(catalog);
        var plan = f.Resolve(); bool match = delta == 0.00005f;
        Check((plan.Catalog is null) == match, "Catalog used different geometry tolerance/nonfinite rules.");
        var wire = new WorldFeatureDto("Feature", null, 1.37f, 0, .3f, .5f);
        var changed = new WorldFeatureDto("Feature", null, 1.37f, delta, .3f, .5f);
        Check(WorldFeatureGeometry.From(wire).Matches(WorldFeatureGeometry.From(changed)) == match, "Client/server geometry rules differ.");
    }
    private static void Mapping(string mode)
    {
        var f = new Fixture(); f.Config.FeatureNameCatalogs.Add(f.Catalog("world"));
        if (mode == "missing") f.Current.RemoveAt(0);
        if (mode == "extra") f.Current.Add(f.Current[0]);
        if (mode == "ambiguous")
        {
            f.Current[1] = f.Current[1] with { Data = Copy(f.Current[0].Data) };
            f.Config.Features[1] = Copy(f.Config.Features[0]);
        }
        if (mode == "nonfinite") f.Current[0].Data.DrawCenterX = float.NaN;
        if (mode == "reordered") f.Current.Reverse();
        var before = f.Current.Select(feature => feature.Data.Label).ToArray(); var plan = f.Resolve();
        if (mode == "reordered") Check(plan.Matched && plan.Labels.SequenceEqual(new[] { "catalog-2", "catalog-1", "catalog-0" }), "Catalog followed list position instead of geometry.");
        else Check(!plan.Matched && plan.Labels.SequenceEqual(before) && plan.Catalog is null && f.Generator.Seeds.Count == 0, "Mismatch/ambiguity rewrote topology labels or submitted a partial catalog: " + mode);
    }
    private static void Generation(string mode)
    {
        var f = new Fixture(); f.Generator.Failure = mode;
        var plan = f.Resolve();
        Check(plan.Matched && (plan.Catalog is not null) == (mode == "success"), "Failed naming was advertised as a complete language catalog.");
        Check(f.Current.Select(feature => feature.Data.Label).SequenceEqual(new[] { "live-0", "live-1", "live-2" }), "Policy mutated live inputs.");
        if (mode != "success") Check(plan.Labels[1] == "source-1", "Failed name generation discarded the source label.");
        else
        {
            f.Current.Reverse(); var reordered = f.Resolve();
            Check(reordered.Catalog!.Features.Select(feature => feature.DrawCenterX).SequenceEqual(new[] { 0f, 1f, 2f }), "Outgoing catalog did not retain server order/geometry.");
            Check(reordered.Labels.Reverse().SequenceEqual(plan.Labels), "Reordered input changed generated names.");
        }
    }
    private static void StableSeeds()
    {
        var f = new Fixture(); f.Resolve(" fr-FR "); var seeds = f.Generator.Seeds.ToArray();
        f.Generator.Seeds.Clear(); f.Current.Reverse(); f.Resolve("FR-fr");
        Check(f.Generator.Seeds.SequenceEqual(seeds), "Case, whitespace or input order changed the name seed.");
        var old = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); f.Generator.Seeds.Clear(); f.Resolve(); Check(f.Generator.Seeds.SequenceEqual(seeds), "CurrentCulture changed numeric seed components."); }
        finally { CultureInfo.CurrentCulture = old; }
    }
    private static ModWorldFeatureDto Copy(ModWorldFeatureDto f) => new()
    { DefName = f.DefName, Label = f.Label, MaxDrawSizeInTiles = f.MaxDrawSizeInTiles, DrawCenterX = f.DrawCenterX, DrawCenterY = f.DrawCenterY, DrawCenterZ = f.DrawCenterZ };
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private sealed class Fixture
    {
        internal readonly ModWorldConfigurationDto Config = new() { WorldConfigurationId = "world", GameLanguage = "en-US" };
        internal readonly List<ClientWorldFeature> Current = new();
        internal readonly Generator Generator = new();
        internal Fixture()
        {
            for (int i = 0; i < 3; i++)
            {
                var data = new ModWorldFeatureDto { DefName = "Feature", Label = "source-" + i, MaxDrawSizeInTiles = 1.37f, DrawCenterX = i, DrawCenterY = .3f, DrawCenterZ = .5f };
                Config.Features.Add(data); var live = Copy(data); live.Label = "live-" + i;
                Current.Add(new(i == 0 ? 19 : i, i == 1 ? 7 : 0, live, "default-" + i));
            }
        }
        internal ModWorldFeatureNameCatalogDto Catalog(string world) => new() { WorldConfigurationId = world, Language = " FR-fr ", Features = Config.Features.Select((f, i) => { var copy = Copy(f); copy.Label = "catalog-" + i; return copy; }).ToList() };
        internal ClientWorldFeatureLabelsPlan Resolve(string? language = "fr-FR") => new ClientWorldFeatureLabels(Generator).Resolve(Config, language, Current);
    }
    private sealed class Generator : IClientWorldFeatureNameGenerator
    {
        internal readonly List<string> Seeds = new(); internal string Failure = "success";
        public string? Generate(string def, string seed, Func<string, bool> available)
        {
            Seeds.Add(seed);
            if (Seeds.Count % 3 == 2)
                switch (Failure) { case "null": return null; case "blank": return " "; case "duplicate": return "source-0"; }
            string name = "generated-" + seed; Check(available(name), "Fixture generated a duplicate."); return name;
        }
    }
}
