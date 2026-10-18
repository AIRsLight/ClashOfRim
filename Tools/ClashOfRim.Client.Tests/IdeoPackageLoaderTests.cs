using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.DlcCompatibility;
using AIRsLight.ClashOfRim.DlcCompatibility.Ideology.Ideos;
using AIRsLight.ClashOfRim.Modules.Compatibility.Ideology;
using HarmonyLib;
using RimWorld;
using Verse;

internal static class IdeoPackageLoaderTests
{
    private const string Xml = "<savedideo><name>文化 İ Français</name></savedideo>";
    private static readonly List<string> paths = new();
    private static readonly List<string> trace = new();
    private static readonly List<Ideo> registered = new();
    private static readonly FactionManager factions = new();
    private static IdeoManager manager = null!;
    private static UniqueIDsManager ids = null!;
    private static Ideo loaded = null!;
    private static Game? expectedGame;
    private static int expectedThread;
    private static bool loadSucceeds;
    private static Exception? loadFailure;
    private static IReadOnlyList<ModWorldIdeoSummaryDto> worldDtos = Array.Empty<ModWorldIdeoSummaryDto>();
    private static Ideo? copiedSource, copiedTarget;

    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture;
        var game = Current.Game;
        var playerDef = FactionDefOf.PlayerColony;
        var harmony = new Harmony("ClashOfRim.Tests.IdeoPackageLoader");
        Check(Scribe.mode == LoadSaveMode.Inactive, "Fixture requires an inactive Scribe.");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.TryLoadIdeo)), prefix: Method(nameof(Load)));
            harmony.Patch(AccessTools.Method(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveIdeo)), prefix: Method(nameof(Save)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.IdeoManager)), prefix: Method(nameof(Ideos)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.FactionManager)), prefix: Method(nameof(Factions)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.UniqueIDsManager)), prefix: Method(nameof(Ids)));
            harmony.Patch(AccessTools.Method(typeof(IdeoGenerator), nameof(IdeoGenerator.InitLoadedIdeo)), prefix: Method(nameof(Initialize)));
            harmony.Patch(AccessTools.Method(typeof(Ideo), nameof(Ideo.SortStyleCategories)), prefix: Method(nameof(Sort)));
            harmony.Patch(AccessTools.Method(typeof(Ideo), nameof(Ideo.RecachePrecepts)), prefix: Method(nameof(Recache)));
            harmony.Patch(AccessTools.Method(typeof(Ideo), nameof(Ideo.CopyTo)), prefix: Method(nameof(Copy)));
            harmony.Patch(AccessTools.Method(typeof(IdeologyPawnReferenceCompatibility), nameof(IdeologyPawnReferenceCompatibility.ReadCurrentWorldIdeos)), prefix: Method(nameof(WorldDtos)));
            FactionDefOf.PlayerColony = new FactionDef { basicMemberKind = new PawnKindDef { label = "colonist" } };
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Current.Game = null; // World setup can load packages before a Game exists.
                ResetLoad();
                LoaderContract();
                CatalogAndNormalization();
            }
        }
        finally
        {
            foreach (Ideo ideo in registered) RemoteIdeoCatalog.Unregister(ideo);
            registered.Clear();
            harmony.UnpatchAll(harmony.Id);
            Scribe.ForceStop(); Current.Game = game; CultureInfo.CurrentCulture = culture;
            FactionDefOf.PlayerColony = playerDef;
            paths.Clear(); trace.Clear(); worldDtos = Array.Empty<ModWorldIdeoSummaryDto>();
            loaded = null!; copiedSource = copiedTarget = null; expectedGame = null;
        }
    }

    private static void LoaderContract()
    {
        var warnings = new List<string>();
        var loader = new IdeoPackageLoader(warnings.Add);
        Check(IdeoPackageHash.Compute("abc") == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            "Package digest no longer matches the SHA-256 wire format.");
        Check(loader.TryLoad(null, null, "empty") is null && loader.TryLoad(" \t", null, "empty") is null && paths.Count == 0,
            "Empty packages entered native loading.");
        string hash = IdeoPackageHash.Compute(Xml);
        Check(loader.TryLoad(Xml, hash + "0", "tampered") is null && paths.Count == 0 && warnings.Count == 1,
            "Hash mismatch entered native loading or lost its diagnostic.");
        foreach (LoadSaveMode mode in new[] { LoadSaveMode.Saving, LoadSaveMode.LoadingVars, LoadSaveMode.ResolvingCrossRefs, LoadSaveMode.PostLoadInit })
        {
            Scribe.mode = mode;
            try
            {
                Check(loader.TryLoad(Xml, hash, "busy") is null && Scribe.mode == mode && paths.Count == 0,
                    "Loading interrupted an existing Scribe operation.");
            }
            finally { Scribe.mode = LoadSaveMode.Inactive; }
        }

        Check(ReferenceEquals(loader.TryLoad(Xml, hash.ToUpperInvariant(), "valid"), loaded)
            && loaded.initialPlayerIdeo && trace.SequenceEqual(new[] { "load" }),
            "Raw loading initialized or materialized the ideology, or rejected an uppercase digest.");
        CheckClean();
        Check(ReferenceEquals(loader.TryLoad(Xml, null, "local"), loaded), "Local load without a digest changed.");
        CheckClean();
        Check(IdeologyPawnReferenceCompatibility.TryComputeIdeoPackageSha256(loaded, out string? exportedHash)
            && exportedHash == hash, "Export and import use different package digest contracts.");
        CheckClean();

        loadSucceeds = false;
        Check(loader.TryLoad(Xml, hash, "native-rejection") is null, "Native load rejection was ignored.");
        CheckClean();
        loadSucceeds = true;
        foreach (Exception failure in new Exception[] { new IOException("io"), new UnauthorizedAccessException("access"), new InvalidOperationException("load"), new ArgumentException("xml") })
        {
            loadFailure = failure;
            int count = warnings.Count;
            Check(loader.TryLoad(Xml, hash, "native-failure") is null && warnings.Count == count + 1,
                "Expected native failure lost its result or diagnostic.");
            CheckClean();
        }
        loadFailure = new NotSupportedException("unexpected");
        bool propagated = false;
        try { loader.TryLoad(Xml, hash, "unexpected"); }
        catch (NotSupportedException) { propagated = true; }
        Check(propagated, "Unexpected engine failures were hidden.");
        CheckClean(); loadFailure = null;
    }

    private static void CatalogAndNormalization()
    {
        Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        manager = new IdeoManager(); ids = new UniqueIDsManager(); ResetLoad();
        var dto = new ModWorldIdeoSummaryDto
        {
            GlobalKey = "owner:loader-test/ideo:1", OwnerUserId = "loader-test", Name = "Remote 文化 İ",
            SavedIdeoPackageXml = Xml, SavedIdeoPackageSha256 = IdeoPackageHash.Compute(Xml)
        };
        Check(RemoteIdeoMaterializer.TryGetOrCreate(dto, out var first) && ReferenceEquals(first, loaded)
            && manager.IdeosListForReading.Contains(first!) && !first!.initialPlayerIdeo && first.solid && !first.hidden
            && first.name == dto.Name && trace.SequenceEqual(new[] { "load", "init", "sort", "recache" }),
            "Remote materialization lost initialization, shadow fields, or catalog registration order.");
        registered.Add(first!); CheckClean();
        trace.Clear();
        Check(RemoteIdeoMaterializer.TryGetOrCreate(dto, out var again) && ReferenceEquals(first, again) && trace.Count == 0,
            "A current catalog entry unnecessarily reloaded its package.");

        Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        manager = new IdeoManager(); ResetLoad();
        Check(RemoteIdeoMaterializer.TryGetOrCreate(dto, out var second) && ReferenceEquals(second, loaded)
            && !ReferenceEquals(first, second) && manager.IdeosListForReading.Contains(second!),
            "A previous game's catalog instance escaped into the current manager.");
        registered.Add(second!); CheckClean();

        manager = new IdeoManager(); ResetLoad();
        var local = new Ideo { id = 73, name = "Local", initialPlayerIdeo = true, hidden = true };
        manager.IdeosListForReading.Add(local);
        worldDtos = new[] { new ModWorldIdeoSummaryDto
        {
            GlobalKey = "local:73", LocalId = "73", Name = "Local", SavedIdeoPackageXml = Xml,
            SavedIdeoPackageSha256 = "unused-for-local-roundtrip"
        } };
        RemoteIdeoMaterializer.NormalizeGeneratedIdeologies();
        Check(ReferenceEquals(copiedSource, loaded) && ReferenceEquals(copiedTarget, local)
            && local.initialPlayerIdeo && local.hidden && local.name == "Local"
            && trace.SequenceEqual(new[] { "load", "copy" }),
            "Local normalization used remote initialization/shadow fields or copied to a different instance.");
        CheckClean();
    }

    private static void ResetLoad()
    {
        paths.Clear(); trace.Clear(); loadFailure = null; loadSucceeds = true;
        loaded = new Ideo { id = 100, name = "Loaded", initialPlayerIdeo = true, hidden = true };
        expectedGame = Current.Game; expectedThread = Thread.CurrentThread.ManagedThreadId;
        copiedSource = copiedTarget = null;
    }

    private static void CheckClean() => Check(paths.All(path => !File.Exists(path)) && Scribe.mode == LoadSaveMode.Inactive,
        "Native load left a temporary file or owned Scribe operation active.");

    private static bool Load(string __0, ref Ideo __1, ref bool __result)
    {
        Check(File.ReadAllText(__0) == Xml && Path.GetExtension(__0) == ".ideo", "Native loader received a different package.");
        Check(ReferenceEquals(Current.Game, expectedGame) && Thread.CurrentThread.ManagedThreadId == expectedThread,
            "Loading switched or captured the wrong Game/thread.");
        Check(Scribe.mode == LoadSaveMode.Inactive, "Native load started while Scribe was busy.");
        paths.Add(__0); trace.Add("load"); Scribe.mode = LoadSaveMode.LoadingVars;
        if (loadFailure is not null) throw loadFailure;
        __1 = loaded; __result = loadSucceeds; return false;
    }

    private static bool Save(Ideo __0, string __1) { paths.Add(__1); File.WriteAllText(__1, Xml); return false; }
    private static bool Ideos(ref IdeoManager __result) { __result = manager; return false; }
    private static bool Factions(ref FactionManager __result) { __result = factions; return false; }
    private static bool Ids(ref UniqueIDsManager __result) { __result = ids; return false; }
    private static bool Initialize(Ideo __0, ref Ideo __result) { trace.Add("init"); __result = __0; return false; }
    private static bool Sort() { trace.Add("sort"); return false; }
    private static bool Recache() { trace.Add("recache"); return false; }
    private static bool Copy(Ideo __instance, Ideo __0) { trace.Add("copy"); copiedSource = __instance; copiedTarget = __0; return false; }
    private static bool WorldDtos(ref IReadOnlyList<ModWorldIdeoSummaryDto> __result) { __result = worldDtos; return false; }
    private static HarmonyMethod Method(string name) => new(typeof(IdeoPackageLoaderTests), name);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
