using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Multiplayer;
using AIRsLight.ClashOfRim.Multiplayer.Pages;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static class MultiplayerWindowTests
{
    private static Runtime native = null!;
    internal static void Run()
    {
        using var runtime = new Runtime(); native = runtime;
        using var ui = new ClientOverlayTestRuntime(nameof(MultiplayerWindowTests));
        var culture = CultureInfo.CurrentCulture;
        try
        {
            Wiring(); MainButton();
            foreach (string language in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language); Navigation(); }
            foreach (string change in new[] { "user", "auth", "server", "colony", "login", "generation", "game", "close", "rebind", "snapshot" }) Ownership(change);
            Permissions(); Cache(ui); CloseAndReopen();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }

    private static object Field(object target, string name) => AccessTools.Field(target.GetType(), name).GetValue(target)!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Wiring()
    {
        var f = new Fixture(); var first = f.Factory.Create(); var second = f.Factory.Create();
        Check(ReferenceEquals(Field(first.Diplomacy, "players"), first.Players)
            && ReferenceEquals(Field(Field(first.Achievements, "panel"), "players"), first.Players), "Player pages lost their shared model.");
        Check(ReferenceEquals(Field(first.Shop, "panel"), first.ShopModel)
            && ReferenceEquals(Field(Field(Field(first.Admin, "shop"), "model"), "shop"), first.ShopModel), "Admin and player shops lost their shared model.");
        Check(ReferenceEquals(Field(first.Bank, "panel"), first.BankModel)
            && ReferenceEquals(((Delegate)Field(Field(first.Mercenary, "panel"), "readBank")).Target, first.BankModel), "Mercenary and bank pages lost their shared model.");
        Check(!ReferenceEquals(first.Players, second.Players) && !ReferenceEquals(first.ShopModel, second.ShopModel)
            && !ReferenceEquals(first.BankModel, second.BankModel) && !ReferenceEquals(first.Account, second.Account), "Different windows share mutable page state.");
    }
    private static void MainButton()
    {
        var f = new Fixture(); MainButtonWorker_ClashOfRimMultiplayer.Attach(f.Admin.Session);
        var button = new MainButtonWorker_ClashOfRimMultiplayer();
        Check(!button.Visible, "Configured client without login exposed the main button.");
        f.Admin.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "session" });
        foreach (bool visible in new[] { false, true })
        {
            native.BaseVisible = visible;
            Check(button.Visible == visible, "Multiplayer button bypassed native visibility.");
            foreach (string missing in new[] { "server", "user", "colony" })
            {
                var previous = (f.Admin.Ports.Server, f.Admin.Ports.User, f.Admin.Ports.Colony);
                if (missing == "server") f.Admin.Ports.Server = ""; else if (missing == "user") f.Admin.Ports.User = ""; else f.Admin.Ports.Colony = "";
                Check(!button.Visible, "Incomplete session exposed the main button: " + missing);
                (f.Admin.Ports.Server, f.Admin.Ports.User, f.Admin.Ports.Colony) = previous;
            }
        }
    }
    private static void Navigation()
    {
        var f = new Fixture(); native.DevMode = true; f.Draw();
        Check(native.Tabs.Count == 9, "Missing main tabs.");
        foreach (string tab in new[] { "Shop", "Bank", "Mercenary", "Diplomacy", "Achievements", "Account", "Diagnostics", "Admin", "Trade" })
        {
            native.Trace.Clear(); Click(tab); f.Draw();
            Check(native.Trace.Contains("Multiplayer" + tab + "Page.Draw"), "Wrong drawn page: " + tab);
            string? refresh = tab switch
            {
                "Diplomacy" or "Achievements" => "ClientPlayerPanel.RefreshAsync",
                "Mercenary" or "Bank" => "MultiplayerBankPage.Tick",
                "Shop" or "Trade" or "Admin" => "Multiplayer" + tab + "Page.Tick", _ => null
            };
            if (refresh is not null) Check(native.Trace.Contains(refresh), "Wrong refresh route: " + tab);
            string? request = tab switch
            {
                "Diplomacy" or "Achievements" => "ClientPlayerPanel.RequestRefresh",
                "Mercenary" or "Bank" => "ClientBankPanel.RequestRefresh",
                "Shop" => "ClientShopPanel.RequestRefresh",
                "Trade" or "Admin" => "Multiplayer" + tab + "Page.RequestRefresh", _ => null
            };
            if (request is not null) Check(native.Trace.Contains(request), "Wrong selection refresh: " + tab);
            if (tab == "Achievements") Check(native.Trace.Contains("MultiplayerAchievementsPage.RequestRefresh") && native.Trace.Contains("MultiplayerAchievementsPage.Tick"), "Achievements lost its own refresh.");
            native.Trace.Clear(); Click(tab);
            Check(native.Trace.Count == 0, "Selecting the active tab repeated refresh.");
        }
    }
    private static void Ownership(string change)
    {
        var f = new Fixture(); f.Draw(); Action old = Tab("Shop").clickedAction;
        switch (change)
        {
            case "game": Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game)); break;
            case "close": f.Window.PostClose(); break;
            case "rebind": ClashOfRimMultiplayerWindow.Attach(new Fixture().Factory); break;
            default: f.Admin.Change(change); break;
        }
        native.Trace.Clear(); old();
        Check(native.Trace.Contains("ClientShopPanel.RequestRefresh") == (change == "snapshot"), "Stale main-tab callback accepted or snapshot advancement rejected: " + change);
        f.Draw();
        Check(native.Trace.Contains(change == "snapshot" ? "MultiplayerShopPage.Draw" : "MultiplayerTradePage.Draw"), "Old tab changed the reopened/current screen: " + change);
    }
    private static void Permissions()
    {
        var f = new Fixture(); native.DevMode = true; f.Draw();
        Action debug = Tab("Diagnostics").clickedAction, admin = Tab("Admin").clickedAction;
        Click("Admin"); f.Admin.Access.SetAdministrator(false); native.Trace.Clear(); f.Draw();
        Check(!native.Trace.Contains("MultiplayerAdminPage.Tick") && native.Trace.Contains("MultiplayerTradePage.Draw"), "Revoked administrator page refreshed before fallback.");
        admin(); Check(!native.Trace.Contains("MultiplayerAdminPage.RequestRefresh"), "Old administrator tab bypassed revoked access.");
        debug(); f.Draw(); native.DevMode = false; native.Trace.Clear(); f.Draw(); debug(); f.Draw();
        Check(!native.Trace.Contains("MultiplayerDiagnosticsPage.Draw") && native.Tabs.Count == 7, "Hidden developer page stayed active.");
    }
    private static void Cache(ClientOverlayTestRuntime ui)
    {
        var f = new Fixture(); f.Draw(); var first = native.Tabs; f.Draw();
        Check(ReferenceEquals(first, native.Tabs), "Stable main tabs rebuilt every frame.");
        LanguageDatabase.activeLanguage = (LoadedLanguage)RuntimeHelpers.GetUninitializedObject(typeof(LoadedLanguage)); f.Draw();
        Check(!ReferenceEquals(first, native.Tabs), "Language change retained old labels.");
        native.Trace.Clear(); LanguageDatabase.activeLanguage = (LoadedLanguage)RuntimeHelpers.GetUninitializedObject(typeof(LoadedLanguage));
        bool changed = false;
        ui.Translating = _ => { if (!changed) { changed = true; f.Admin.Ports.User = "other"; } };
        f.Draw(); ui.Translating = null;
        Check(!native.Trace.Any(x => x.EndsWith(".Draw", StringComparison.Ordinal)), "Translation reentry drew pages for an expired session.");
    }
    private static void CloseAndReopen()
    {
        var f = new Fixture(); f.Draw(); var pages = (MultiplayerWindowPages)Field(f.Window, "pages");
        var oldOwner = pages.Players.Ownership; native.Trace.Clear(); f.Window.PostClose();
        Check(!ReferenceEquals(oldOwner, pages.Players.Ownership), "Closing retained player and tab lifetime.");
        foreach (string page in new[] { "Achievements", "Trade", "Shop", "Bank", "Mercenary", "Account", "Admin" })
            Check(native.Trace.Count(x => x == "Multiplayer" + page + "Page.Close") == 1, "Close skipped or repeated child cleanup: " + page);
        Check(native.Trace.Count(x => x == "MultiplayerDiplomacyPage.Reset") == 1, "Diplomacy state was not reset.");
        f.Draw(); native.Trace.Clear(); Click("Achievements"); native.AchievementCloses = true; f.Draw(); native.AchievementCloses = false;
        Check(native.Trace.Contains("Window.Close"), "Trophy placement no longer closes the main window.");
        ClashOfRimMultiplayerWindow.Attach(null); native.Trace.Clear(); f.Draw();
        Check(native.Trace.Count == 0, "Unbound window entered a business page.");
    }
    private static TabRecord Tab(string name) => native.Tabs.Single(t => t.label == "ClashOfRim.Multiplayer.Tab" + name);
    private static void Click(string name) => Tab(name).clickedAction();

    private sealed class Fixture
    {
        internal readonly ClientAdministrationTests.Fixture Admin = new();
        internal readonly MultiplayerPageFactory Factory;
        internal readonly ClashOfRimMultiplayerWindow Window = new();
        internal Fixture()
        {
            // These domain instances are inert dependencies: this test exercises the
            // real composition and shell; existing panel tests execute their workflows.
            var constructor = typeof(MultiplayerPageFactory).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            object Dependency(Type type) => type == typeof(ClientSessionLifecycle) ? Admin.Session
                : type == typeof(ClientAdminAccess) ? Admin.Access : type == typeof(ClientAdministration) ? Admin.Module
                : RuntimeHelpers.GetUninitializedObject(type);
            Factory = (MultiplayerPageFactory)constructor.Invoke(constructor.GetParameters().Select(p => Dependency(p.ParameterType)).ToArray());
            ClashOfRimMultiplayerWindow.Attach(Factory); native.DevMode = false;
        }
        internal void Draw() => Window.DoWindowContents(new Rect(0, 0, 900, 600));
    }
    private sealed class Runtime : IDisposable
    {
        internal bool DevMode, BaseVisible = true, AchievementCloses;
        internal List<TabRecord> Tabs = new();
        internal readonly List<string> Trace = new();
        private readonly Harmony harmony = new(nameof(MultiplayerWindowTests));
        private readonly Game previousGame = Current.Game;
        private readonly LoadedLanguage previousLanguage = LanguageDatabase.activeLanguage;
        private readonly object? previousFactory = AccessTools.Field(typeof(ClashOfRimMultiplayerWindow), "factory").GetValue(null);
        private readonly object? previousSession = AccessTools.Field(typeof(MainButtonWorker_ClashOfRimMultiplayer), "session").GetValue(null);
        internal Runtime()
        {
            void Patch(MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(MultiplayerWindowTests), prefix));
            Patch(AccessTools.PropertyGetter(typeof(Prefs), nameof(Prefs.DevMode)), nameof(DevMode));
            Patch(AccessTools.PropertyGetter(typeof(MainButtonWorker), nameof(MainButtonWorker.Visible)), nameof(BaseVisibility));
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(TabDrawer)).Where(m => m.Name == nameof(TabDrawer.DrawTabs)))
                Patch(method.IsGenericMethodDefinition ? method.MakeGenericMethod(typeof(TabRecord)) : method, nameof(Tabs));
            Patch(AccessTools.Method(typeof(Window), nameof(Window.Close)), nameof(Record));
            Patch(AccessTools.Method(typeof(Window), nameof(Window.PostClose)), nameof(Skip));
            foreach (var type in new[] { typeof(MultiplayerTradePage), typeof(MultiplayerShopPage), typeof(MultiplayerBankPage), typeof(MultiplayerMercenaryPage), typeof(MultiplayerDiplomacyPage), typeof(MultiplayerAchievementsPage), typeof(MultiplayerAccountPage), typeof(MultiplayerDiagnosticsPage), typeof(MultiplayerAdminPage) })
                foreach (var method in AccessTools.GetDeclaredMethods(type).Where(m => m.Name is "Draw" or "Tick" or "RequestRefresh" or "Close" or "Reset"))
                    Patch(method, method.ReturnType == typeof(bool) ? nameof(Achievement) : nameof(Record));
            Patch(AccessTools.Method(typeof(ClientPlayerPanel), "RefreshAsync"), nameof(Refresh));
            foreach (var type in new[] { typeof(ClientPlayerPanel), typeof(ClientBankPanel), typeof(ClientShopPanel) })
                Patch(AccessTools.Method(type, "RequestRefresh"), nameof(Record));
        }
        public void Dispose()
        {
            harmony.UnpatchAll(harmony.Id); Current.Game = previousGame; LanguageDatabase.activeLanguage = previousLanguage;
            AccessTools.Field(typeof(ClashOfRimMultiplayerWindow), "factory").SetValue(null, previousFactory);
            AccessTools.Field(typeof(MainButtonWorker_ClashOfRimMultiplayer), "session").SetValue(null, previousSession);
        }
    }
    private static bool Record(MethodBase __originalMethod) { native.Trace.Add(__originalMethod.DeclaringType!.Name + "." + __originalMethod.Name); return false; }
    private static bool Skip() => false;
    private static bool DevMode(ref bool __result) { __result = native.DevMode; return false; }
    private static bool BaseVisibility(ref bool __result) { __result = native.BaseVisible; return false; }
    private static bool Tabs(object[] __args) { native.Tabs = __args.OfType<List<TabRecord>>().Single(); return false; }
    private static bool Refresh(MethodBase __originalMethod, ref Task __result) { Record(__originalMethod); __result = Task.CompletedTask; return false; }
    private static bool Achievement(MethodBase __originalMethod, ref bool __result) { Record(__originalMethod); __result = native.AchievementCloses; return false; }
}
