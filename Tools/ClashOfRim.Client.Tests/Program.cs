using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.CoreCompatibility;
using AIRsLight.ClashOfRim.DlcCompatibility;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Xml.Linq;
using Verse;

var tests = new (string Name, Action Run)[]
{
    ("registered rejection is shared by UI and authoritative preparation", RegisteredRejectionIsShared),
    ("registered capture and finalize handlers run", RegisteredCaptureAndFinalizeHandlersRun),
    ("unfinished things are rejected", UnfinishedThingsAreRejected),
    ("fertilized eggs retain progress but not parents", FertilizedEggsRetainProgressButNotParents),
    ("ideology relics are rejected", IdeologyRelicsAreRejected),
    ("adopted npc ideologies fork without splitting shared player ideologies", AdoptedNpcIdeologiesFork),
    ("biotech operation targets are cleared", BiotechOperationTargetsAreCleared),
    ("unnatural corpses are rejected", UnnaturalCorpsesAreRejected),
    ("unfinished thing defs are hidden from request lists", UnfinishedThingDefsAreHidden),
    ("prepared concrete things carry transfer policy metadata", PreparedThingsCarryPolicyMetadata),
    ("server entry diagnostics list mods in load order", ServerEntryDiagnosticsListModsInLoadOrder),
    ("only accepted language warnings can be bypassed", OnlyAcceptedLanguageWarningsCanBeBypassed),
    ("visible compatibility warnings request the full server manifest", VisibleCompatibilityWarningsRequestFullManifest),
    ("selection labels show a single ellipsis", SelectionLabelsShowSingleEllipsis),
    ("remote projection defers addiction need lookup until pawn needs load", RemoteProjectionDefersAddictionNeedLookup),
    ("hidden trap proxies retain source snapshot identity", HiddenTrapProxiesRetainSourceSnapshotIdentity),
    ("defense duty returns to its radius without consuming wander cadence", DefenseDutyUsesIndependentReturnNode)
};

foreach ((string name, Action run) in tests)
{
    try
    {
        Console.WriteLine("RUN: " + name);
        run();
        Console.WriteLine("PASS: " + name);
    }
    catch (Exception ex)
    {
        Console.WriteLine("FAIL-TYPE: " + ex.GetType().FullName);
        try
        {
            Console.WriteLine("FAIL-MESSAGE: " + ex.Message);
        }
        catch
        {
            Console.WriteLine("FAIL-MESSAGE: <unavailable>");
        }

        Environment.ExitCode = 1;
        break;
    }
}

static void RegisteredRejectionIsShared()
{
    BeginCycle();
    ClashOfRimCompatibilityApi.RegisterThingTransferRule(
        "test.reject",
        (Thing thing, ThingTransferContext context) =>
            ThingTransferDecision.Reject("test.forbidden"),
        null,
        null);

    Thing thing = MakeThing("ForbiddenThing");
    Assert(!ClashOfRimCompatibilityApi.CanTransferThing(
        thing,
        ThingTransferContext.Outbound("trade"),
        out string? uiReason));
    Assert(uiReason == "test.forbidden");

    var reference = new ModThingReferenceDto { GlobalKey = "thing:1", DefName = "ForbiddenThing", StackCount = 1 };
    Assert(!ClashOfRimCompatibilityApi.PrepareThingTransfer(
        thing,
        reference,
        ThingTransferContext.Outbound("trade"),
        out string? authoritativeReason));
    Assert(authoritativeReason == uiReason);
}

