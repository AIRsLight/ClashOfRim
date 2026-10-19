using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Admin;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.CompatibilityClient;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.Modules.Sessions;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static class ClientSettingsAccessTests
{
    private static Runtime native = null!;
    internal static void Run()
    {
        using var runtime = new Runtime(); native = runtime;
        using var ui = new ClientOverlayTestRuntime(nameof(ClientSettingsAccessTests));
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string language in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            foreach (bool configured in new[] { false, true })
            foreach (bool administrator in new[] { false, true })
            foreach (bool playing in new[] { false, true })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                var f = new Fixture(); if (!configured) f.Ports.Colony = ""; f.Access.SetAdministrator(administrator);
                Current.ProgramState = playing ? ProgramState.Playing : ProgramState.Entry;
                bool editable = !configured || administrator, debug = !playing || editable;
                Check(f.Access.CanEditLocalSettings == editable && DeveloperToolAccessPolicy.CanUseDeveloperTools() == debug, "Local permission semantics changed.");
                bool enable = true; Check(ClashOfRimPrefsDevModePatch.Prefix(ref enable) == debug && enable == debug, "DevMode setter admission changed.");
                enable = false; Check(ClashOfRimPrefsDevModePatch.Prefix(ref enable) && !enable, "Disabling DevMode was blocked.");
                native.DevMode = true;
                Check(ClashOfRimDebugWindowsOpenerPatch.Prefix() == debug && native.DevMode == debug, "Debug GUI was not disabled.");
                native.Removed.Clear(); native.DevMode = true; DebugSettings.godMode = DebugSettings.devPalette = true;
                DeveloperToolAccessPolicy.EnforceCurrentState();
                Check(native.Removed.Count == (debug ? 0 : 4) && native.DevMode == debug && DebugSettings.godMode == debug && DebugSettings.devPalette == debug, "Enforcement removed the wrong native tools.");
                f.Baseline.Flow.Apply(ClientModSettingsPolicyTests.Manifest);
                Check(ClashOfRimWriteModSettingsPatch.Prefix("example.mod", "ExampleMod", null!) == editable, "Controlled write gate disagrees with administrator role.");
                Check(ClashOfRimWriteModSettingsPatch.Prefix("example.mod", "Optional", null!) && ClashOfRimWriteModSettingsPatch.Prefix(ClashOfRimMod.PackageId, nameof(ClashOfRimMod), null!), "Optional/own settings were blocked.");
                var window = New<Dialog_ModSettings>();
                ui.Labels.Clear();
                Check(ClashOfRimDialogModSettingsPatch.Prefix(window, new Rect(0, 0, 400, 300)) == editable
                    && ClashOfRimDialogModSettingsPreClosePatch.Prefix(window) == editable, "Unknown settings window was allowed while locked.");
                Check(ui.Labels.Count == (editable ? 0 : 2), "Locked window did not render its existing explanation.");
                AccessTools.Field(typeof(Dialog_ModSettings), "mod").SetValue(window, New<ExampleMod>());
                Check(ModSettingsBaselinePolicy.CanEditModSettingsWindow(window) == editable, "Folder/package mapping bypassed controlled settings lock.");
                AccessTools.Field(typeof(Dialog_ModSettings), "mod").SetValue(window, New<UncontrolledMod>());
                Check(ModSettingsBaselinePolicy.CanEditModSettingsWindow(window), "Uncontrolled mod window was locked.");
            }
            PermissionLifetime(); ProfileLifecycle();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static void PermissionLifetime()
    {
        Current.ProgramState = ProgramState.Playing;
        foreach (string change in new[] { "user", "server", "colony", "auth", "login", "generation", "snapshot", "unavailable" })
        {
            var f = new Fixture(); f.Access.SetAdministrator(true);
            switch (change)
            {
                case "user": f.Ports.User = "other"; break;
                case "server": f.Ports.Server += "/other"; break;
                case "colony": f.Ports.Colony = "other"; break;
                case "auth": f.Ports.Auth = "other"; break;
                case "login": f.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "new" }); break;
                case "generation": ClientSessionWork.Reset(); break;
                case "snapshot": f.Ports.Snapshot = "new"; break;
                case "unavailable": f.Session.BeginUnavailable(null, null); break;
            }
            Check(DeveloperToolAccessPolicy.CanUseDeveloperTools() == (change is "snapshot" or "unavailable"), "Permission retained a different identity: " + change);
        }
        foreach (string missing in new[] { "server", "user", "colony" })
        {
            var f = new Fixture();
            if (missing == "server") f.Ports.Server = ""; else if (missing == "user") f.Ports.User = ""; else f.Ports.Colony = "";
            Check(f.Access.CanEditLocalSettings, "Unconfigured client locked local settings.");
        }
        var idle = new Fixture(); idle.Ports.Snapshot = ""; idle.Ports.Auth = "";
        Check(!idle.Access.CanEditLocalSettings, "Configured prelogin client bypassed local settings lock.");
        AccessTools.Field(typeof(DeveloperToolAccessPolicy), "access").SetValue(null, null);
        Check(DeveloperToolAccessPolicy.CanUseDeveloperTools(), "Uncomposed client disabled native developer tools.");
    }
    private static void ProfileLifecycle()
    {
        var f = new Fixture(); f.Baseline.Flow.Apply(ClientModSettingsPolicyTests.Manifest);
        CompatibilityConfigOverlayPath.Deactivate();
        Check(!CompatibilityConfigOverlayPath.ServerProfileActive, "Inactive process enabled profile.");
        CompatibilityConfigOverlayPath.Activate(ClientModSettingsPolicyTests.Manifest);
        string active = Path.Combine(native.Root, "ClashOfRimConfigOverlays", "active.json");
        Check(File.Exists(active) && File.ReadAllText(active).Contains("\"profileArmed\":true"), "Activation did not arm next boot.");
        Check(!ModSettingsBaselinePolicy.TryGetOverlaySettingsFilename("example.mod", "ExampleMod", out _), "Activation changed settings path before restart.");
        f.Baseline.Flow.Apply(null);
        AccessTools.Field(typeof(ModSettingsBaselinePolicy), "access").SetValue(null, null);
        AccessTools.Field(typeof(ModSettingsBaselinePolicy), "policy").SetValue(null,
            AccessTools.Method(typeof(ModSettingsBaselinePolicy), "CreatePolicy").Invoke(null, new object?[] { null }));
        AccessTools.Field(typeof(CompatibilityConfigOverlayPath), "session").SetValue(null, null);
        AccessTools.Field(typeof(CompatibilityConfigOverlayPath), "baseline").SetValue(null, null);
        ResetBoot();
        Check(CompatibilityConfigOverlayPath.ServerProfileActive && File.ReadAllText(active).Contains("\"profileArmed\":false"), "Boot did not consume profile arm.");
        string path = "original.xml";
        ClashOfRimGetSettingsFilenameOverlayPatch.Postfix("example.mod", "ExampleMod", ref path);
        Check(path.StartsWith(native.Root, StringComparison.OrdinalIgnoreCase) && path.EndsWith("Mod_example.mod_ExampleMod.xml", StringComparison.Ordinal)
            && Directory.Exists(Path.GetDirectoryName(path)), "Controlled filename did not use its isolated profile.");
        ModSettingsBaselinePolicy.Attach(f.Access, f.Baseline.Flow);
        CompatibilityConfigOverlayPath.Attach(f.Session, f.Baseline.Flow);
        Check(ModSettingsBaselinePolicy.TryGetOverlaySettingsFilename("example.mod", "ExampleMod", out string afterAttach) && afterAttach == path,
            "Composition changed the cold-boot profile path.");
        Check(CompatibilityConfigOverlayPath.TryResolveActiveConfigPath("EXAMPLE.MOD", "Optional", out _), "Profile membership incorrectly required Enforce.");
        Check(!ModSettingsBaselinePolicy.TryGetOverlaySettingsFilename("example.mod", "Optional", out _), "Ignored config path was forced by settings lock.");
        Check(!CompatibilityConfigOverlayPath.TryResolveActiveConfigPath("example.mod", "unknown", out _), "Undeclared file redirected.");
        f.Ports.Server = "http://another-server";
        Check(!CompatibilityConfigOverlayPath.TryResolveActiveConfigPath("example.mod", "ExampleMod", out _), "Active profile crossed server identity.");
        f.Ports.Server = "http://localhost"; ResetBoot();
        Check(!CompatibilityConfigOverlayPath.ServerProfileActive, "Consumed profile reactivated on another boot.");
        CompatibilityConfigOverlayPath.Activate(ClientModSettingsPolicyTests.Manifest); CompatibilityConfigOverlayPath.Deactivate();
        Check(!File.Exists(active) && !CompatibilityConfigOverlayPath.ServerProfileActive, "Deactivate retained next-boot profile.");
        ResetBoot(); Check(!CompatibilityConfigOverlayPath.ServerProfileActive, "Deleted profile revived.");
    }
    private static void ResetBoot()
    {
        foreach (string flag in new[] { "activeOverlayLoaded", "profileActivationChecked", "profileActiveForCurrentProcess" })
            AccessTools.Field(typeof(CompatibilityConfigOverlayPath), flag).SetValue(null, false);
        AccessTools.Field(typeof(CompatibilityConfigOverlayPath), "cachedActiveOverlay").SetValue(null, null);
    }
    private sealed class Fixture
    {
        internal readonly ClientCompatibilityBaselineTests.Fixture Baseline = new();
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientAdminAccess Access;
        internal Fixture()
        {
            Session = new(Ports, Ports, new ClientSessionConnection(Ports)); Access = new(Session);
            DeveloperToolAccessPolicy.Attach(Access);
            ModSettingsBaselinePolicy.Attach(Access, Baseline.Flow);
            CompatibilityConfigOverlayPath.Attach(Session, Baseline.Flow);
        }
    }
    private sealed class Runtime : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "ClashOfRim.SettingsTests-" + Guid.NewGuid().ToString("N"));
        internal bool DevMode;
        internal readonly List<Type> Removed = new();
        internal readonly ModContentPack Pack = New<ModContentPack>();
        private readonly bool savedGod = DebugSettings.godMode, savedPalette = DebugSettings.devPalette;
        private readonly Dictionary<FieldInfo, object?> saved = new();
        private readonly Harmony harmony = new(nameof(ClientSettingsAccessTests));
        internal Runtime()
        {
            foreach (var type in new[] { typeof(DeveloperToolAccessPolicy), typeof(ModSettingsBaselinePolicy), typeof(CompatibilityConfigOverlayPath) })
                foreach (var field in AccessTools.GetDeclaredFields(type).Where(f => f.IsStatic && !f.IsLiteral)) saved.Add(field, field.GetValue(null));
            ResetBoot(); Directory.CreateDirectory(Root);
            void Patch(MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientSettingsAccessTests), prefix));
            Patch(AccessTools.PropertyGetter(typeof(GenFilePaths), "ConfigFolderPath"), nameof(ConfigFolder));
            Patch(AccessTools.PropertyGetter(typeof(Prefs), nameof(Prefs.DevMode)), nameof(GetDevMode));
            Patch(AccessTools.PropertySetter(typeof(Prefs), nameof(Prefs.DevMode)), nameof(SetDevMode));
            Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.WindowStack)), nameof(Stack));
            Patch(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.TryRemove), new[] { typeof(Type), typeof(bool) }), nameof(Remove));
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(Messages)).Where(m => m.Name == nameof(Messages.Message))) Patch(method, nameof(Skip));
            Patch(AccessTools.PropertyGetter(typeof(LoadedModManager), nameof(LoadedModManager.RunningModsListForReading)), nameof(Mods));
            Patch(AccessTools.PropertyGetter(typeof(Mod), nameof(Mod.Content)), nameof(Content));
            Patch(AccessTools.PropertyGetter(typeof(ModContentPack), nameof(ModContentPack.FolderName)), nameof(FolderName));
            Patch(AccessTools.PropertyGetter(typeof(ModContentPack), nameof(ModContentPack.PackageId)), nameof(PackageId));
            Patch(AccessTools.PropertyGetter(typeof(ModContentPack), nameof(ModContentPack.PackageIdPlayerFacing)), nameof(PackageId));
        }
        public void Dispose()
        {
            harmony.UnpatchAll(harmony.Id); foreach (var entry in saved) entry.Key.SetValue(null, entry.Value);
            DebugSettings.godMode = savedGod; DebugSettings.devPalette = savedPalette;
            string full = Path.GetFullPath(Root), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("ClashOfRim.SettingsTests-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid temporary root.");
            Directory.Delete(full, true);
        }
    }
    private static bool ConfigFolder(ref string __result) { __result = native.Root; return false; }
    private static bool GetDevMode(ref bool __result) { __result = native.DevMode; return false; }
    private static bool SetDevMode(bool __0) { native.DevMode = __0; return false; }
    private static bool Stack(ref WindowStack __result) { __result = New<WindowStack>(); return false; }
    private static bool Remove(Type __0, ref bool __result) { native.Removed.Add(__0); __result = true; return false; }
    private static bool Mods(ref List<ModContentPack> __result) { __result = new() { native.Pack }; return false; }
    private static bool Content(ref ModContentPack __result) { __result = native.Pack; return false; }
    private static bool FolderName(ref string __result) { __result = "ExampleFolder"; return false; }
    private static bool PackageId(ref string __result) { __result = "example.mod"; return false; }
    private static bool Skip() => false;
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private sealed class ExampleMod : Mod { internal ExampleMod(ModContentPack content) : base(content) { } }
    private sealed class UncontrolledMod : Mod { internal UncontrolledMod(ModContentPack content) : base(content) { } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
