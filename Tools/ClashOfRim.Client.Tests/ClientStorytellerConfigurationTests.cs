using System;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;
using HarmonyLib;
using RimWorld;
using Verse;

internal static class ClientStorytellerConfigurationTests
{
    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture;
        int count = 0;
        try
        {
            foreach (string name in new[] { "en-US", "fr-FR", "tr-TR", "zh-CN" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Defaults(); count++;
                foreach (string change in new[] { "user", "server", "colony", "auth", "login", "game", "generation", "snapshot" })
                    foreach (string phase in new[] { "capture", "prepare", "apply" }) { Ownership(change, phase); count++; }
                foreach (string change in new[] { "closed", "replaced", "reset" }) { PendingReplacement(change); count++; }
            }
            Runtime(); count++;
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
        Console.WriteLine($"PASS: storyteller configuration ({count} scenarios)");
    }
    private static void Defaults()
    {
        var f = new Fixture();
        Check(!f.Module.ContinueSetup(f.Page) && f.Prepared == 0, "No configuration advanced setup.");
        f.Module.LockCommitment(); Check(f.Locked == 0, "Ordinary setup forced commitment.");
        f.Prepare(); f.Module.LockCommitment(); Check(f.Locked == 1, "Multiplayer setup did not lock commitment.");
        f.Playing = false;
        Check(!f.Module.ApplyRuntime(Config()) && f.Prepared == 0, "Non-playing runtime applied configuration.");
        f.Playing = true;
        foreach (var invalid in new[] { new ModWorldConfigurationDto(), new() { StorytellerDefName = "story" }, new() { DifficultyDefName = "difficulty" } })
            Check(!f.Module.ApplyRuntime(invalid) && f.Prepared == 0, "Incomplete definitions reached runtime.");
        var input = Config(); f.OnPrepare = () => input.DifficultyValuesXml = "modified";
        Check(f.Module.ApplyRuntime(input) && f.Last == Baseline(), "Apply borrowed a mutable configuration after capture.");
        f.OnPrepare = null; f.Resolvable = false;
        Check(!f.Module.ContinueSetup(f.Page) && f.Page.Continued == 0, "Failed staging advanced setup.");
        f.Resolvable = true;
        Check(f.Module.ContinueSetup(f.Page) && f.Page.Continued == 1 && f.Last == Baseline(), "Setup did not use the shared baseline.");
        int applied = f.Applied;
        Check(!f.Module.ContinueSetup(f.Page) && f.Applied == applied && f.Page.Continued == 1, "Closed page applied twice.");
        Check(f.Module.Capture() == Baseline() && f.Module.Capture().Signature == "story\u001fdifficulty\u001f<saveable><threatScale>1.37</threatScale></saveable>", "Capture/signature changed baseline.");
    }
    private static void Ownership(string change, string phase)
    {
        var f = new Fixture(); f.Prepare();
        Action mutate = () => f.Change(change);
        if (phase == "capture") f.OnCapture = mutate;
        if (phase == "prepare") f.OnPrepare = mutate;
        if (phase == "apply") f.OnApply = mutate;
        bool preserved = change == "snapshot";
        if (phase == "capture")
            Check(f.Module.Capture().HasDefinitions == preserved, $"Stale capture: {change}.");
        else
        {
            Check(f.Module.ContinueSetup(f.Page) == preserved && f.Page.Continued == (preserved ? 1 : 0), $"Stale setup continuation: {change}/{phase}.");
            Check(f.Applied == (preserved || phase == "apply" ? 1 : 0), $"Stale baseline applied: {change}/{phase}.");
        }
    }
    private static void PendingReplacement(string change)
    {
        var f = new Fixture(); f.Prepare();
        f.OnPrepare = () =>
        {
            if (change == "closed") f.Page.Current = false;
            else if (change == "reset") f.Setup.Reset();
            else f.Prepare("replacement");
        };
        Check(!f.Module.ContinueSetup(f.Page) && f.Applied == 0 && f.Page.Continued == 0, $"Replaced setup applied old baseline: {change}.");
    }
    private static void Runtime()
    {
        var oldGame = Current.Game; var oldState = Current.ProgramState;
        var oldStories = DefDatabase<StorytellerDef>.AllDefsListForReading.ToArray();
        var oldDifficulties = DefDatabase<DifficultyDef>.AllDefsListForReading.ToArray();
        var culture = CultureInfo.CurrentCulture; var ui = CultureInfo.CurrentUICulture;
        var harmony = new Harmony("ClashOfRim.Tests.StorytellerConfiguration");
        harmony.CreateClassProcessor(typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.CoreCompatibility.InvariantGameDataCulturePatch", true)!).Patch();
        harmony.Patch(AccessTools.PropertyGetter(typeof(Prefs), "LogVerbose"), prefix: new HarmonyMethod(typeof(ClientStorytellerConfigurationTests), nameof(QuietPrefs)));
        try
        {
            var story = new StorytellerDef { defName = "ClashStorytellerTest" };
            var difficulty = new DifficultyDef { defName = "ClashDifficultyTest" };
            DefDatabase<StorytellerDef>.Add(story); DefDatabase<DifficultyDef>.Add(difficulty);
            var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
            Current.Game = game; Current.ProgramState = ProgramState.Playing;
            var runtime = new ClientStorytellerRuntime();
            foreach (string name in new[] { "en-US", "fr-FR", "de-DE", "tr-TR", "zh-CN" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                game.InitData = new GameInitData();
                game.storyteller = new Storyteller(story, difficulty, new Difficulty { threatScale = 1.37f });
                var session = runtime.Bind(); var captured = session.Capture();
                Check(captured.Storyteller == story.defName && captured.Difficulty == difficulty.defName && captured.ValuesXml!.Contains("1.37"), "Real capture lost definitions/custom difficulty.");
                var staged = session.Prepare(captured);
                Check(staged is not null, "Valid Scribe difficulty rejected."); staged!.Apply();
                Check(game.storyteller.difficulty.threatScale == 1.37f
                    && game.InitData.permadeathChosen && !game.InitData.permadeath, "Round trip changed custom difficulty or commitment.");
                Check(CultureInfo.CurrentCulture.Name == name && CultureInfo.CurrentUICulture == ui, "Difficulty serialization leaked culture.");
                foreach (string xml in new[] { "<broken", "<other/>", "<saveable IsNull=\"True\" />" })
                {
                    var previous = game.storyteller;
                    Check(session.Prepare(captured with { ValuesXml = xml }) is null && ReferenceEquals(previous, game.storyteller) && Scribe.mode == LoadSaveMode.Inactive,
                        "Malformed/absent saveable silently reverted to preset.");
                }
                Scribe.mode = LoadSaveMode.Saving;
                try
                {
                    Check(session.Prepare(captured) is null && Scribe.mode == LoadSaveMode.Saving, "Busy Scribe overwritten/stopped.");
                    var busy = session.Capture();
                    Check(busy.ValuesXml is null && busy.CaptureFailure is not null && Scribe.mode == LoadSaveMode.Saving, "Busy capture lost failure or disturbed Scribe.");
                }
                finally { Scribe.mode = LoadSaveMode.Inactive; }
                var preset = session.Prepare(captured with { ValuesXml = null }); Check(preset is not null, "Legacy preset baseline rejected.");
                game.InitData = null; preset!.Apply();
                Check(game.storyteller.difficulty.threatScale == new Difficulty(difficulty).threatScale, "Missing XML did not use preset/no-init runtime failed.");
                Check(session.Prepare(captured with { Difficulty = "missing" }) is null, "Unresolved Def accepted.");
                var otherGame = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game)); Current.Game = otherGame;
                staged.Apply(); Check(otherGame.storyteller is null, "Staged change crossed Game ownership."); Current.Game = game;
            }
        }
        finally
        {
            Scribe.ForceStop(); harmony.UnpatchAll(harmony.Id); Current.Game = oldGame; Current.ProgramState = oldState;
            CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = ui;
            DefDatabase<StorytellerDef>.Clear(); foreach (var def in oldStories) DefDatabase<StorytellerDef>.Add(def);
            DefDatabase<DifficultyDef>.Clear(); foreach (var def in oldDifficulties) DefDatabase<DifficultyDef>.Add(def);
        }
    }
    private static bool QuietPrefs(ref bool __result) { __result = false; return false; }
    private static ModWorldConfigurationDto Config(string story = "story") => new()
    { StorytellerDefName = story, DifficultyDefName = "difficulty", DifficultyValuesXml = "<saveable><threatScale>1.37</threatScale></saveable>" };
    private static ClientStorytellerBaseline Baseline() => ClientStorytellerBaseline.From(Config());
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    internal sealed class Page : IClientStorytellerPage
    {
        internal bool Current = true; internal int Continued;
        public bool IsCurrent => Current;
        public void Continue(Func<bool> current) { if (current()) { Current = false; Continued++; } }
    }
    internal sealed class Fixture : IClientStorytellerRuntime
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientWorldSetupState Setup;
        internal readonly ClientStorytellerConfiguration Module;
        internal readonly Page Page = new();
        internal bool Playing = true, Resolvable = true;
        internal int Prepared, Applied, Locked;
        internal ClientStorytellerBaseline? Last;
        internal Action? OnCapture, OnPrepare, OnApply;
        internal Fixture()
        { ClientSessionWork.Reset(); Session = new(Ports, Ports, new(Ports)); Setup = new(Session); Module = new(Session, Setup, this); }
        internal void Prepare(string story = "story") => Setup.Prepare(new() { Result = new() { Accepted = true }, WorldConfigured = true, WorldConfiguration = Config(story) });
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break;
                case "server": Ports.Server = "http://other"; break;
                case "colony": Ports.Colony = "other"; break;
                case "auth": Ports.Auth = "other"; break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth, SessionId = "same-credentials" }); break;
                case "game": Ports.GameOwner = new(); break;
                case "generation": ClientSessionWork.Reset(); break;
                case "snapshot": Ports.Snapshot = "advanced"; break;
            }
        }
        public IClientStorytellerSession Bind() => new Bound(this, Ports.GameOwner);
        private sealed record Bound(Fixture F, object Owner) : IClientStorytellerSession
        {
            public bool IsCurrent => ReferenceEquals(Owner, F.Ports.GameOwner);
            public bool IsPlaying => F.Playing;
            public ClientStorytellerBaseline Capture() { F.OnCapture?.Invoke(); return Baseline(); }
            public IClientStorytellerChange? Prepare(ClientStorytellerBaseline baseline)
            { F.Prepared++; F.Last = baseline; F.OnPrepare?.Invoke(); return F.Resolvable ? new Staged(F) : null; }
            public void LockCommitment() => F.Locked++;
        }
        private sealed record Staged(Fixture F) : IClientStorytellerChange
        { public void Apply() { F.Applied++; F.OnApply?.Invoke(); } }
    }
}
