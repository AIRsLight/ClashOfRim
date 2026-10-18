using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.DlcCompatibility;
using AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Genes;
using AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Pawns;
using AIRsLight.ClashOfRim.DlcCompatibility.Biotech.RemoteMaps;
using AIRsLight.ClashOfRim.Modules.Compatibility.Biotech;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using RimWorld;
using Verse;

internal static class BiotechPawnAndMapCapabilityTests
{
    private static UniqueIDsManager? ids;
    private static readonly List<Pawn> mapPawns = new();
    private static readonly List<string> temporaryPaths = new();
    private static CustomXenotype loadedXenotype = null!;
    private static bool loadSucceeds;
    private static int workNotifications;

    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture;
        var game = Current.Game;
        var harmony = new Harmony("ClashOfRim.Tests.BiotechPawnAndMapCapabilities");
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.UniqueIDsManager)), prefix: Method(nameof(Ids)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(MapPawns), nameof(MapPawns.AllPawns)), prefix: Method(nameof(Pawns)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Pawn_GeneTracker), nameof(Pawn_GeneTracker.CustomXenotype)), prefix: Method(nameof(CustomXenotype)));
            harmony.Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.Notify_DisabledWorkTypesChanged)), prefix: Method(nameof(WorkChanged)));
            harmony.Patch(AccessTools.Method(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveXenotype)), prefix: Method(nameof(SaveXenotype)));
            harmony.Patch(AccessTools.Method(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.TryLoadXenotype)), prefix: Method(nameof(LoadXenotype)));
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ja-JP" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                PayloadContract();
                ProjectionRules();
                PawnPackageAndDatabase();
                LoadedMapAndRegistration();
            }
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            Current.Game = game; CultureInfo.CurrentCulture = culture;
            ClashOfRimCompatibilityApi.ApplyServerDlcBaseline(null);
            ClashOfRimCompatibilityApi.BeginRegistrationCycle("client-tests");
            ids = null; mapPawns.Clear(); temporaryPaths.Clear(); loadedXenotype = null!;
        }
    }

    private static void PayloadContract()
    {
        var warnings = new List<string>();
        var custom = new BiotechSavedXenotypePackageDto { Name = "异种 İ", Xml = "<xenotype>\"İ\"</xenotype>", XmlSha256 = "abc" };
        var extension = BiotechPawnExchangePayload.Create("CustomDef", custom)!;
        Check(extension.ProviderId == "ludeon.rimworld.biotech" && extension.Kind == "rimworld.biotech.pawn-exchange"
            && extension.Metadata["xenotypeDef"] == "CustomDef" && extension.Metadata["customXenotypeName"] == custom.Name
            && extension.Metadata["customXenotypeSha256"] == "abc", "Pawn extension wire metadata changed.");
        using var json = JsonDocument.Parse(extension.PayloadJson!);
        Check(json.RootElement.GetProperty("name").GetString() == custom.Name && json.RootElement.GetProperty("xml").GetString() == custom.Xml
            && json.RootElement.GetProperty("xmlSha256").GetString() == "abc", "Custom xenotype JSON members or Unicode roundtrip changed.");
        var package = new ModPawnExchangePackageDto(); package.Extensions.Add(extension);
        Check(BiotechPawnExchangePayload.ReadCustomXenotype(package, warnings.Add)?.Xml == custom.Xml, "Custom payload did not roundtrip.");
        extension.ProviderId = extension.ProviderId.ToUpperInvariant();
        Check(BiotechPawnExchangePayload.ReadCustomXenotype(package, warnings.Add) is null, "Provider matching became culture-dependent or case-insensitive.");
        extension.ProviderId = BiotechCompatibilityKeys.PackageId; extension.PayloadJson = "not-json";
        package.Extensions.Add(BiotechPawnExchangePayload.Create(null, custom)!);
        Check(BiotechPawnExchangePayload.ReadCustomXenotype(package, warnings.Add) is null && warnings.Count == 1,
            "Malformed first extension stopped being diagnosed/ignored or selected a later duplicate instead.");
        Check(BiotechPawnExchangePayload.Create(null, null) is null && BiotechPawnExchangePayload.ReadCustomXenotype(null, warnings.Add) is null,
            "Empty metadata generated a spurious pawn extension.");
    }

    private static void ProjectionRules()
    {
        bool pawnEnabled = true, pollutionEnabled = true;
        var messages = new List<string>();
        var projection = new BiotechRemoteMapProjection(() => pawnEnabled, () => pollutionEnabled, messages.Add);
        var map = ProjectionXml(); var references = ProjectionXml();
        projection.Sanitize(map, references);
        foreach (var root in new[] { map, references })
        {
            var pawn = root.Element("thing")!;
            Check(pawn.Descendants("pawn").Single().Value == "Thing_Pawn7"
                && pawn.Descendants("overriddenByGene").First().Value == "null"
                && pawn.Descendants("overriddenByGene").Last().Value == "Gene_1"
                && pawn.Descendants("sourceGene").Single().Value == "null"
                && pawn.Descendants("linkedGene").Single().Value == "Gene_2"
                && pawn.Descendants("hediffs").Elements("li").Count() == 1,
                "Projection lost valid local gene links or retained cross-pawn/dependency state.");
            Check(root.Element("li")!.Descendants("gene").Single().Value == "Gene_999", "Non-pawn XML was sanitized as a pawn.");
        }
        string once = map.ToString(); projection.Sanitize(map, references);
        Check(map.ToString() == once && messages.Count == 2, "Repeated projection changed already sanitized XML or reported false changes.");
        pawnEnabled = false;
        var disabled = ProjectionXml(); string original = disabled.ToString(); projection.Sanitize(disabled, references);
        Check(disabled.ToString() == original, "Projection ignored its pawn exchange capability gate.");
        var area = new XElement("li", new XAttribute("Class", "Verse.Area_PollutionClear"));
        Check(projection.BuildAreaLoadId(area, 7) == "Area_7_PollutionClear", "Pollution area capability incorrectly depends on pawn exchange.");
        var prior = CultureInfo.CurrentCulture; var custom = (CultureInfo)prior.Clone(); custom.NumberFormat.NegativeSign = "minus";
        try { CultureInfo.CurrentCulture = custom; Check(projection.BuildAreaLoadId(area, -7) == "Area_-7_PollutionClear", "Area wire ID used current number formatting."); }
        finally { CultureInfo.CurrentCulture = prior; }
        pollutionEnabled = false;
        Check(projection.BuildAreaLoadId(area, 7) is null, "Pollution area provider ignored its own capability gate.");
    }

    private static void PawnPackageAndDatabase()
    {
        Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        Current.Game.customXenotypeDatabase = new CustomXenotypeDatabase();
        bool enabled = true; var warnings = new List<string>();
        var component = new BiotechPawnExchangeCompatibility(() => enabled, warnings.Add);
        var genes = new[] { new GeneDef { defName = "XenoA" }, new GeneDef { defName = "XenoB" } };
        var custom = new CustomXenotype { name = "共享异种 İ", genes = genes.ToList(), inheritable = true };
        var pawn = new Pawn(); pawn.genes = new Pawn_GeneTracker(pawn);
        Set(pawn.genes, "cachedHasCustomXenotype", true); Set(pawn.genes, "cachedCustomXenotype", custom);
        Set(pawn.genes, "xenotype", new XenotypeDef { defName = "FixtureXenotype" });
        var package = new ModPawnExchangePackageDto(); temporaryPaths.Clear();
        component.Append(pawn, package);
        Check(package.Extensions.Count == 1 && package.Extensions[0].Metadata["xenotypeDef"] == "FixtureXenotype"
            && temporaryPaths.Count == 1 && temporaryPaths.All(path => !File.Exists(path)), "Capture lost metadata or leaked its exported temp file.");
        loadedXenotype = new CustomXenotype { name = custom.name, genes = genes.Reverse().ToList(), inheritable = true };
        loadSucceeds = true;
        Check(component.TryRegister(package, out _) && Current.Game.customXenotypeDatabase.customXenotypes.Count == 1,
            "Valid custom xenotype was not registered in the current game's database.");
        loadedXenotype = custom;
        Check(component.TryRegister(package, out _) && Current.Game.customXenotypeDatabase.customXenotypes.Count == 1,
            "Equivalent reordered genes created a duplicate custom xenotype.");
        var payload = BiotechPawnExchangePayload.ReadCustomXenotype(package, warnings.Add)!;
        payload.XmlSha256 = "invalid";
        Check(!BiotechSavedXenotypePackages.TryRegister(payload, out _), "Hash mismatch was accepted.");
        payload.XmlSha256 = null; payload.Xml = new string('界', 400_000);
        Check(!BiotechSavedXenotypePackages.TryRegister(payload, out _), "UTF-8 byte-size limit was checked as a character count.");
        payload.Xml = "<xenotype/>"; loadSucceeds = false;
        Check(!BiotechSavedXenotypePackages.TryRegister(payload, out _) && temporaryPaths.All(path => !File.Exists(path)),
            "Failed engine loading was accepted or leaked its temporary import file.");
        Current.Game = null;
        Check(!BiotechSavedXenotypePackages.TryRegister(payload, out _), "Registration without a current game was accepted.");
        enabled = false; int pathsBefore = temporaryPaths.Count;
        component.Append(pawn, package);
        Check(component.TryRegister(package, out _) && package.Extensions.Count == 1 && temporaryPaths.Count == pathsBefore,
            "Disabled pawn capability still accessed xenotype persistence.");
        ids = new UniqueIDsManager(); component.ResetRestoredPawnGeneCaches(pawn);
        Check(Get(pawn.genes, "cachedHasCustomXenotype") is not null, "Disabled post-restore callback cleared gene state.");
        enabled = true; ids = null; component.ResetRestoredPawnGeneCaches(pawn);
        Check(Get(pawn.genes, "cachedHasCustomXenotype") is not null, "Post-restore callback ignored the missing ID-manager guard.");
        ids = new UniqueIDsManager(); component.ResetRestoredPawnGeneCaches(pawn);
        Check(Get(pawn.genes, "cachedHasCustomXenotype") is null && Get(pawn.genes, "cachedCustomXenotype") is null,
            "Post-restore callback stopped using the shared custom-xenotype cache invalidation.");
    }

    private static void LoadedMapAndRegistration()
    {
        ids = new UniqueIDsManager(); mapPawns.Clear(); workNotifications = 0;
        var pawn = new Pawn(); pawn.genes = new Pawn_GeneTracker(pawn);
        var gene = new Gene { def = new GeneDef { defName = "ValidRemoteGene" }, pawn = new Pawn(), loadID = 999 };
        pawn.genes.Endogenes.Add(gene); pawn.genes.Xenogenes.Add(null!); pawn.genes.Xenogenes.Add(new Gene());
        Set(pawn.genes, "cachedGenes", new List<Gene>()); mapPawns.Add(pawn); mapPawns.Add(new Pawn());
        var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map)); map.mapPawns = (MapPawns)RuntimeHelpers.GetUninitializedObject(typeof(MapPawns));
        bool enabled = false; var messages = new List<string>();
        var runtime = new BiotechRemoteMapRuntimeSanitizer(() => enabled, messages.Add);
        runtime.Sanitize(map); Check(pawn.genes.Xenogenes.Count == 2 && workNotifications == 0, "Disabled map cleanup changed genes.");
        enabled = true; runtime.Sanitize(map);
        Check(pawn.genes.Xenogenes.Count == 0 && ReferenceEquals(gene.pawn, pawn) && gene.loadID != 999
            && Get(pawn.genes, "cachedGenes") is null && workNotifications == 1 && messages.Single().Contains("removedGenes=2, relinkedGenes=1, localizedGenes=1"),
            "Loaded map cleanup lost invalid-gene removal, ownership, local IDs, cache invalidation or diagnostics.");

        ClashOfRimCompatibilityApi.BeginRegistrationCycle("biotech-pawn-map"); ClashOfRimCompatibilityApi.ApplyServerDlcBaseline(null);
        ClashOfRimCompatibilityApi.RegisterCompatibilityPackage(BiotechCompatibilityKeys.PackageId, () => true,
            new[] { BiotechCompatibilityKeys.PawnExchange, BiotechCompatibilityKeys.TradeMetadata, BiotechCompatibilityKeys.WorldPollution });
        BiotechCompatibilityPackage.RegisterCapabilities(); BiotechCompatibilityPackage.RegisterCapabilities();
        var area = new XElement("li", new XAttribute("Class", "Area_PollutionClear"));
        Check(ClashOfRimCompatibilityApi.BuildRemoteMapAreaLoadId(area, 4) == "Area_4_PollutionClear"
            && ClashOfRimCompatibilityApi.GetCompatibilityDefensePointDefNames().SequenceEqual(new[] { "ClashOfRim_MechDefensePoint" }),
            "Actual package registration lost pollution/defense capabilities or registered duplicates.");
        var projected = ProjectionXml(); var references = ProjectionXml();
        ClashOfRimCompatibilityApi.SanitizeRemoteMapProjection(new ModSnapshotPackageMetadataDto(), projected, references);
        Check(projected.Descendants("pawn").Single().Value == "Thing_Pawn7" && references.Descendants("pawn").Single().Value == "Thing_Pawn7",
            "Package projection binding failed to sanitize both map and reference-pawn roots.");
        workNotifications = 0;
        ClashOfRimCompatibilityApi.NotifyRemoteMapLoaded(map, null!, "unit", new ModSnapshotPackageMetadataDto());
        Check(workNotifications == 1, "Package loaded-map binding did not run exactly once.");
        var captured = new ModPawnExchangePackageDto(); Set(pawn.genes, "xenotype", new XenotypeDef { defName = "RegisteredPawnType" });
        ClashOfRimCompatibilityApi.AppendPawnExchangeExtensions(pawn, captured);
        var extension = captured.Extensions.Single(); var metadata = extension.Metadata;
        ClashOfRimCompatibilityApi.NormalizePawnExchangePackage(captured);
        Check(ReferenceEquals(extension, captured.Extensions.Single()) && ReferenceEquals(metadata, extension.Metadata)
            && metadata["xenotypeDef"] == "RegisteredPawnType", "Removing the no-op normalizer changed extension identity or contents.");
        ClashOfRimCompatibilityApi.ApplyServerDlcBaseline(Array.Empty<string>());
        Check(ClashOfRimCompatibilityApi.BuildRemoteMapAreaLoadId(area, 4) is null
            && !ClashOfRimCompatibilityApi.GetCompatibilityDefensePointDefNames().Any(), "Registered capabilities ignored a changed DLC baseline.");
        ClashOfRimCompatibilityApi.ApplyServerDlcBaseline(null);
        foreach (string capability in new[] { BiotechCompatibilityKeys.PawnExchange, BiotechCompatibilityKeys.TradeMetadata,
                     BiotechCompatibilityKeys.WorldPollution, BiotechCompatibilityKeys.WorldGeneration })
        {
            ClashOfRimCompatibilityApi.BeginRegistrationCycle("biotech-single-capability");
            ClashOfRimCompatibilityApi.RegisterCompatibilityPackage(BiotechCompatibilityKeys.PackageId, () => true, new[] { capability });
            BiotechCompatibilityPackage.RegisterCapabilities();
            Check(ClashOfRimCompatibilityApi.GetCompatibilityDefensePointDefNames().Any() == (capability != BiotechCompatibilityKeys.WorldGeneration)
                && (ClashOfRimCompatibilityApi.BuildRemoteMapAreaLoadId(area, 4) is not null) == (capability == BiotechCompatibilityKeys.WorldPollution),
                "Defense or pollution capability incorrectly borrowed another capability's gate: " + capability);
        }
    }

    private static XElement ProjectionXml() => XElement.Parse(@"<map><thing><id>Pawn7</id><kindDef>Colonist</kindDef><pather/><jobs/>
<genes><endogenes><li><def>A</def><loadID>1</loadID><pawn>Thing_Remote</pawn><overriddenByGene>Gene_99</overriddenByGene></li>
<li><def>B</def><loadID>2</loadID><overriddenByGene>Gene_1</overriddenByGene></li></endogenes></genes>
<healthTracker><hediffSet><hediffs><li><def>GeneticDrugNeed</def></li><li Class='RimWorld.Hediff_ChemicalDependency'/>
<li><def>Other</def><sourceGene>Gene_99</sourceGene><linkedGene>Gene_2</linkedGene></li></hediffs></hediffSet></healthTracker>
</thing><li><genes><gene>Gene_999</gene></genes></li></map>");
    private static HarmonyMethod Method(string name) => new(typeof(BiotechPawnAndMapCapabilityTests), name);
    private static object? Get(object target, string field) => AccessTools.Field(target.GetType(), field).GetValue(target);
    private static void Set(object target, string field, object? value) => AccessTools.Field(target.GetType(), field).SetValue(target, value);
    private static bool Ids(ref UniqueIDsManager? __result) { __result = ids; return false; }
    private static bool Pawns(ref List<Pawn> __result) { __result = mapPawns; return false; }
    private static bool CustomXenotype(Pawn_GeneTracker __instance, ref CustomXenotype? __result)
    {
        __result = Get(__instance, "cachedCustomXenotype") as CustomXenotype;
        return false;
    }
    private static bool WorkChanged() { workNotifications++; return false; }
    private static bool SaveXenotype(CustomXenotype __0, string __1) { temporaryPaths.Add(__1); File.WriteAllText(__1, "<xenotype>共享</xenotype>"); return false; }
    private static bool LoadXenotype(string __0, ref CustomXenotype __1, ref bool __result)
    {
        temporaryPaths.Add(__0); Check(File.Exists(__0), "Import did not write the engine input file.");
        __1 = loadedXenotype; __result = loadSucceeds; return false;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
