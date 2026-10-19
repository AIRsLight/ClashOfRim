using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.CompatibilityClient;
using AIRsLight.ClashOfRim.MainMenu;
using AIRsLight.ClashOfRim.Modules.World;
using HarmonyLib;
using UnityEngine;
using Verse;

internal static class ClientMainMenuEntryTests
{
    private static readonly List<Window> windows = new();
    private static int forms, warnings;
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var previous = ClientServerEntryWindows.Current;
        using var engine = new ClientGameComponentTestRuntime("ClashOfRim.Tests.MenuEntry");
        using var gui = new ClientOverlayTestRuntime("ClashOfRim.Tests.MenuEntry");
        var harmony = new Harmony("ClashOfRim.Tests.MenuEntry");
        void Prefix(System.Reflection.MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientMainMenuEntryTests), prefix));
        void Getter(Type type, string name, string prefix) => Prefix(AccessTools.PropertyGetter(type, name), prefix);
        Getter(typeof(Find), nameof(Find.WindowStack), nameof(Stack));
        Getter(typeof(CompatibilityConfigOverlayPath), nameof(CompatibilityConfigOverlayPath.ServerProfileActive), nameof(No));
        Getter(typeof(Prefs), nameof(Prefs.DevMode), nameof(No));
        Getter(typeof(Event), nameof(Event.current), nameof(NoEvent));
        Prefix(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.WindowOfType), Type.EmptyTypes, new[] { typeof(ClashOfRimServerEntryDialog) }), nameof(Existing));
        Prefix(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.Add)), nameof(Add));
        Prefix(AccessTools.Method(typeof(Window), nameof(Window.Close)), nameof(Close));
        Prefix(AccessTools.Method(typeof(Window), nameof(Window.PostClose)), nameof(Skip));
        Prefix(AccessTools.Method(typeof(Log), nameof(Log.Warning)), nameof(Warning));
        harmony.Patch(AccessTools.Method(typeof(ClientWorldEntryStart), nameof(ClientWorldEntryStart.CreateForm)),
            postfix: new HarmonyMethod(typeof(ClientMainMenuEntryTests), nameof(FormCreated)));
        try
        {
            foreach (int length in new[] { 0, 1, 4 })
            {
                var f = Setup(gui); var menu = Options(length);
                Rewrite(menu); Check(menu.Count == length + 1 && menu[Math.Min(1, length)] is ClashOfRimMenuOption { Kind: ClashOfRimMenuOptionKind.ServerEntry },
                    "Server entry insertion displaced the original options.");
                Rewrite(menu); Check(menu.Count == length + 1 && forms == 1, "Repeated rewrite duplicated entry or created another form.");
                var join = menu.OfType<ClashOfRimMenuOption>().Single(); Invoke(join); Invoke(join);
                Check(windows.Count == 1 && windows[0] is ClashOfRimServerEntryDialog && f.Ends == 0 && f.Writes == 0, "Opening entry duplicated windows or started login.");
                var dialog = (ClashOfRimServerEntryDialog)windows[0]; var form = Form(dialog);
                Check(form.ServerBaseUrl == f.Ports.Server && form.UserId == f.Ports.User && form.Password == "", "Menu form lost defaults or copied a saved password.");
                form.Password = "close-secret"; dialog.Close(); Invoke(join);
                Check(form.Password == "" && !form.IsCurrent && windows.Count == 0, "Closed menu form reopened or retained its password.");
                var reopened = Options(length); Rewrite(reopened); Invoke(reopened.OfType<ClashOfRimMenuOption>().Single());
                Check(windows.Count == 1 && !ReferenceEquals(form, Form((ClashOfRimServerEntryDialog)windows[0])), "New menu could not open a fresh form.");
            }
            foreach (string change in new[] { "user", "server", "colony", "auth", "login", "generation", "game", "scene", "adapter", "label-login" })
            {
                var f = Setup(gui);
                if (change == "label-login") gui.Translating = key => { if (key == "ClashOfRim.ServerEntry.JoinTitle") f.Change("login"); };
                var menu = Options(4); Rewrite(menu);
                if (change == "game") { Current.Game = New<Game>(); f.Ports.GameOwner = Current.Game; }
                else if (change == "scene") Current.ProgramState = ProgramState.Playing;
                else if (change == "adapter") ClientServerEntryWindows.Attach(new(f.Start));
                else if (change != "label-login") f.Change(change);
                Invoke(menu.OfType<ClashOfRimMenuOption>().Single());
                Check(windows.Count == 0 && f.Ends == 0 && f.Writes == 0, "Old server entry option adopted " + change);
            }
            {
                var f = Setup(gui); var menu = Options(4); Rewrite(menu); f.Change("snapshot");
                Invoke(menu.OfType<ClashOfRimMenuOption>().Single());
                var dialog = (ClashOfRimServerEntryDialog)windows.Single(); var form = Form(dialog);
                form.ServerBaseUrl = " https://localhost/base/ "; form.UserId = " 玩家-İ "; form.Password = " 密码 😀 ";
                dialog.OnAcceptKeyPressed(); dialog.OnAcceptKeyPressed();
                (await f.Ports.Next())(); await f.Idle();
                Check(f.Ends == 1 && f.Writes == 1 && f.Hellos == 1 && f.Prepares == 1 && f.Scenarios == 1
                    && f.Captured?.ServerBaseUrl == "https://localhost/base" && f.Captured.UserId == "玩家-İ" && f.Password == " 密码 😀 "
                    && form.Password == "" && windows.Count == 0, "Native entry window bypassed or duplicated the existing login flow.");
            }
            foreach (string change in new[] { "none", "installed", "scene", "translation-installed" })
            {
                var f = Setup(gui); ClientServerEntryWindows.Attach(null);
                var menu = Options(4); Rewrite(menu);
                if (change == "installed") ClientServerEntryWindows.Attach(new(f.Start));
                if (change == "scene") Current.ProgramState = ProgramState.Playing;
                if (change == "translation-installed") gui.Translating = key => { if (key.EndsWith("ModUnavailable")) ClientServerEntryWindows.Attach(new(f.Start)); };
                Invoke(menu.OfType<ClashOfRimMenuOption>().Single());
                Check(windows.Count == (change == "none" ? 1 : 0) && warnings == (change == "none" ? 1 : 0) && f.Ends == 0,
                    "Unavailable menu displayed an obsolete warning or borrowed a later binding: " + change);
            }
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id); windows.Clear(); MainMenuDrawContext.End();
            ClientServerEntryWindows.Attach(previous); ClientSessionWork.Reset();
        }
    }
    private static ClientWorldEntryStartTests.Fixture Setup(ClientOverlayTestRuntime gui)
    {
        gui.Reset(); Current.ProgramState = ProgramState.Entry; windows.Clear(); forms = warnings = 0;
        var f = new ClientWorldEntryStartTests.Fixture(); Current.Game = New<Game>(); f.Ports.GameOwner = Current.Game; f.Ports.Password = "stored-secret";
        ClientServerEntryWindows.Attach(new(f.Start)); return f;
    }
    private static List<ListableOption> Options(int count) => Enumerable.Range(0, count).Select(i => new ListableOption(i.ToString(), () => { })).ToList();
    private static void Rewrite(List<ListableOption> options)
    {
        MainMenuDrawContext.Begin(600, true);
        try { ClashOfRimMainMenuPatches.Prefix(new Rect(0, 0, 170, 600), options); } finally { MainMenuDrawContext.End(); }
    }
    private static void Invoke(ListableOption option) => ((Action)AccessTools.Field(typeof(ListableOption), "action").GetValue(option))();
    private static ClientWorldEntryForm Form(ClashOfRimServerEntryDialog dialog) => (ClientWorldEntryForm)AccessTools.Field(typeof(ClashOfRimServerEntryDialog), "form").GetValue(dialog);
    private static void FormCreated() => forms++;
    private static bool Stack(ref WindowStack __result) { __result = New<WindowStack>(); return false; }
    private static bool Existing(ref ClashOfRimServerEntryDialog? __result) { __result = windows.OfType<ClashOfRimServerEntryDialog>().FirstOrDefault(); return false; }
    private static bool Add(Window __0) { windows.Add(__0); return false; }
    private static bool Close(Window __instance) { windows.Remove(__instance); __instance.PostClose(); return false; }
    private static bool Warning() { warnings++; return false; }
    private static bool No(ref bool __result) { __result = false; return false; }
    private static bool NoEvent(ref Event? __result) { __result = null; return false; }
    private static bool Skip() => false;
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
