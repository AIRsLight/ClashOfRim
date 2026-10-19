using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.DlcCompatibility;
using AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Genes;
using AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Pawns;
using AIRsLight.ClashOfRim.Modules.Compatibility.Biotech;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static class PawnPreviewDlcTests
{
    private static readonly FactionManager factions = new();
    private static readonly IdeoManager ideos = new();
    private static UniqueIDsManager ids = new();
    private static readonly List<string> paths = new();
    private static CustomXenotype custom = null!;
    private static int nativeIdeoPosts, gameplaySetters;
    private static bool loadXenotype = true;
    internal static void Run()
    {
        var previousGame = Current.Game; var culture = CultureInfo.CurrentCulture;
        var registration = ClashOfRimCompatibilityApi.CreateRegistrationToken("pawn-preview-dlc-tests");
        var harmony = new Harmony("ClashOfRim.Tests.PawnPreviewDlc");
        void Patch(System.Reflection.MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(PawnPreviewDlcTests), prefix));
        Patch(AccessTools.Method(typeof(Pawn_IdeoTracker), nameof(Pawn_IdeoTracker.ExposeData)), nameof(ObserveIdeo));
        Patch(AccessTools.Method(typeof(Pawn_IdeoTracker), nameof(Pawn_IdeoTracker.SetIdeo)), nameof(Gameplay));
        Patch(AccessTools.Method(typeof(Pawn_RoyaltyTracker), nameof(Pawn_RoyaltyTracker.SetTitle)), nameof(Gameplay));
        Patch(AccessTools.Method(typeof(Pawn_RoyaltyTracker), nameof(Pawn_RoyaltyTracker.SetFavor)), nameof(Gameplay));
        Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.FactionManager)), nameof(Factions));
        Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.IdeoManager)), nameof(Ideos));
        Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.UniqueIDsManager)), nameof(Ids));
        Patch(AccessTools.Method(typeof(ClashOfRimCompatibilityApi), nameof(ClashOfRimCompatibilityApi.LocalizeRestoredPawnWithCompatibility)), nameof(Gameplay));
        Patch(AccessTools.PropertyGetter(typeof(ModsConfig), nameof(ModsConfig.BiotechActive)), nameof(Active));
        using var nativeScribe = new NativeScribeTestScope();
        Patch(AccessTools.PropertyGetter(typeof(GenTypes), "AllActiveAssemblies"), nameof(Assemblies));
        Patch(AccessTools.Method(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.TryLoadXenotype)), nameof(LoadXenotype));
        Patch(AccessTools.Method(typeof(ContentFinder<Texture2D>), nameof(ContentFinder<Texture2D>.Get)), nameof(Texture));
        Patch(AccessTools.Method(typeof(ClashOfRimText), nameof(ClashOfRimText.Key), new[] { typeof(string), typeof(NamedArgument[]) }), nameof(Text));
        Patch(AccessTools.Method(typeof(ClashOfRimText), nameof(ClashOfRimText.Key), new[] { typeof(string) }), nameof(Text));
        Patch(AccessTools.Method(typeof(Log), nameof(Log.Warning), new[] { typeof(string) }), nameof(Quiet));
        var title = new RoyalTitleDef { defName = "PreviewTitleFixture" };
        var previousTitles = DefDatabase<RoyalTitleDef>.AllDefsListForReading.ToArray();
        var empire = new Faction { def = new FactionDef { defName = "PreviewEmpireFixture" } };
        var ideo = new Ideo { name = "文化 İ" };
        const string ideoKey = "owner:preview-fixture/ideo:42";
        try
        {
            // Use the capability registry: a patched forwarding getter can be
            // inlined after another test unpatches its caller in Release builds.
            Check(ClashOfRimCompatibilityApi.UseRegistrationToken(registration, () =>
                ClashOfRimCompatibilityApi.RegisterCompatibilityPackage("pawn-preview-dlc-tests", static () => true,
                    new[] { IdeologyCompatibilityKeys.PawnReference, RoyaltyCompatibilityKeys.RemoteStateGuards })),
                "Preview capability registration failed.");
            Check(IdeologyPawnReferenceCompatibility.HasPawnReference,
                "Preview ideology capability is unavailable.");
            DefDatabase<RoyalTitleDef>.Add(title); factions.AllFactionsListForReading.Add(empire);
            ideos.IdeosListForReading.Add(ideo); RemoteIdeoCatalog.Register(ideo, ideoKey);
            Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
            Current.Game.customXenotypeDatabase = new CustomXenotypeDatabase();
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); gameplaySetters = 0;
                NativeIdeoDispatch();
                var pawn = new Pawn(); pawn.ideo = new PawnPreviewIdeoTracker(pawn);
                Set(pawn.ideo, "certaintyInt", 0.75f); pawn.ideo.joinTick = 42;
                var package = new ModPawnExchangePackageDto { Reference = new() { Metadata = new() { [IdeologyPawnReferenceCompatibility.PawnMetadataIdeoGlobalId] = ideoKey } } };
                Check(IdeologyPawnReferenceCompatibility.RestorePreview(pawn, package, out var ideoMessage),
                    "Ideology preview restoration rejected: " + ideoMessage);
                Check(ReferenceEquals(pawn.ideo.Ideo, ideo), "Ideology preview did not bind the catalog object.");
                Check((float)Get(pawn.ideo, "certaintyInt")! == 0.75f && pawn.ideo.joinTick == 42, "Ideology preview converted or reset the pawn.");
                Check(RemoteIdeoCatalog.TryGetIdeo(ideoKey, out var retained) && ReferenceEquals(retained, ideo), "Preview replaced the world ideology.");
                package.Reference.Metadata[IdeologyPawnReferenceCompatibility.PawnMetadataIdeoGlobalId] = "unknown";
                Check(!IdeologyPawnReferenceCompatibility.RestorePreview(pawn, package, out _), "Unknown ideology was silently replaced with a local belief.");

                package.Reference.Metadata[RoyaltyPawnReferenceCompatibility.PawnMetadataRoyaltyState] = "PreviewEmpireFixture|PreviewTitleFixture|37";
                Check(RoyaltyPawnReferenceCompatibility.RestorePreview(pawn, package, out _)
                    && pawn.royalty.AllTitlesForReading.Single().def == title && pawn.royalty.AllTitlesForReading[0].faction == empire
                    && pawn.royalty.AllTitlesForReading[0].pawn == pawn && ((Dictionary<Faction, int>)Get(pawn.royalty, "favor")!)[empire] == 37,
                    "Royalty preview lost the resolved title, faction or favor.");
                package.Reference.Metadata[RoyaltyPawnReferenceCompatibility.PawnMetadataRoyaltyState] = "PreviewEmpireFixture|PreviewTitleFixture|99;PreviewEmpireFixture||-1;MissingEmpire|PreviewTitleFixture|50";
                Check(RoyaltyPawnReferenceCompatibility.RestorePreview(pawn, package, out _) && pawn.royalty.AllTitlesForReading.Count == 0
                    && ((Dictionary<Faction, int>)Get(pawn.royalty, "favor")!)[empire] == 0, "Royalty projection changed shared resolution/normalization.");
                Xenotype(pawn);
                ids = new UniqueIDsManager();
                var race = new ThingDef { defName = "PreviewIdentity", category = ThingCategory.Pawn, race = new RaceProperties() };
                var first = new Pawn { def = race, thingIDNumber = 123 };
                var second = new Pawn { def = race, thingIDNumber = 123 };
                PawnExchangeLoadIdUtility.LocalizePreviewPawn(first); PawnExchangeLoadIdUtility.LocalizePreviewPawn(second);
                Check(first.thingIDNumber != second.thingIDNumber && first.thingIDNumber != 123 && second.thingIDNumber != 123,
                    "Detached previews retained colliding source Thing IDs.");
                Check(gameplaySetters == 0, "Display restoration invoked a gameplay conversion/title setter.");
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); throw; }
        finally
        {
            ClashOfRimCompatibilityApi.RevokeRegistrationToken(registration);
            Scribe.ForceStop(); Current.Game = previousGame; CultureInfo.CurrentCulture = culture;
            RemoteIdeoCatalog.Unregister(ideo); ideos.IdeosListForReading.Remove(ideo); factions.AllFactionsListForReading.Remove(empire);
            DefDatabase<RoyalTitleDef>.Clear(); foreach (var previous in previousTitles) DefDatabase<RoyalTitleDef>.Add(previous);
            harmony.UnpatchAll(harmony.Id); paths.Clear(); custom = null!;
        }
    }

    private static void NativeIdeoDispatch()
    {
        foreach (bool preview in new[] { false, true })
        {
            nativeIdeoPosts = 0; string path = Path.GetTempFileName();
            try
            {
                string type = (preview ? typeof(PawnPreviewIdeoTracker) : typeof(Pawn_IdeoTracker)).FullName!;
                File.WriteAllText(path, "<root><tracker Class='" + type + "'><ideo>null</ideo><certainty>0.75</certainty><joinTick>42</joinTick><previousIdeos/><babyIdeoExposure/></tracker></root>");
                Scribe.loader.InitLoading(path); Pawn_IdeoTracker? tracker = null;
                Scribe_Deep.Look(ref tracker, "tracker", new Pawn()); Scribe.loader.FinalizeLoading();
                Check(tracker is not null && (float)Get(tracker, "certaintyInt")! == 0.75f && tracker.joinTick == 42
                    && nativeIdeoPosts == (preview ? 0 : 1) && (tracker is PawnPreviewIdeoTracker) == preview,
                    $"Native Scribe did not preserve fields or dispatch the preview IExposable implementation: preview={preview}, type={tracker?.GetType().FullName}, certainty={(tracker is null ? null : Get(tracker, "certaintyInt"))}, join={tracker?.joinTick}, posts={nativeIdeoPosts}.");
            }
            finally { Scribe.ForceStop(); File.Delete(path); }
        }
    }

    private static void Xenotype(Pawn pawn)
    {
        paths.Clear(); pawn.genes = new Pawn_GeneTracker(pawn);
        var gene = new GeneDef { defName = "PreviewGene", passOnDirectly = true };
        pawn.genes.Endogenes.Add(new Gene { def = gene, pawn = pawn });
        custom = new CustomXenotype { name = "异种 İ", inheritable = true, genes = new() { gene } };
        const string xml = "<xenotype/>";
        var payload = new BiotechSavedXenotypePackageDto { Name = custom.name, Xml = xml, XmlSha256 = ClientSnapshotPayloadCodec.Hash(System.Text.Encoding.UTF8.GetBytes(xml)) };
        var package = new ModPawnExchangePackageDto(); package.Extensions.Add(BiotechPawnExchangePayload.Create(null, payload)!);
        var component = new BiotechPawnExchangeCompatibility(static () => true, _ => { }); loadXenotype = true;
        Check(component.RestorePreview(pawn, package, out _) && ReferenceEquals(Get(pawn.genes, "cachedCustomXenotype"), custom)
            && Current.Game.customXenotypeDatabase.customXenotypes.Count == 0 && paths.Count == 1 && paths.All(p => !File.Exists(p)),
            "Xenotype preview registered a world definition, lost its display cache or leaked a file.");
        Scribe.mode = LoadSaveMode.Saving;
        try { Check(!component.RestorePreview(pawn, package, out _) && paths.Count == 1 && Scribe.mode == LoadSaveMode.Saving,
            "Preview xenotype load took ownership of another Scribe operation."); }
        finally { Scribe.mode = LoadSaveMode.Inactive; }
        pawn.genes.Endogenes.Clear();
        Check(component.RestorePreview(pawn, package, out _) && Get(pawn.genes, "cachedCustomXenotype") is null, "Mismatched genes were labeled as the supplied custom xenotype.");
        loadXenotype = false;
        Check(!component.RestorePreview(pawn, package, out _) && paths.All(p => !File.Exists(p)) && Current.Game.customXenotypeDatabase.customXenotypes.Count == 0,
            "Failed preview import registered a definition or leaked its file.");
        payload.XmlSha256 = "tampered"; package.Extensions.Clear(); package.Extensions.Add(BiotechPawnExchangePayload.Create(null, payload)!);
        int count = paths.Count;
        Check(!component.RestorePreview(pawn, package, out _) && paths.Count == count, "Tampered preview xenotype reached native loading.");
    }
    private static void Set(object target, string name, object value) => AccessTools.Field(target.GetType(), name).SetValue(target, value);
    private static object? Get(object target, string name) => AccessTools.Field(target.GetType(), name).GetValue(target);
    private static bool ObserveIdeo() { if (Scribe.mode != LoadSaveMode.PostLoadInit) return true; nativeIdeoPosts++; return false; }
    private static bool Gameplay() { gameplaySetters++; throw new InvalidOperationException("Preview entered gameplay setter."); }
    private static bool Factions(ref FactionManager __result) { __result = factions; return false; }
    private static bool Ideos(ref IdeoManager __result) { __result = ideos; return false; }
    private static bool Ids(ref UniqueIDsManager __result) { __result = ids; return false; }
    private static bool Active(ref bool __result) { __result = true; return false; }
    private static bool Assemblies(ref IEnumerable<System.Reflection.Assembly> __result)
    { __result = new[] { typeof(Pawn).Assembly, typeof(PawnPreviewIdeoTracker).Assembly }; return false; }
    private static bool Texture(ref Texture2D? __result) { __result = null; return false; }
    private static bool Text(string key, ref string __result) { __result = key; return false; }
    private static bool Quiet() => false;
    private static bool LoadXenotype(string __0, out CustomXenotype __1, ref bool __result)
    { paths.Add(__0); Check(File.Exists(__0), "Missing xenotype file."); __1 = custom; __result = loadXenotype; return false; }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
