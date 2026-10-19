using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.MainMenu;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static class ClientMainMenuSavingTests
{
    internal static void Run()
    {
        try { RunAsync().GetAwaiter().GetResult(); }
        catch (Exception exception) { Console.WriteLine(exception.StackTrace); throw; }
    }
    private static async Task RunAsync()
    {
        foreach (string change in new[] { "user", "server", "colony", "auth", "game", "world", "world-object", "login", "same-login", "generation", "unavailable" })
        {
            using var f = new ClientSnapshotSavingTests.Fixture(); var command = f.Module.PrepareMenu()!;
            Check(command.IsCurrent && !f.Sync.IsBusy && f.Ports.Captures == 0, "Menu preparation acquired upload resources.");
            if (change == "unavailable") f.Session.BeginUnavailable(f.Ports.Auth, null); else f.Change(change);
            await f.Ports.PumpUntil(command.StartAsync(() => f.Exits++));
            Check(!command.IsCurrent && f.Uploads == 0 && f.Ports.Captures == 0 && f.Exits == 0 && !f.Sync.IsBusy, "Retained menu adopted " + change);
        }
        using (var f = new ClientSnapshotSavingTests.Fixture())
        {
            var command = f.Module.PrepareMenu()!; f.Change("snapshot"); f.Ports.SaveBytes = SnapshotPackageModuleTests.Save("other");
            Task work = command.StartAsync(); await command.StartAsync(); await f.Ports.PumpUntil(work); await f.Registrations(2);
            Check(f.Uploads == 1 && f.Ports.Published == 1 && f.Exits == 0 && !f.Sync.IsBusy, "Menu snapshot advancement or single choice failed.");
        }
        using (var f = new ClientSnapshotSavingTests.Fixture())
        {
            var command = f.Module.PrepareMenu()!; Check(f.Sync.TryBeginManual(out var lease), "Fixture busy lease.");
            await command.StartAsync(); lease!.Dispose(); await command.StartAsync();
            Check(f.Uploads == 0 && f.Messages.Count == 1, "Busy menu retried a consumed choice.");
            await f.Ports.PumpUntil(f.Module.PrepareMenu()!.StartAsync()); await f.Registrations(2);
            Check(f.Uploads == 1, "New menu could not retry busy save.");
        }
        await Engine();
    }
    private const string Upload = "ClashOfRim.Menu.UploadSnapshot", QuitMenu = "ClashOfRim.Menu.UploadSnapshotAndQuitToMainMenu",
        QuitOS = "ClashOfRim.Menu.UploadSnapshotAndQuitToOS", Close = "ClashOfRim.RemoteSessionMap.Close";
    private static GameInfo info = null!;
    private static bool disabled;
    private static int tabCloses, exits;
    private static Action? tabClosing;
    private static readonly List<Task> observed = new();
    private static async Task Engine()
    {
        var previousSaving = ClientSnapshotSavingCallbacks.Current; var previousExit = ClientSessionExitCallbacks.Current;
        using var engine = new ClientGameComponentTestRuntime("ClashOfRim.Tests.MenuSaving");
        using var gui = new ClientOverlayTestRuntime("ClashOfRim.Tests.MenuSaving");
        var harmony = new Harmony("ClashOfRim.Tests.MenuSaving");
        void Getter(Type type, string name, string prefix) => harmony.Patch(AccessTools.PropertyGetter(type, name), prefix: new HarmonyMethod(typeof(ClientMainMenuSavingTests), prefix));
        void Method(Type type, string name, string prefix) => harmony.Patch(AccessTools.Method(type, name), prefix: new HarmonyMethod(typeof(ClientMainMenuSavingTests), prefix));
        Getter(typeof(Game), nameof(Game.Info), nameof(Info));
        Getter(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SavingIsTemporarilyDisabled), nameof(Disabled));
        Getter(typeof(Find), nameof(Find.MainTabsRoot), nameof(Tabs));
        Method(typeof(MainTabsRoot), nameof(MainTabsRoot.SetCurrentTab), nameof(CloseTabs));
        Method(typeof(GenScene), nameof(GenScene.GoToMainMenu), nameof(ExitMenu));
        Method(typeof(Root), nameof(Root.Shutdown), nameof(ExitOS));
        foreach (var (type, name) in new[] { (typeof(ClientSnapshotSaving), nameof(ClientSnapshotSaving.StartAsync)),
            (typeof(ClientSessionExitCallbacks), nameof(ClientSessionExitCallbacks.EndAsync)) })
            harmony.Patch(AccessTools.Method(type, name), postfix: new HarmonyMethod(typeof(ClientMainMenuSavingTests), nameof(Observe)));
        try
        {
            // Golden menu layouts cover normal/permadeath, disabled saving,
            // local files, observation/battle and absent multiplayer snapshots.
            var layouts = new (bool Perma, bool Disabled, bool Files, string Remote, bool Eligible, string Expected)[]
            {
                (false, false, true, "", true, $"{Upload},2,3,4,5"),
                (false, false, false, "", true, $"{Upload},1,2,3,4,5"),
                (false, false, true, "observation", true, $"{Close},{Upload},2,3,4,5"),
                (false, false, true, "battle", true, $"{Upload},2,3,4,5"),
                (false, false, true, "", false, "0,1,2,3,4,5"),
                (false, false, true, "observation", false, $"{Close},0,2,3,4,5"),
                (false, false, true, "battle", false, "0,2,3,4,5"),
                (true, false, true, "", true, $"0,1,{QuitMenu},{QuitOS},4,5"),
                (true, false, true, "observation", true, $"{Close},0,1,{QuitMenu},{QuitOS},4,5"),
                (true, false, false, "battle", true, $"0,1,{QuitMenu},{QuitOS},4,5"),
                (false, true, true, "", true, "1,2,3,4,5"),
                (false, true, false, "", true, "0,1,2,3,4,5"),
                (false, true, true, "observation", true, $"{Close},1,2,3,4,5"),
                (true, true, true, "observation", true, $"{Close},0,1,2,3,4,5")
            };
            foreach (var row in layouts)
            {
                using var f = Setup(gui); info.permadeathMode = row.Perma; disabled = row.Disabled;
                if (!row.Eligible) f.Ports.Snapshot = "";
                if (row.Remote == "observation") ClashOfRimGameComponent.SetActiveObservationSession(ActiveRemoteMapSession.FromObservation("observe", "Scout", "bob", "c", "s"));
                if (row.Remote == "battle") ClashOfRimGameComponent.SetActiveRaidBattleSession(new() { EventId = "raid" });
                var menu = Rewrite(row.Files);
                Check(string.Join(',', menu.Select(Label)) == row.Expected, "Menu layout changed: " + row);
                foreach (var option in menu.Where(o => Label(o).StartsWith("ClashOfRim.Menu.")))
                    Check(option.minHeight == (Label(option) == Upload ? 10 : Label(option) == QuitMenu ? 12 : 13), "Replacement lost original height.");
                Check(f.Ports.Captures == 0 && f.Uploads == 0, "Drawing menu started saving.");
            }
            foreach (string action in new[] { Upload, QuitMenu, QuitOS })
            {
                using var f = Setup(gui); info.permadeathMode = action != Upload;
                var option = Rewrite(true).Single(o => Label(o) == action);
                Invoke(option); Invoke(option); await Pump(f);
                Check(f.Uploads == 1 && f.Ports.Published == 1 && exits == (action == Upload ? 0 : 1), "Menu did not share the original save/exit workflow.");
                if (action == Upload) await f.Registrations(2);
                if (action != Upload)
                {
                    await f.Logout.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(f.Logouts.Count == 1 && f.Logouts.Single().Context.AuthToken == "auth" && f.Session.SessionId is null,
                        "Saved exit replayed logout or borrowed cleared credentials.");
                }
            }
            foreach (string change in new[] { "auth", "game", "adapter", "tab-auth", "translation-auth" })
            {
                using var f = Setup(gui);
                if (change == "translation-auth") gui.Translating = key => { if (key == Upload) f.Change("auth"); };
                var option = Rewrite(true).Single(o => Label(o) == Upload);
                if (change == "auth") f.Change("auth");
                if (change == "game") { Current.Game = New<Game>(); f.Ports.GameOwner = Current.Game; }
                if (change == "adapter") ClientSnapshotSavingCallbacks.Attach(new(f.Module));
                if (change == "tab-auth") tabClosing = () => f.Change("auth");
                Invoke(option); await Pump(f);
                Check(f.Ports.Captures == 0 && f.Uploads == 0 && exits == 0 && tabCloses == (change == "tab-auth" ? 1 : 0), "Old menu callback crossed " + change);
            }
            foreach (string state in new[] { "ready", "unbound", "no-session", "no-snapshot", "busy", "mutation", "unavailable" })
            {
                using var f = Setup(gui); AIRsLight.ClashOfRim.Modules.Synchronization.ClientSynchronizationCoordinator.Lease? lease = null;
                if (state == "unbound") ClientSnapshotSavingCallbacks.Attach(null);
                if (state == "no-session") f.Session.ClearIdentity();
                if (state == "no-snapshot") f.Ports.Snapshot = "";
                if (state == "busy") f.Sync.TryBeginManual(out lease);
                if (state == "mutation") f.Change("mutation");
                if (state == "unavailable") f.Session.BeginUnavailable(f.Ports.Auth, null);
                var menu = Rewrite(true);
                bool replacesMenu = state is not ("unbound" or "no-session" or "no-snapshot");
                Check(menu.Any(option => Label(option) == Upload) == replacesMenu && menu.Any(option => Label(option) == "1") != replacesMenu,
                    "Multiplayer menu fell back to vanilla save/load while temporarily blocked: " + state);
                bool vanilla = AutosaveSnapshotUploadPatch.Prefix(); await Pump(f); lease?.Dispose();
                Check(vanilla == (state is "unbound" or "no-session") && f.Uploads == (state == "ready" ? 1 : 0), "Autosave interception changed: " + state);
                if (state == "ready") { await f.Registrations(2); Check(f.UploadKind == ModSnapshotUploadKinds.AutoSave, "Autosave lost upload kind."); }
                else Check(f.Messages.Count == 0, "Skipped autosave became interactive.");
            }
            foreach (bool shutdown in new[] { false, true })
            {
                using var f = Setup(gui);
                if (shutdown) ClashOfRimShutdownDisconnectPatch.Prefix(); else ClashOfRimGoToMainMenuDisconnectPatch.Prefix();
                await Pump(f);
                Check(f.Logouts.Count == 1 && f.Session.SessionId is null && f.Uploads == 0, "Immediate exit did not use the session lifecycle.");
            }
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id); MainMenuDrawContext.End(); observed.Clear(); tabClosing = null;
            ClientSnapshotSavingCallbacks.Attach(previousSaving); ClientSessionExitCallbacks.Attach(previousExit);
        }
    }
    private static ClientSnapshotSavingTests.Fixture Setup(ClientOverlayTestRuntime gui)
    {
        gui.Reset(); observed.Clear(); tabCloses = exits = 0; tabClosing = null; disabled = false; info = New<GameInfo>();
        var f = new ClientSnapshotSavingTests.Fixture(); var game = New<Game>(); Current.Game = game; f.Ports.GameOwner = game;
        AccessTools.Field(typeof(Game), "components").SetValue(game, new List<GameComponent> { new ClashOfRimGameComponent(game) });
        ClientSnapshotSavingCallbacks.Attach(new(f.Module)); ClientSessionExitCallbacks.Attach(new(f.Session));
        return f;
    }
    private static List<ListableOption> Rewrite(bool files)
    {
        var options = Enumerable.Range(0, 6).Select(i => new ListableOption(i.ToString(), () => { }) { minHeight = 10 + i }).ToList();
        MainMenuDrawContext.Begin(600, files);
        try { ClashOfRimMainMenuPatches.Prefix(new Rect(0, 0, 170, 600), options); } finally { MainMenuDrawContext.End(); }
        return options;
    }
    private static string Label(ListableOption option) => (string)AccessTools.Field(typeof(ListableOption), "label").GetValue(option);
    private static void Invoke(ListableOption option) => ((Action)AccessTools.Field(typeof(ListableOption), "action").GetValue(option))();
    private static bool Info(ref GameInfo __result) { __result = info; return false; }
    private static bool Disabled(ref bool __result) { __result = disabled; return false; }
    private static bool Tabs(ref MainTabsRoot __result) { __result = New<MainTabsRoot>(); return false; }
    private static bool CloseTabs() { tabCloses++; tabClosing?.Invoke(); return false; }
    private static bool ExitMenu() { ClashOfRimGoToMainMenuDisconnectPatch.Prefix(); exits++; return false; }
    private static bool ExitOS() { ClashOfRimShutdownDisconnectPatch.Prefix(); exits++; return false; }
    private static void Observe(Task __result) => observed.Add(__result);
    private static async Task Pump(ClientSnapshotSavingTests.Fixture fixture) { var tasks = Task.WhenAll(observed.ToArray()); observed.Clear(); await fixture.Ports.PumpUntil(tasks); }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
