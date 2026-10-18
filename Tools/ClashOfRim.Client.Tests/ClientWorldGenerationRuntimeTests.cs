using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.World;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

internal static class ClientWorldGenerationRuntimeTests
{
    private static World product = null!;
    private static WindowStack stack = null!;
    private static readonly List<Window> windows = new();
    private static Action? generating, closing;
    private static object[]? arguments;
    private static int pushed, popped, resets, writes;
    private static bool failWrite;
    private static float pollution = .25f;
    internal static void Run()
    {
        var game = Current.Game; var creating = Current.CreatingWorld; var culture = CultureInfo.CurrentCulture;
        var defs = DefDatabase<FactionDef>.AllDefsListForReading.ToArray();
        var harmony = new Harmony("ClashOfRim.Tests.WorldGenerationRuntime");
        void Patch(System.Reflection.MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientWorldGenerationRuntimeTests), prefix));
        Patch(AccessTools.Method(typeof(AIRsLight.ClashOfRim.ThirdPartyCompatibility.ClashOfRimCompatibilityApi), "ResolveWorldGenerationFloatSetting"), nameof(Pollution));
        Patch(AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GenerateWorld)), nameof(Generate));
        Patch(AccessTools.Method(typeof(GameInitData), nameof(GameInitData.ResetWorldRelatedMapInitData)), nameof(Reset));
        Patch(AccessTools.Method(typeof(Rand), nameof(Rand.EnsureStateStackEmpty)), nameof(Empty));
        Patch(AccessTools.Method(typeof(Rand), nameof(Rand.PushState), new[] { typeof(int) }), nameof(Push));
        Patch(AccessTools.Method(typeof(Rand), nameof(Rand.PopState)), nameof(Pop));
        Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.WindowStack)), nameof(GetStack));
        Patch(AccessTools.PropertyGetter(typeof(WindowStack), nameof(WindowStack.Count)), nameof(Count));
        Patch(AccessTools.PropertyGetter(typeof(WindowStack), "Item"), nameof(Item));
        Patch(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.IsOpen), new[] { typeof(Window) }), nameof(IsOpen));
        Patch(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.Add)), nameof(Add));
        Patch(AccessTools.Method(typeof(Window), nameof(Window.Close)), nameof(Close));
        Patch(AccessTools.Method(typeof(ModSettings), nameof(ModSettings.Write)), nameof(Write));
        try
        {
            stack = New<WindowStack>();
            var faction = new FactionDef { defName = "WorldGenerationTest" }; DefDatabase<FactionDef>.Add(faction);
            foreach (string name in new[] { "en-US", "fr-FR", "tr-TR", "zh-CN" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string coverage in new[] { "0.05", "0.3", "0.5", "1", "1.01", "30", "1e-1" })
                {
                    Current.Game = New<Game>(); Current.Game.InitData = new GameInitData(); Current.Game.World = New<World>();
                    var previous = Current.Game.World; product = New<World>(); arguments = null; pushed = popped = resets = 0;
                    pollution = coverage == "30" ? 42f : .25f;
                    var c = new ModWorldConfigurationDto { SeedString = "种子-I", PlanetCoverage = coverage, OverallRainfall = "999", OverallTemperature = "normal", OverallPopulation = "Normal", LandmarkDensity = "-1" };
                    c.FactionDefNames.Add(faction.defName);
                    var bound = new ClientWorldGenerationRuntime().Bind();
                    using (var result = bound.Generate(ClientWorldGenerationInput.Read(c), () => bound.IsCurrent))
                    {
                        Check(arguments is not null && (float)arguments[0] == float.Parse(coverage, CultureInfo.InvariantCulture) && (string)arguments[1] == "种子-I", "Adapter changed coverage or seed.");
                        Check((OverallRainfall)arguments![2] == OverallRainfall.Normal && (OverallTemperature)arguments[3] == OverallTemperature.Normal
                            && (LandmarkDensity)arguments[5] == LandmarkDensity.Normal && ((List<FactionDef>)arguments[6]).Single() == faction && (float)arguments[7] == pollution, "Undefined enum or faction resolution changed.");
                        result.Publish(); Check(ReferenceEquals(Current.Game.World, product) && bound.IsCurrent, "World publication lost boundary.");
                    }
                    Check(ReferenceEquals(Current.Game.World, previous) && Current.CreatingWorld is null && pushed == 1 && popped == 1 && resets == 1, "Uncommitted world/Rand state not restored.");
                }
            }
            Current.Game = New<Game>(); Current.Game.InitData = new GameInitData(); Current.Game.World = New<World>();
            var owner = Current.Game; var session = new ClientWorldGenerationRuntime().Bind(); var successor = New<Game>(); successor.World = New<World>();
            generating = () => Current.Game = successor; product = New<World>();
            var old = session.Generate(ClientWorldGenerationInput.Read(new() { PlanetCoverage = "0.3" }), () => session.IsCurrent);
            var newerCreating = New<World>(); Current.CreatingWorld = newerCreating; old.Publish(); old.Dispose();
            Check(ReferenceEquals(Current.Game, successor) && !ReferenceEquals(successor.World, product) && ReferenceEquals(Current.CreatingWorld, newerCreating), "Old generation published or cleared successor state.");
            Current.Game = owner; Current.CreatingWorld = null; session = new ClientWorldGenerationRuntime().Bind();
            generating = () => throw new InvalidOperationException("expected generator failure");
            try { session.Generate(ClientWorldGenerationInput.Read(new() { PlanetCoverage = "0.3" }), () => session.IsCurrent); throw new Exception("Generator failure lost."); }
            catch (InvalidOperationException) { }
            Check(Current.CreatingWorld is null && pushed == popped, "Native generation failure leaked creating world/Rand state.");
            generating = null;
            Pages(); Store();
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id); Current.Game = game; Current.CreatingWorld = creating; CultureInfo.CurrentCulture = culture;
            DefDatabase<FactionDef>.Clear(); foreach (var def in defs) DefDatabase<FactionDef>.Add(def);
            generating = closing = null; windows.Clear();
        }
    }
    private static void Pages()
    {
        Current.Game = New<Game>(); var owner = Current.Game;
        var source = New<Page_CreateWorldParams>(); var next = New<Page_SelectStoryteller>(); var blocker = New<Page_SelectScenario>();
        source.next = next; source.nextAct = () => throw new Exception("Next action must not run when next page exists."); next.prev = source;
        windows.Clear(); windows.Add(source); windows.Add(blocker);
        var page = new ClientWorldGenerationPage(source); page.Close();
        Check(!windows.Contains(source) && next.prev is null, "Source page did not detach continuation.");
        page.Reopen(); Check(windows.Count(w => ReferenceEquals(w, source)) == 1, "Failed page could not reopen."); page.Close();
        var newer = New<Page_SelectScenario>(); windows.Add(newer);
        page.Continue(() => ReferenceEquals(Current.Game, owner));
        Check(!windows.Contains(blocker) && windows.Contains(newer) && windows.Contains(next), "Completion closed later setup pages or missed next page.");
        windows.Clear(); windows.Add(source); windows.Add(blocker); page = new(source); page.Close();
        closing = () => Current.Game = New<Game>();
        page.Continue(() => ReferenceEquals(Current.Game, owner)); closing = null;
        Check(!windows.Contains(next), "Close callback switched Game but old next page opened.");
        Current.Game = owner; windows.Clear(); source.next = null; int calls = 0; source.nextAct = () => calls++;
        windows.Add(source); page = new(source); page.Close(); page.Continue(() => true); Check(calls == 1, "Next action lost.");
    }
    private static void Store()
    {
        writes = 0; failWrite = false; var settings = new ClashOfRimSettings { CurrentWorldConfigurationId = "previous" }; var store = new ClientWorldEntryStore(settings);
        store.Remember("previous"); Check(writes == 0, "Identical world rewrote settings.");
        failWrite = true;
        try { store.Remember("new"); throw new Exception("Store failure lost."); } catch (InvalidOperationException) { }
        Check(settings.CurrentWorldConfigurationId == "previous", "Failed world identity save polluted memory.");
        failWrite = false; store.Remember("new"); Check(writes == 2 && settings.CurrentWorldConfigurationId == "new", "World identity did not persist.");
    }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static bool Generate(object[] __args, ref World __result) { arguments = __args; Current.CreatingWorld = product; generating?.Invoke(); __result = product; return false; }
    private static bool Pollution(ref float __result) { __result = pollution; return false; }
    private static bool Reset() { resets++; return false; }
    private static bool Empty() => false;
    private static bool Push() { pushed++; return false; }
    private static bool Pop() { popped++; return false; }
    private static bool GetStack(ref WindowStack __result) { __result = stack; return false; }
    private static bool Count(ref int __result) { __result = windows.Count; return false; }
    private static bool Item(int __0, ref Window __result) { __result = windows[__0]; return false; }
    private static bool IsOpen(Window __0, ref bool __result) { __result = windows.Contains(__0); return false; }
    private static bool Add(Window __0) { windows.Add(__0); return false; }
    private static bool Close(Window __instance) { windows.Remove(__instance); closing?.Invoke(); return false; }
    private static bool Write() { writes++; if (failWrite) throw new InvalidOperationException("expected settings failure"); return false; }
}