static void RegisteredCaptureAndFinalizeHandlersRun()
{
    BeginCycle();
    bool finalized = false;
    ClashOfRimCompatibilityApi.RegisterThingTransferRule(
        "test.capture",
        null,
        (Thing thing, ModThingReferenceDto reference, ThingTransferContext context) =>
            reference.Metadata["test.capture"] = context.Surface,
        (ModThingReferenceDto reference, Thing thing, ThingTransferContext context, out string? missingDefName) =>
        {
            missingDefName = null;
            finalized = reference.Metadata.TryGetValue("test.capture", out string? value)
                && value == context.Surface;
            return finalized;
        });

    Thing thing = MakeThing("CapturedThing");
    var reference = new ModThingReferenceDto { GlobalKey = "thing:2", DefName = "CapturedThing", StackCount = 1 };
    ThingTransferContext context = ThingTransferContext.Outbound("gift");
    Assert(ClashOfRimCompatibilityApi.PrepareThingTransfer(thing, reference, context, out _));
    Assert(reference.Metadata["test.capture"] == "gift");
    Assert(ClashOfRimCompatibilityApi.FinalizeThingTransfer(reference, thing, context, out _));
    Assert(finalized);
}

static void UnfinishedThingsAreRejected()
{
    BeginCycle();
    CoreThingTransferCompatibility.Apply();
    var thing = (UnfinishedThing)FormatterServices.GetUninitializedObject(typeof(UnfinishedThing));
    thing.def = MakeDef("UnfinishedGun");

    Assert(!ClashOfRimCompatibilityApi.CanTransferThing(
        thing,
        ThingTransferContext.Outbound("trade"),
        out string? reason));
    Assert(reason == CoreThingTransferCompatibility.UnfinishedThingRejectionCode);
}

