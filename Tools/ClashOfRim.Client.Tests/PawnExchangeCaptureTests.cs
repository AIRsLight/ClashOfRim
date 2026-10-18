using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using RimWorld;
using Verse;

internal static class PawnExchangeCaptureTests
{
    private const string Xml = "<pawn><name>测试-é</name><state>0.25</state></pawn>";
    private static string scribeMode = "";
    private static int serializations, extensions;
    private static readonly List<string> warnings = new();
    internal static void Run()
    {
        var previous = CultureInfo.CurrentCulture;
        var harmony = new Harmony("ClashOfRim.Tests.PawnExchangeCapture");
        void Patch(MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(PawnExchangeCaptureTests), prefix));
        Patch(AccessTools.Method(typeof(ScribeSaver), nameof(ScribeSaver.DebugOutputFor)), nameof(Serialize));
        Patch(AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.LabelShort)), nameof(Label));
        Patch(AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.LabelCapNoCount)), nameof(ItemLabel));
        Patch(AccessTools.Method(typeof(ClashOfRimCompatibilityApi), nameof(ClashOfRimCompatibilityApi.CollectPawnReferenceMetadata)), nameof(Metadata));
        Patch(AccessTools.Method(typeof(ClashOfRimCompatibilityApi), nameof(ClashOfRimCompatibilityApi.AppendPawnExchangeExtensions)), nameof(Extension));
        Patch(AccessTools.Method(typeof(Log), nameof(Log.Warning), new[] { typeof(string) }), nameof(Warning));
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            foreach (string path in new[] { "support-send", "support-return", "gift" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                serializations = extensions = 0; scribeMode = "";
                Pawn pawn = NewPawn(1), relative = NewPawn(2), codedOwner = NewPawn(3), secondLayer = NewPawn(4);
                pawn.relations = new Pawn_RelationsTracker(pawn);
                relative.relations = new Pawn_RelationsTracker(relative);
                var relationDef = new PawnRelationDef { defName = "Parent" };
                pawn.relations.DirectRelations.Add(new DirectPawnRelation(relationDef, relative, 0));
                pawn.relations.DirectRelations.Add(new DirectPawnRelation(relationDef, relative, 0));
                relative.relations.DirectRelations.Add(new DirectPawnRelation(relationDef, secondLayer, 0));
                pawn.equipment = new Pawn_EquipmentTracker(pawn);
                pawn.equipment.AllEquipmentListForReading.Add(Item(10, pawn));
                pawn.equipment.AllEquipmentListForReading.Add(Item(11, codedOwner));
                pawn.equipment.AllEquipmentListForReading.Add(Item(12, null));
                pawn.apparel = new Pawn_ApparelTracker(pawn);
                pawn.apparel.WornApparel.Add((Apparel)Item(13, relative, apparel: true));
                const string global = "owner:origin/pawn:Human999";
                var preserved = new Dictionary<string, string?> { ["context"] = "origin-metadata", ["optional"] = null, [" "] = "ignored" };
                var capture = new PawnExchangeCapture("sender", "colony", "snapshot");
                var package = path == "gift" ? GiftPawnPackageUtility.BuildPackage(pawn, global, "sender", "colony", "snapshot", "container")
                    : capture.Capture(pawn, global, preservedMetadata: path == "support-return" ? preserved : null);
                Require(serializations == 1 && extensions == 1 && package.Extensions.Single().ProviderId == "test", "Capture repeated or lost serialization/extension hooks.");
                Require(package.Reference!.GlobalId == global && package.Reference.SourceSnapshotId == "snapshot"
                    && package.Reference.Name == "pawn-1" && package.Reference.Dead == false
                    && package.Reference.Metadata["context"] == (path == "support-return" ? "origin-metadata" : "sender/colony")
                    && !package.Reference.Metadata.ContainsKey(" ") && preserved.Count == 3, "Root reference context or preserved metadata changed.");
                if (path == "support-return") Require(package.Reference.Metadata.ContainsKey("optional") && package.Reference.Metadata["optional"] is null, "Preserved null metadata was lost.");
                Require(package.Identity!.ThingDef == "Human" && package.Identity.PawnKindDef == "Colonist" && package.Identity.Gender == "Female"
                    && package.Appearance!.DisplayName == "pawn-1" && package.Status!.Dead == false, "Structured pawn fields changed.");
                Require(package.Equipment.Count == 3 && package.Apparel.Count == 1 && package.Relationships.Count == 2
                    && package.Relationships.All(r => r.OtherPawnGlobalId == PawnGlobalIdUtility.Build("sender", relative) && r.RelationDef == "Parent"), "Equipment containers or one-layer relations changed.");
                Require(package.Equipment[0].BiocodedPawnGlobalId == global, "Own coded weapon lost the explicit returning pawn identity.");
                Require(package.Equipment[1].BiocodedPawnGlobalId == PawnGlobalIdUtility.Build("sender", codedOwner), "Gift rewrote the coded owner to the carrier.");
                Require(package.Equipment[2].Biocoded == true && package.Equipment[2].BiocodedPawnGlobalId is null, "Unknown coded owner was invented.");
                Require(package.Equipment[0].GlobalId == global + "/equipment:Gun10" && package.Equipment[0].StackCount == 1
                    && package.Equipment[0].HitPoints == 37 && package.Equipment[0].Def == "Gun"
                    && package.Apparel[0].GlobalId == global + "/apparel:Shirt13"
                    && package.Apparel[0].BiocodedPawnGlobalId == PawnGlobalIdUtility.Build("sender", relative), "Equipment identity, quantity, condition or apparel binding changed.");
                Require(package.Scribe!.Xml == Xml && package.Scribe.XmlSha256 == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Xml))).ToLowerInvariant(), "Scribe bytes/hash changed with culture.");
                var references = package.Scribe.PawnReferenceReplacements;
                Require(references.Count == 2 && references.Select(r => r.SourceLoadId).ToHashSet().SetEquals(new[] { relative.GetUniqueLoadID(), codedOwner.GetUniqueLoadID() }), "References include self/second layer or omit a gear-only owner.");
                Require(references.All(r => r.Reference!.SourceSnapshotId == "snapshot" && r.Reference.Metadata["context"] == "sender/colony")
                    && references.Select(r => r.PlaceholderLoadId).Distinct().Count() == 2, "Replacement context was guessed from the global ID or collided.");
                VerifyReferenceDecoding(references);
                var repeated = capture.Capture(pawn, global, path == "gift" ? "container" : "");
                Require(references.Select(r => r.PlaceholderLoadId).SequenceEqual(repeated.Scribe!.PawnReferenceReplacements.Select(r => r.PlaceholderLoadId)), "Stable capture intent changed its reference IDs.");
                Require(pawn.equipment.AllEquipmentListForReading.Count == 3 && pawn.relations.DirectRelations.Count == 2 && !pawn.Destroyed, "Capture mutated the source pawn.");
            }
            VerifyLimitsAndFailures();
        }
        catch (Exception ex) { Console.WriteLine(ex.StackTrace); throw; }
        finally { harmony.UnpatchAll(harmony.Id); CultureInfo.CurrentCulture = previous; scribeMode = ""; warnings.Clear(); }
    }

    private static void VerifyReferenceDecoding(IReadOnlyList<ModPawnScribePawnReferenceReplacementDto> references)
    {
        // Placeholder names are opaque map entries, including packages written by the old gift builder.
        var read = typeof(PawnExchangeScribeRestorer).GetMethod("BuildPawnLoadDocument", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (string placeholder in new[] { references[1].PlaceholderLoadId, "ClashOfRimGiftPlaceholderPawn_legacy" })
        {
            var replacement = new ModPawnScribePawnReferenceReplacementDto
            { SourceLoadId = references[1].SourceLoadId, PlaceholderLoadId = placeholder, Reference = references[1].Reference };
            string xml = "<pawn><gearOwner>" + replacement.SourceLoadId + "</gearOwner><oldOwner>" + placeholder + "</oldOwner><name>local</name></pawn>";
            string restored = (string)read.Invoke(null, new object[] { xml, new[] { replacement } })!;
            Require(!restored.Contains("gearOwner") && !restored.Contains("oldOwner") && restored.Contains("<name>local</name>"), "Decoder leaked an external pawn ID or interpreted a placeholder prefix.");
        }
    }

    private static void VerifyLimitsAndFailures()
    {
        var pawn = NewPawn(1000); pawn.relations = new Pawn_RelationsTracker(pawn);
        var relation = new PawnRelationDef { defName = "Parent" };
        for (int i = 0; i < 130; i++) pawn.relations.DirectRelations.Add(new DirectPawnRelation(relation, NewPawn(2000 + i), 0));
        var capture = new PawnExchangeCapture("sender", "colony", null);
        var package = capture.Capture(pawn, "global");
        Require(package.Relationships.Count == 128 && package.Scribe!.PawnReferenceReplacements.Count == 128
            && package.Reference!.SourceSnapshotId is null, "Capture lost its reference bound or invented a snapshot.");
        foreach (string fault in new[] { "empty", "throws" })
        {
            scribeMode = fault; warnings.Clear(); int before = extensions;
            package = capture.Capture(NewPawn(1), "global");
            Require(package.Scribe is null && package.Identity!.ThingDef == "Human" && extensions == before + 1 && warnings.Count == 1, "Serialization failure discarded structured fields or bypassed extensions.");
        }
    }

    private static Pawn NewPawn(int id)
    {
        var pawn = new Pawn { def = new ThingDef { defName = "Human", race = new RaceProperties() }, kindDef = new PawnKindDef { defName = "Colonist" }, thingIDNumber = id, gender = Gender.Female };
        pawn.health = new Pawn_HealthTracker(pawn);
        return pawn;
    }
    private static ThingWithComps Item(int id, Pawn? owner, bool apparel = false)
    {
        ThingWithComps item = apparel ? new Apparel() : new ThingWithComps();
        item.def = new ThingDef { defName = apparel ? "Shirt" : "Gun", label = "item", useHitPoints = true };
        item.thingIDNumber = id; item.stackCount = 0; item.HitPoints = 37;
        var coding = new CompBiocodable { parent = item };
        AccessTools.Field(typeof(CompBiocodable), "codedPawn").SetValue(coding, owner);
        AccessTools.Field(typeof(CompBiocodable), "biocoded").SetValue(coding, true);
        AccessTools.Field(typeof(ThingWithComps), "comps").SetValue(item, new List<ThingComp> { coding });
        return item;
    }
    private static bool Serialize(ref string __result)
    {
        serializations++;
        if (scribeMode == "throws") throw new InvalidOperationException("Expected serializer failure");
        __result = scribeMode == "empty" ? " " : Xml; return false;
    }
    private static bool Label(Pawn __instance, ref string __result) { __result = "pawn-" + __instance.thingIDNumber.ToString(CultureInfo.InvariantCulture); return false; }
    private static bool ItemLabel(ref string __result) { __result = "item"; return false; }
    private static bool Metadata(Dictionary<string, string?> metadata, string? userId, string? colonyId) { metadata["context"] = userId + "/" + colonyId; return false; }
    private static bool Extension(ModPawnExchangePackageDto package) { extensions++; package.Extensions.Add(new() { ProviderId = "test", Kind = "probe" }); return false; }
    private static bool Warning(string __0) { warnings.Add(__0); return false; }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
