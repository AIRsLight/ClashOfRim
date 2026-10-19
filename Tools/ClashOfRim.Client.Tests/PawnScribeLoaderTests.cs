using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Pawns;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using RimWorld;
using Verse;

internal static class PawnScribeLoaderTests
{
    private const string Xml = "<pawn><id>Human123</id><name>文化 İ</name><skills><li><level>12</level></li></skills></pawn>";
    private static readonly List<string> trace = new(), paths = new(), warnings = new();
    private static Pawn? loaded;
    private static string? fault, nativeXml;
    private static Exception failure = new InvalidOperationException("expected");
    private static bool stopFails;
    private static readonly Faction player = new();
    private static Action? onRegister;

    internal static void Run()
    {
        Check(Scribe.mode == LoadSaveMode.Inactive, "Fixture needs inactive Scribe.");
        var culture = CultureInfo.CurrentCulture;
        var harmony = new Harmony("ClashOfRim.Tests.PawnScribeLoader");
        void Patch(MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(PawnScribeLoaderTests), prefix));
        try
        {
            Patch(AccessTools.Method(typeof(ScribeLoader), nameof(ScribeLoader.InitLoading)), nameof(Init));
            Patch(AccessTools.Method(typeof(ScribeLoader), nameof(ScribeLoader.FinalizeLoading)), nameof(Finalize));
            Patch(AccessTools.Method(typeof(ScribeLoader), nameof(ScribeLoader.ForceStop)), nameof(Stop));
            Patch(AccessTools.Method(typeof(PawnScribeLoader), "ReadPawn"), nameof(Deep));
            Patch(AccessTools.Method(typeof(ClashOfRimText), nameof(ClashOfRimText.Key), new[] { typeof(string), typeof(NamedArgument[]) }), nameof(Text));
            Patch(AccessTools.Method(typeof(ClashOfRimText), nameof(ClashOfRimText.Key), new[] { typeof(string) }), nameof(Text));
            Patch(AccessTools.Method(typeof(Log), nameof(Log.Warning), new[] { typeof(string) }), nameof(Warning));
            Patch(AccessTools.Method(typeof(ClashLog), nameof(ClashLog.Message)), nameof(Quiet));
            Patch(AccessTools.Method(typeof(ClashOfRimCompatibilityApi), nameof(ClashOfRimCompatibilityApi.TryRegisterPawnExchangePackageExtensions)), nameof(Register));
            Patch(AccessTools.Method(typeof(PawnExchangeLoadIdUtility), nameof(PawnExchangeLoadIdUtility.LocalizeRestoredPawn)), nameof(Localize));
            Patch(AccessTools.Method(typeof(ClashOfRimCompatibilityApi), nameof(ClashOfRimCompatibilityApi.RestorePawnReferenceMetadata),
                new[] { typeof(Pawn), typeof(IReadOnlyDictionary<string, string?>), typeof(string) }), nameof(Metadata));
            Patch(AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfPlayer)), nameof(Player));
            Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.SetFaction)), nameof(SetFaction));
            Patch(AccessTools.Method(typeof(PawnExchangeScribeRestorer), "MarkPawnGlobalId"), nameof(Mark));
            Patch(AccessTools.Method(typeof(PawnExchangeScribeRestorer), "MergeRelationshipPlaceholdersForGlobalId"), nameof(Merge));
            Patch(AccessTools.Method(typeof(PawnExchangeScribeRestorer), "RestoreOneLayerRelationships"), nameof(Relations));
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Documents(); NativeLifetime(); Arrival(); Preview();
            }
        }
        finally
        {
            Scribe.mode = LoadSaveMode.Inactive;
            harmony.UnpatchAll(harmony.Id); CultureInfo.CurrentCulture = culture;
            onRegister = null; loaded = null; trace.Clear(); paths.Clear(); warnings.Clear();
        }
        NativeScribeIsolation();
        Console.WriteLine("PASS: pawn Scribe separation (four cultures; frozen XML, native failures/cleanup, arrival order; native generic isolation)");
    }

    private static void NativeScribeIsolation()
    {
        string path = Path.Combine(Path.GetTempPath(), "clash-pawn-scribe-isolation-" + Guid.NewGuid().ToString("N") + ".xml");
        using var nativeScribe = new NativeScribeTestScope();
        try
        {
            // A different reference type must still deserialize after this fixture.
            string xml = Scribe.saver.DebugOutputFor(new ScribeProbe { Value = 37 });
            File.WriteAllText(path, "<root>" + xml + "</root>");
            ScribeProbe? restored = null;
            Scribe.loader.InitLoading(path);
            Scribe_Deep.Look(ref restored, "saveable");
            Scribe.loader.FinalizeLoading();
            Check(restored?.Value == 37 && Scribe.mode == LoadSaveMode.Inactive,
                "Pawn fixture changed native Scribe behavior for another reference type.");
        }
        finally { Scribe.ForceStop(); File.Delete(path); }
    }

    private sealed class ScribeProbe : IExposable
    {
        public int Value;
        public void ExposeData() => Scribe_Values.Look(ref Value, "value");
    }

    private static ModPawnScribePayloadDto Payload(string xml = Xml) => new()
    { Xml = xml, XmlSha256 = ClientSnapshotPayloadCodec.Hash(Encoding.UTF8.GetBytes(xml)) };
    private static PawnScribeDocument Document(string xml = Xml)
    {
        Check(PawnScribeDocument.Read(Payload(xml), out var document) == PawnScribePayloadError.None, "Valid payload rejected.");
        return document!;
    }
    private static void Documents()
    {
        foreach (var payload in new ModPawnScribePayloadDto?[] { null, new(), new() { Xml = " \t" } })
            Check(PawnScribeDocument.Read(payload, out var missing) == PawnScribePayloadError.Missing && missing is null, "Empty Scribe admitted.");
        Check(PawnScribeDocument.Read(new() { Xml = new string('界', PawnScribeDocument.MaxScribeXmlBytes / 3 + 1) }, out _) == PawnScribePayloadError.TooLarge,
            "Size limit counted characters instead of UTF-8 bytes.");
        var dto = Payload(); dto.XmlSha256 = "wrong";
        Check(PawnScribeDocument.Read(dto, out _) == PawnScribePayloadError.HashMismatch, "Tampered Scribe admitted.");
        dto.XmlSha256 = Payload().XmlSha256!.ToUpperInvariant();
        Check(PawnScribeDocument.Read(dto, out _) == PawnScribePayloadError.None, "Uppercase wire digest rejected.");
        dto.XmlSha256 = null; Check(PawnScribeDocument.Read(dto, out _) == PawnScribePayloadError.None, "Legacy unhashed Scribe rejected.");

        const string source = "<thing Class='Pawn'><id>Human1</id><kindDef>Colonist</kindDef><name>文化 İ</name>"
            + "<pather><moving>True</moving></pather><jobs><curJob>attack</curJob></jobs><drafter><drafted>True</drafted></drafter>"
            + "<mindState><enemyTarget>Human2</enemyTarget><unrelated>keep</unrelated></mindState>"
            + "<social><directRelations><li><def>Parent</def><otherPawn>Thing_Human2</otherPawn></li></directRelations></social>"
            + "<sourceOwner>Human2</sourceOwner><placeholder>Opaque_2</placeholder><policy>DrugPolicy_4</policy><customPolicy>CustomPolicy_5</customPolicy>"
            + "<nested Class='Pawn'><id>Human9</id><kindDef>Colonist</kindDef></nested>"
            + "<equipment><li><id>Gun3</id><quality>6</quality><custom>0.25</custom></li></equipment>"
            + "<healthTracker><hediffs><li><def>Scar</def><severity>0.25</severity></li></hediffs></healthTracker>"
            + "<genes><xenogenes><li><def>Gene_İ</def></li></xenogenes></genes></thing>";
        dto = Payload(source);
        var replacement = new ModPawnScribePawnReferenceReplacementDto { SourceLoadId = "Human2", PlaceholderLoadId = "Opaque_2" };
        dto.PawnReferenceReplacements.Add(replacement);
        Check(PawnScribeDocument.Read(dto, out var document) == PawnScribePayloadError.None, "Complex document rejected.");
        dto.Xml = "<changed/>"; replacement.SourceLoadId = "changed"; replacement.PlaceholderLoadId = "changed"; dto.PawnReferenceReplacements.Clear();
        string Build() => document!.Build(static id => id.StartsWith("CustomPolicy_", StringComparison.Ordinal), "missing root");
        string built = Build(); var pawn = XDocument.Parse(built).Root!.Element("pawn")!;
        Check(pawn.Attribute("Class")!.Value == "Pawn" && pawn.Element("id")!.Value == "Human1" && pawn.Element("name")!.Value == "文化 İ",
            "Frozen payload borrowed caller edits or lost pawn identity/text.");
        Check(pawn.Element("sourceOwner") is null && pawn.Element("placeholder") is null && pawn.Element("nested") is null
            && pawn.Element("policy") is null && pawn.Element("customPolicy") is null && !pawn.Element("social")!.Descendants("li").Any(),
            "Load document retained cross-map pawn/policy references.");
        Check(pawn.Element("pather")!.Element("moving")!.Value == "False" && pawn.Element("jobs")!.Element("curJob")!.Attribute("IsNull")!.Value == "True"
            && pawn.Element("drafter")!.Element("drafted")!.Value == "False" && pawn.Element("mindState")!.Element("unrelated")!.Value == "keep",
            "Runtime reset discarded unrelated pawn state.");
        Check(pawn.Element("equipment")!.Descendants("quality").Single().Value == "6" && pawn.Element("healthTracker")!.Descendants("severity").Single().Value == "0.25"
            && pawn.Element("genes")!.Descendants("def").Single().Value == "Gene_İ" && Build() == built,
            "XML sanitation lost complex gear, health, genes or reused mutable output.");
        foreach (string malformed in new[] { "<pawn>", "<!DOCTYPE pawn [<!ENTITY x SYSTEM 'file:///does-not-exist'>]><pawn>&x;</pawn>" })
        {
            bool rejected = false;
            try { Document(malformed).Build(static _ => false, "missing root"); } catch (XmlException) { rejected = true; }
            Check(rejected, "Malformed XML or external entity was accepted.");
        }
    }

    private static void NativeLifetime()
    {
        Reset(); Check(PawnScribeLoader.TryLoad(Document(), "pawn", out var pawn, out _) && ReferenceEquals(pawn, loaded), "Native pawn result lost.");
        Check(trace.SequenceEqual(new[] { "init", "deep", "finalize" }), "Native loader performed arrival effects or reordered Scribe phases."); Clean();
        foreach (LoadSaveMode mode in Enum.GetValues<LoadSaveMode>().Where(x => x != LoadSaveMode.Inactive))
        {
            Reset(); Scribe.mode = mode;
            Check(!PawnScribeLoader.TryLoad(Document(), "pawn", out _, out _) && trace.Count == 0 && Scribe.mode == mode,
                "Loader interrupted another Scribe operation: " + mode);
            Scribe.mode = LoadSaveMode.Inactive;
        }
        foreach (string phase in new[] { "init", "deep", "finalize" })
        foreach (Exception error in new Exception[] { new IOException("io"), new UnauthorizedAccessException("access"), new XmlException("xml"), new InvalidOperationException("invalid"), new ArgumentException("arg"), new NullReferenceException("null") })
        {
            Reset(); fault = phase; failure = error;
            Check(!PawnScribeLoader.TryLoad(Document(), "pawn", out pawn, out var message) && pawn is null
                && message == "ClashOfRim.PawnExchange.StatusRestoreFailed" && trace.Last() == "stop", "Failure lost cleanup or escaped: " + phase);
            Clean();
        }
        Reset(); loaded = null;
        Check(!PawnScribeLoader.TryLoad(Document(), "pawn", out pawn, out var empty) && pawn is null
            && empty == "ClashOfRim.PawnExchange.StatusScribeReturnedNull", "Null native pawn was treated as success."); Clean();
        Reset(); fault = "init"; stopFails = true;
        Check(!PawnScribeLoader.TryLoad(Document(), "pawn", out _, out _) && warnings.Count == 2, "ForceStop failure suppressed diagnostics."); Clean();
        Reset(); fault = "deep"; failure = new NotSupportedException("unexpected");
        bool propagated = false;
        try { PawnScribeLoader.TryLoad(Document(), "pawn", out _, out _); } catch (NotSupportedException) { propagated = true; }
        Check(propagated && trace.Last() == "stop", "Unexpected failure was hidden or skipped cleanup."); Clean();
        Reset(); Check(!PawnScribeLoader.TryLoad(Document("<bad>"), "pawn", out _, out _) && trace.Count == 0, "Malformed XML reached native Scribe."); Clean();
    }

    private static void Arrival()
    {
        foreach (bool duringRegistration in new[] { false, true })
        {
            Reset();
            if (duringRegistration) onRegister = () => Scribe.mode = LoadSaveMode.Saving;
            else Scribe.mode = LoadSaveMode.Saving;
            Check(!PawnExchangeScribeRestorer.TryRestore(new() { Scribe = Payload() }, _ => true, "invalid", "pawn", out _, out _)
                && Scribe.mode == LoadSaveMode.Saving
                && trace.SequenceEqual(duringRegistration ? new[] { "register" } : Array.Empty<string>()),
                "Arrival registration or loader took over an existing Scribe session.");
            Scribe.mode = LoadSaveMode.Inactive;
        }
        foreach (bool forceFaction in new[] { false, true })
        {
            Reset(); var package = new ModPawnExchangePackageDto { Scribe = Payload(), Reference = new() { GlobalId = "origin/pawn:Human123" } };
            onRegister = () => package.Scribe.Xml = "<changed/>";
            Check(PawnExchangeScribeRestorer.TryRestore(package, _ => { trace.Add("validate"); return true; }, "invalid", "pawn", out var pawn, out _, forceFaction)
                && ReferenceEquals(pawn, loaded), "Arrival lost native result.");
            var expected = new List<string> { "register", "init", "deep", "finalize", "validate" };
            if (forceFaction) expected.Add("faction");
            expected.AddRange(new[] { "localize", "metadata", "mark", "merge", "relations" });
            Check(trace.SequenceEqual(expected), "Arrival side effects or their ordering changed: " + string.Join(",", trace)); Clean();
        }
        foreach (string phase in new[] { "payload", "register", "deep", "validator" })
        {
            Reset(); fault = phase;
            var package = new ModPawnExchangePackageDto { Scribe = phase == "payload" ? null : Payload() };
            Check(!PawnExchangeScribeRestorer.TryRestore(package, _ => false, "invalid", "pawn", out var pawn, out _) && pawn is null,
                "Rejected arrival published a pawn.");
            Check(!trace.Contains("localize") && !trace.Contains("mark") && !trace.Contains("relations"), "Failed arrival reached world commit: " + phase);
            if (phase == "payload") Check(trace.Count == 0, "Invalid payload registered extensions.");
            if (phase == "register") Check(trace.SequenceEqual(new[] { "register" }), "Rejected extension reached native loading.");
            Clean();
        }
    }

    private static void Preview()
    {
        const string xml = "<pawn><name>文化 İ</name><faction>Faction_5</faction><questTags><li>ClashOfRim.PawnGlobalId:real</li></questTags>"
            + "<ownership><ownedBed>Bed123</ownedBed></ownership><social><directRelations><li><def>Parent</def><otherPawn>Human123</otherPawn></li></directRelations></social>"
            + "<ideo><ideo>Ideo_4</ideo><certainty>0.75</certainty><joinTick>42</joinTick></ideo>"
            + "<royalty><titles><li><def>Duke</def><faction>Faction_5</faction></li></titles><heirs><keys><li>Faction_5</li></keys><values><li>Human123</li></values></heirs></royalty>"
            + "<healthTracker><hediffs><li><def>Scar</def><severity>0.25</severity></li></hediffs></healthTracker></pawn>";
        var token = ClashOfRimCompatibilityApi.CreateRegistrationToken("test-pawn-preview");
        try
        {
            ClashOfRimCompatibilityApi.UseRegistrationToken(token, () =>
            {
                ClashOfRimCompatibilityApi.RegisterPawnExchangePackageNormalizer(package => { _ = Rand.Value; });
                ClashOfRimCompatibilityApi.RegisterPawnPreviewRestorer((Pawn p, ModPawnExchangePackageDto package, out string message) =>
                {
                    Step("preview-metadata"); message = "preview rejected";
                    package.Reference!.GlobalId = "mutated-copy";
                    Check(ReferenceEquals(p, loaded), "Preview callback received another pawn.");
                    return fault != "preview-reject";
                });
            });
            foreach (string phase in new[] { "success", "init", "deep", "finalize", "preview-reject", "preview-metadata" })
            {
                Reset(); fault = phase;
                var package = new ModPawnExchangePackageDto { Scribe = Payload(xml), Reference = new() { GlobalId = "original" } };
                Rand.PushState(12345); float expected = Rand.Value; Rand.PopState();
                Rand.PushState(12345);
                try
                {
                    bool success = PawnPreviewScribeRestorer.TryRestore(package, out var pawn, out _);
                    Check(success == (phase == "success") && (success ? ReferenceEquals(pawn, loaded) : pawn is null), "Preview failure published a pawn: " + phase);
                    Check(Rand.Value == expected, "Preview advanced the simulation RNG: " + phase);
                }
                finally { Rand.PopState(); }
                Check(package.Reference.GlobalId == "original" && package.Scribe.Xml == xml, "Preview extension mutated caller-owned input.");
                Check(!trace.Any(x => x is "register" or "faction" or "localize" or "metadata" or "mark" or "merge" or "relations"),
                    "Preview entered real arrival side effects.");
                var root = XDocument.Parse(nativeXml!).Root!.Element("pawn")!;
                Check(root.Element("faction")!.Value == "null" && !root.Element("questTags")!.Elements().Any()
                    && !root.Element("ownership")!.Elements().Any() && !root.Element("social")!.Element("directRelations")!.Elements().Any(),
                    "Preview retained ownership or relationship bindings.");
                Check(root.Element("ideo")!.Attribute("Class")!.Value == typeof(PawnPreviewIdeoTracker).FullName
                    && root.Element("ideo")!.Element("certainty")!.Value == "0.75" && root.Element("ideo")!.Element("ideo")!.Value == "null"
                    && !root.Element("royalty")!.Element("titles")!.Elements().Any()
                    && root.Element("healthTracker")!.Descendants("severity").Single().Value == "0.25", "Preview lost state or left native ideology/title side effects enabled.");
                Clean();
            }
        }
        finally { ClashOfRimCompatibilityApi.RevokeRegistrationToken(token); }
    }

    private static void Reset()
    {
        Scribe.mode = LoadSaveMode.Inactive; trace.Clear(); paths.Clear(); warnings.Clear(); fault = null; onRegister = null; stopFails = false;
        failure = new InvalidOperationException("expected"); nativeXml = null;
        loaded = new Pawn { def = new ThingDef { defName = "Human", race = new RaceProperties { intelligence = Intelligence.Humanlike } }, thingIDNumber = 123 };
    }
    private static void Clean() => Check(Scribe.mode == LoadSaveMode.Inactive && paths.All(p => !File.Exists(p)), "Native Scribe or temporary file leaked.");
    private static void Step(string phase) { trace.Add(phase); if (fault == phase) throw failure; }
    private static bool Init(string __0)
    {
        nativeXml = File.ReadAllText(__0);
        paths.Add(__0); Check(File.Exists(__0) && XDocument.Load(__0).Root!.Element("pawn")!.Element("name")!.Value == "文化 İ", "Native loader received unfrozen/wrong XML.");
        Scribe.mode = LoadSaveMode.LoadingVars; Step("init"); return false;
    }
    private static bool Deep(ref Pawn? __0) { Step("deep"); _ = Rand.Value; __0 = loaded; return false; }
    private static bool Finalize() { Step("finalize"); Scribe.mode = LoadSaveMode.Inactive; return false; }
    private static bool Stop() { trace.Add("stop"); Scribe.mode = LoadSaveMode.Inactive; if (stopFails) throw new InvalidOperationException("stop"); return false; }
    private static bool Register(out string message, ref bool __result) { trace.Add("register"); onRegister?.Invoke(); message = "extension"; __result = fault != "register"; return false; }
    private static bool Localize() { Step("localize"); return false; }
    private static bool Metadata(ref string __result) { Step("metadata"); __result = ""; return false; }
    private static bool Player(ref Faction __result) { __result = player; return false; }
    private static bool SetFaction() { Step("faction"); return false; }
    private static bool Mark() { Step("mark"); return false; }
    private static bool Merge(ref int __result) { Step("merge"); __result = 0; return false; }
    private static bool Relations(ref int __result) { Step("relations"); __result = 0; return false; }
    private static bool Text(string key, ref string __result) { __result = key; return false; }
    private static bool Warning(string __0) { warnings.Add(__0); return false; }
    private static bool Quiet() => false;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