static void FertilizedEggsRetainProgressButNotParents()
{
    BeginCycle();
    CoreThingTransferCompatibility.Apply();
    ThingWithComps egg = MakeThingWithComp("EggTestFertilized", out CompHatcher hatcher);
    Pawn parent = (Pawn)FormatterServices.GetUninitializedObject(typeof(Pawn));
    Faction sourceFaction = (Faction)FormatterServices.GetUninitializedObject(typeof(Faction));
    Faction receivingFaction = (Faction)FormatterServices.GetUninitializedObject(typeof(Faction));
    hatcher.hatcheeParent = parent;
    hatcher.otherParent = parent;
    hatcher.hatcheeFaction = sourceFaction;
    typeof(CompHatcher).GetField("gestateProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(hatcher, 0.625f);

    var reference = new ModThingReferenceDto { GlobalKey = "egg:1", DefName = egg.def.defName, StackCount = 1 };
    Assert(ClashOfRimCompatibilityApi.PrepareThingTransfer(
        egg,
        reference,
        ThingTransferContext.Outbound("gift"),
        out _));

    ThingWithComps restored = MakeThingWithComp("EggTestFertilized", out CompHatcher restoredHatcher);
    restoredHatcher.hatcheeParent = parent;
    restoredHatcher.otherParent = parent;
    restoredHatcher.hatcheeFaction = sourceFaction;
    Assert(ClashOfRimCompatibilityApi.FinalizeThingTransfer(
        reference,
        restored,
        ThingTransferContext.Inbound("gift", receivingFaction),
        out _));

    float progress = (float)typeof(CompHatcher)
        .GetField("gestateProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(restoredHatcher)!;
    Assert(Math.Abs(progress - 0.625f) < 0.0001f);
    Assert(restoredHatcher.hatcheeParent is null);
    Assert(restoredHatcher.otherParent is null);
    Assert(ReferenceEquals(restoredHatcher.hatcheeFaction, receivingFaction));
}

static void HiddenTrapProxiesRetainSourceSnapshotIdentity()
{
    var identities = new[]
    {
        new RemoteMapThingIdentityRecord
        {
            MapUniqueId = "3",
            ProjectedThingId = "cor_remote_raid_TrapIED_Smoke24411",
            OriginalThingId = "TrapIED_Smoke24411"
        }
    };

    Assert(RemoteMapThingIdentityResolver.TryResolveOriginalThingId(
        identities,
        "Map_3",
        "Thing_cor_remote_raid_TrapIED_Smoke24411",
        out string originalThingId));
    Assert(originalThingId == "TrapIED_Smoke24411");
}

static void IdeologyRelicsAreRejected()
{
    BeginCycle();
    IdeologyThingTransferCompatibility.Apply();
    ThingWithComps relicThing = MakeThingWithComp("RelicWeapon", out CompStyleable styleable);
    typeof(CompStyleable).GetField("sourcePrecept", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(styleable, FormatterServices.GetUninitializedObject(typeof(Precept_Relic)));

    Assert(!ClashOfRimCompatibilityApi.CanTransferThing(
        relicThing,
        ThingTransferContext.Outbound("gift"),
        out string? reason));
    Assert(reason == IdeologyThingTransferCompatibility.RelicRejectionCode);
}

static void AdoptedNpcIdeologiesFork()
{
    Assert(PlayerIdeoForkPolicy.ShouldFork(
        hasServerGlobalKey: true,
        referencedByNpcFaction: true,
        referencedByPlayerProxy: false));
    Assert(PlayerIdeoForkPolicy.ShouldFork(
        hasServerGlobalKey: false,
        referencedByNpcFaction: true,
        referencedByPlayerProxy: false));
    Assert(!PlayerIdeoForkPolicy.ShouldFork(
        hasServerGlobalKey: false,
        referencedByNpcFaction: false,
        referencedByPlayerProxy: true));
    Assert(!PlayerIdeoForkPolicy.ShouldFork(
        hasServerGlobalKey: false,
        referencedByNpcFaction: false,
        referencedByPlayerProxy: false));
}

static void BiotechOperationTargetsAreCleared()
{
    BeginCycle();
    BiotechThingTransferCompatibility.Apply();
    Thing target = MakeThing("Target");
    var genepack = (Genepack)FormatterServices.GetUninitializedObject(typeof(Genepack));
    genepack.def = MakeDef("Genepack");
    genepack.targetContainer = target;
    var reference = new ModThingReferenceDto { GlobalKey = "gene:1", DefName = "Genepack", StackCount = 1 };

    Assert(ClashOfRimCompatibilityApi.FinalizeThingTransfer(
        reference,
        genepack,
        ThingTransferContext.Inbound("trade", null),
        out _));
    Assert(genepack.targetContainer is null);
}

static void UnnaturalCorpsesAreRejected()
{
    BeginCycle();
    AnomalyThingTransferCompatibility.Apply();
    var corpse = (UnnaturalCorpse)FormatterServices.GetUninitializedObject(typeof(UnnaturalCorpse));
    corpse.def = MakeDef("UnnaturalCorpse");

    Assert(!ClashOfRimCompatibilityApi.CanTransferThing(
        corpse,
        ThingTransferContext.Outbound("gift"),
        out string? reason));
    Assert(reason == AnomalyThingTransferCompatibility.UnnaturalCorpseRejectionCode);
}

static void UnfinishedThingDefsAreHidden()
{
    ThingDef def = MakeDef("UnfinishedThing");
    def.thingClass = typeof(UnfinishedThing);

    Assert(!TradeThingReferenceUtility.IsTradeableItemDef(def));
}

static void PreparedThingsCarryPolicyMetadata()
{
    BeginCycle();
    ClashOfRimCompatibilityApi.RegisterThingTransferRule(
        "test.policy-marker",
        null,
        (Thing thing, ModThingReferenceDto reference, ThingTransferContext context) =>
            reference.Metadata["test.policy-marker"] = "captured",
        null);
    Thing thing = MakeThing("Steel");
    var reference = new ModThingReferenceDto
    {
        GlobalKey = "owner:user-a/colony:colony-a/snapshot:snapshot-a/map:0/thing:steel",
        DefName = "Steel",
        StackCount = 10
    };

    Assert(ThingTransferPipeline.TryPrepareOutbound(
        thing,
        reference,
        ThingReferenceSurfaces.TradeOffer,
        out _));
    Assert(reference.Metadata[ThingTransferPolicy.VersionMetadataKey] == ThingTransferPolicy.CurrentVersion);
    Assert(reference.Metadata[ThingTransferPolicy.DecisionMetadataKey] == ThingTransferPolicy.AcceptedDecision);
}

static void ServerEntryDiagnosticsListModsInLoadOrder()
{
    var manifest = new CompatibilityManifest
    {
        ManifestId = "manifest-123",
        ProtocolVersion = "protocol-2",
        RimWorldVersion = "1.6.9999",
        GameLanguage = "English",
        Mods = new List<ModManifestEntry>
        {
            new()
            {
                LoadOrder = 1,
                PackageId = "author.second",
                Name = "Second\nInjected line",
                Source = "SteamWorkshop",
                WorkshopId = "200",
                Role = ModCompatibilityRole.Required
            },
            new()
            {
                LoadOrder = 0,
                PackageId = "ludeon.rimworld",
                Name = "Core",
                Source = "Official",
                Role = ModCompatibilityRole.Required
            }
        }
    };

    string text = AIRsLight.ClashOfRim.CompatibilityClient.ClientCompatibilityManifestDiagnostics
        .FormatForServerEntry(manifest);

    Assert(text.Contains("mods=2"));
    Assert(text.Contains("manifest=manifest-123"));
    Assert(text.Contains("protocol=protocol-2"));
    Assert(text.Contains("RimWorld=1.6.9999"));
    Assert(text.Contains("language=English"));
    Assert(text.IndexOf("[0] ludeon.rimworld", StringComparison.Ordinal)
        < text.IndexOf("[1] author.second", StringComparison.Ordinal));
    Assert(text.Contains("Second Injected line"));
    Assert(!text.Contains("Second\nInjected line"));
}

static void OnlyAcceptedLanguageWarningsCanBeBypassed()
{
    Assert(AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityLanguageMismatchPolicy.IsLanguageMismatch(
        "GameLanguageMismatch"));
    Assert(!AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityLanguageMismatchPolicy.IsLanguageMismatch(
        "ModListMismatch"));
    Assert(AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityLanguageMismatchPolicy.ResolveServerLanguage(
        "ChineseSimplified",
        "English") == "ChineseSimplified");

    var response = new ModLoginResponseDto
    {
        Result = new ModProtocolResponseDto { Accepted = true },
        CompatibilityIssues = new List<ModCompatibilityIssueDto>
        {
            new() { Severity = "Warning", Code = "GameLanguageMismatch" },
            new() { Severity = "Info", Code = "AllowedPureTranslationMod" }
        }
    };

    Assert(AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityLanguageMismatchPolicy.CanContinue(response));

    response.CompatibilityIssues.Add(new ModCompatibilityIssueDto
    {
        Severity = "Error",
        Code = "ModListMismatch"
    });
    Assert(!AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityLanguageMismatchPolicy.CanContinue(response));

    response.CompatibilityIssues.RemoveAt(response.CompatibilityIssues.Count - 1);
    response.CompatibilityIssues.Add(new ModCompatibilityIssueDto
    {
        Severity = "Info",
        Code = "RimWorldVersionMismatch"
    });
    Assert(!AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityLanguageMismatchPolicy.CanContinue(response));

    response.CompatibilityIssues.RemoveAt(response.CompatibilityIssues.Count - 1);
    response.Result.Accepted = false;
    Assert(!AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityLanguageMismatchPolicy.CanContinue(response));
}

static void VisibleCompatibilityWarningsRequestFullManifest()
{
    Assert(!CompatibilityManifestDeliveryPolicy.ShouldIncludeFullManifest(Array.Empty<CompatibilityIssue>()));
    Assert(!CompatibilityManifestDeliveryPolicy.ShouldIncludeFullManifest(new[]
    {
        new CompatibilityIssue(
            CompatibilityIssueSeverity.Info,
            CompatibilityIssueCode.AllowedPureTranslationMod,
            "allowed")
    }));
    Assert(CompatibilityManifestDeliveryPolicy.ShouldIncludeFullManifest(new[]
    {
        new CompatibilityIssue(
            CompatibilityIssueSeverity.Warning,
            CompatibilityIssueCode.GameLanguageMismatch,
            "language")
    }));
}

static void BeginCycle()
{
    ClashOfRimCompatibilityApi.BeginRegistrationCycle("client-tests");
}

static void SelectionLabelsShowSingleEllipsis()
{
    Assert(AIRsLight.ClashOfRim.ClashOfRimUiUtility.SelectionLabel("Notice") == "Notice ...");
    Assert(AIRsLight.ClashOfRim.ClashOfRimUiUtility.SelectionLabel("Notice ...") == "Notice ...");
    Assert(AIRsLight.ClashOfRim.ClashOfRimUiUtility.SelectionLabel("  ") == "...");
}

static void RemoteProjectionDefersAddictionNeedLookup()
{
    var pawn = (Pawn)FormatterServices.GetUninitializedObject(typeof(Pawn));
    var addiction = (Hediff_Addiction)FormatterServices.GetUninitializedObject(typeof(Hediff_Addiction));
    addiction.pawn = pawn;

    Need_Chemical? result = null;
    Assert(RemoteMapProjectionAddictionLoadPatch.Prefix(addiction, ref result));

    using (RemoteMapProjectionLoadScope.Begin())
    {
        Assert(!RemoteMapProjectionAddictionLoadPatch.Prefix(addiction, ref result));
        Assert(result is null);
    }
}

static void DefenseDutyUsesIndependentReturnNode()
{
    string dutiesPath = FindRepositoryFile(Path.Combine("Defs", "DutyDefs", "ClashOfRim_Duties.xml"));
    XDocument document = XDocument.Load(dutiesPath);
    XElement defendDuty = document
        .Descendants("DutyDef")
        .Single(def => string.Equals(
            def.Element("defName")?.Value,
            "ClashOfRim_DefendDefensePoint",
            StringComparison.Ordinal));
    List<string> jobGivers = defendDuty
        .Descendants("subNodes")
        .Elements("li")
        .Select(node => node.Attribute("Class")?.Value ?? string.Empty)
        .ToList();

    Assert(jobGivers.Contains("AIRsLight.ClashOfRim.Raids.JobGiver_ClashReturnToDefensePoint"));
    Assert(!jobGivers.Contains("JobGiver_GotoTravelDestination"));
}

static string FindRepositoryFile(string relativePath)
{
    foreach (string startPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        DirectoryInfo? directory = new(startPath);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }
    }

    throw new FileNotFoundException("Could not locate repository file.", relativePath);
}

static Thing MakeThing(string defName)
{
    var thing = (Thing)FormatterServices.GetUninitializedObject(typeof(Thing));
    thing.def = MakeDef(defName);
    return thing;
}

static ThingDef MakeDef(string defName)
{
    var def = (ThingDef)FormatterServices.GetUninitializedObject(typeof(ThingDef));
    def.defName = defName;
    def.category = ThingCategory.Item;
    def.stackLimit = 1;
    return def;
}

static ThingWithComps MakeThingWithComp<TComp>(string defName, out TComp comp)
    where TComp : ThingComp
{
    var thing = (ThingWithComps)FormatterServices.GetUninitializedObject(typeof(ThingWithComps));
    thing.def = MakeDef(defName);
    comp = (TComp)FormatterServices.GetUninitializedObject(typeof(TComp));
    comp.parent = thing;
    typeof(ThingWithComps).GetField("comps", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(thing, new List<ThingComp> { comp });
    if (comp is CompStyleable styleable)
    {
        thing.compStyleable = styleable;
    }
    return thing;
}

static void Assert(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException("assertion failed");
    }
}
