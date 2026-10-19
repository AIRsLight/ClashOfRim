using HarmonyLib;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.Modules.Sessions;
using RimWorld;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.CompatibilityClient;

[HarmonyPatch(typeof(Dialog_ModSettings), nameof(Dialog_ModSettings.DoWindowContents))]
public static class ClashOfRimDialogModSettingsPatch
{
    public static bool Prefix(Dialog_ModSettings __instance, Rect inRect)
    {
        if (ModSettingsBaselinePolicy.CanEditModSettingsWindow(__instance))
        {
            return true;
        }

        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, 0f, inRect.width, 35f), ClashOfRimText.Key("ClashOfRim.Compatibility.ModSettingsLockedTitle"));
        Text.Font = GameFont.Small;
        Widgets.Label(
            new Rect(0f, 48f, inRect.width, inRect.height - 48f),
            ClashOfRimText.Key("ClashOfRim.Compatibility.ModSettingsLockedBody"));
        return false;
    }
}

[HarmonyPatch(typeof(Dialog_ModSettings), nameof(Dialog_ModSettings.PreClose))]
public static class ClashOfRimDialogModSettingsPreClosePatch
{
    public static bool Prefix(Dialog_ModSettings __instance)
    {
        return ModSettingsBaselinePolicy.CanEditModSettingsWindow(__instance);
    }
}

[HarmonyPatch(typeof(LoadedModManager), nameof(LoadedModManager.WriteModSettings))]
public static class ClashOfRimWriteModSettingsPatch
{
    public static bool Prefix(string modIdentifier, string modHandleName, ModSettings settings)
    {
        return !ModSettingsBaselinePolicy.ShouldSuppressControlledSettingsWrite(modIdentifier, modHandleName);
    }
}

[HarmonyPatch]
public static class ClashOfRimGetSettingsFilenameOverlayPatch
{
    public static MethodBase? TargetMethod()
    {
        return AccessTools.Method(
            typeof(LoadedModManager),
            "GetSettingsFilename",
            new[] { typeof(string), typeof(string) });
    }

    public static void Postfix(string modIdentifier, string modHandleName, ref string __result)
    {
        if (ModSettingsBaselinePolicy.TryGetOverlaySettingsFilename(modIdentifier, modHandleName, out string overlayPath))
        {
            CompatibilityConfigOverlayPath.EnsureDirectoryFor(overlayPath);
            __result = overlayPath;
        }
    }
}

internal static class ModSettingsBaselinePolicy
{
    private static ClientAdminAccess? access;
    // Other mods may read their settings before this mod has been constructed.
    private static ClientModSettingsPolicy policy = CreatePolicy(null);
    internal static void Attach(ClientAdminAccess administration, ClientCompatibilityBaseline baseline)
    { access = administration; policy = CreatePolicy(baseline); }
    private static ClientModSettingsPolicy CreatePolicy(ClientCompatibilityBaseline? baseline) => new(baseline,
        () => CompatibilityConfigOverlayPath.ActiveManifestJson,
        message => Log.Warning("[ClashOfRim][Compatibility] Failed to parse server manifest for settings lock: " + message));

    public static bool CanEditModSettingsWindow(Dialog_ModSettings window)
    {
        if (CanEditAllModSettings())
        {
            return true;
        }

        Mod? target = GetDialogMod(window);
        if (target is null)
        {
            return false;
        }

        return !IsControlledConfig(target.Content.FolderName, target.GetType().Name);
    }

    public static bool ShouldSuppressControlledSettingsWrite(string modIdentifier, string modHandleName)
    {
        return !CanEditAllModSettings() && IsControlledConfig(modIdentifier, modHandleName);
    }

    public static bool TryGetOverlaySettingsFilename(string modIdentifier, string modHandleName, out string path)
    {
        path = string.Empty;
        if (!CompatibilityConfigOverlayPath.ServerProfileActive
            || IsClashOfRimConfig(modIdentifier, modHandleName)
            || !IsControlledConfig(modIdentifier, modHandleName))
        {
            return false;
        }

        string? packageId = ResolvePackageId(modIdentifier);
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        path = CompatibilityConfigOverlayPath.Resolve(packageId!, modHandleName);
        return true;
    }

    public static void InvalidateManifestCache() => policy.Invalidate();

    private static bool CanEditAllModSettings() => access?.CanEditLocalSettings != false;

    private static bool IsControlledConfig(string modIdentifier, string modHandleName)
    {
        if (string.IsNullOrWhiteSpace(modIdentifier) || string.IsNullOrWhiteSpace(modHandleName))
        {
            return false;
        }

        string? packageId = ResolvePackageId(modIdentifier);
        if (IsClashOfRimIdentifier(packageId) || IsClashOfRimConfig(modIdentifier, modHandleName))
        {
            return false;
        }

        return policy.IsControlled(packageId ?? modIdentifier, modHandleName);
    }

    private static Mod? GetDialogMod(Dialog_ModSettings window)
    {
        return typeof(Dialog_ModSettings)
            .GetField("mod", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(window) as Mod;
    }

    private static string? ResolvePackageId(string modIdentifier)
    {
        foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
        {
            if (string.Equals(mod.FolderName, modIdentifier, StringComparison.OrdinalIgnoreCase)
                || string.Equals(mod.PackageId, modIdentifier, StringComparison.OrdinalIgnoreCase)
                || string.Equals(mod.PackageIdPlayerFacing, modIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                return mod.PackageIdPlayerFacing ?? mod.PackageId;
            }
        }

        return modIdentifier;
    }

    private static bool IsClashOfRimConfig(string? modIdentifier, string? modHandleName)
    {
        if (string.Equals(modHandleName, nameof(ClashOfRimMod), StringComparison.Ordinal)
            || string.Equals(modHandleName, nameof(ClashOfRimSettings), StringComparison.Ordinal))
        {
            return true;
        }

        return IsClashOfRimIdentifier(ResolvePackageId(modIdentifier ?? string.Empty))
            || IsClashOfRimIdentifier(modIdentifier);
    }

    private static bool IsClashOfRimIdentifier(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(trimmed)
            && string.Equals(trimmed, ClashOfRimMod.PackageId, StringComparison.OrdinalIgnoreCase);
    }

}

internal static class CompatibilityConfigOverlayPath
{
    private static ClientSessionLifecycle? session;
    private static ClientCompatibilityBaseline? baseline;
    internal static void Attach(ClientSessionLifecycle lifecycle, ClientCompatibilityBaseline compatibility)
    { session = lifecycle; baseline = compatibility; }
    private const string OverlayRootFolderName = "ClashOfRimConfigOverlays";
    private const string ActiveOverlayFileName = "active.json";
    private static ActiveOverlayDto? cachedActiveOverlay;
    private static bool activeOverlayLoaded;
    private static bool profileActivationChecked;
    private static bool profileActiveForCurrentProcess;

    public static string Resolve(string packageId, string fileName, string? manifestJson = null)
    {
        return ResolveOverlayConfigPath(ResolveCurrentScope(manifestJson), packageId, fileName);
    }

    public static bool TryResolveActiveConfigPath(string packageId, string fileName, out string path)
    {
        path = string.Empty;
        ActiveOverlayDto? active = ResolveActiveOverlay();
        if (active is null
            || !EnsureProfileActivation()
            || !IsActiveForCurrentServer(active)
            || !ManifestDeclaresConfig(active.ManifestJson, packageId, fileName))
        {
            return false;
        }

        path = ResolveOverlayConfigPath(active.Scope, packageId, fileName);
        return true;
    }

    private static string ResolveOverlayConfigPath(string scope, string packageId, string fileName)
    {
        string root = Path.Combine(ResolveConfigFolder(), OverlayRootFolderName, scope);
        string safePackage = GenText.SanitizeFilename(packageId ?? string.Empty);
        string safeFile = GenText.SanitizeFilename(fileName ?? string.Empty);
        return Path.Combine(root, safePackage, "Mod_" + safePackage + "_" + safeFile + ".xml");
    }

    public static bool ServerProfileActive => EnsureProfileActivation();

    public static string? ActiveManifestJson => EnsureProfileActivation()
        ? ResolveActiveOverlay()?.ManifestJson
        : null;

    public static void Activate(string manifestJson)
    {
        string scope = ResolveCurrentScope(manifestJson);
        var active = new ActiveOverlayDto
        {
            Scope = scope,
            ManifestJson = manifestJson ?? string.Empty,
            ServerBaseUrl = ResolveCurrentServerBaseUrl(),
            ProfileArmed = true
        };
        WriteActiveOverlay(active);
        cachedActiveOverlay = active;
        activeOverlayLoaded = true;
        profileActivationChecked = true;
        profileActiveForCurrentProcess = false;
    }

    public static void Deactivate()
    {
        cachedActiveOverlay = null;
        activeOverlayLoaded = true;
        profileActivationChecked = true;
        profileActiveForCurrentProcess = false;
        string path = ResolveActiveOverlayPath();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static void EnsureDirectoryFor(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][Compatibility] Failed to remove stale config overlay " + path + ": " + ex.Message);
        }
    }

    private static string ResolveCurrentScope(string? manifestJsonOverride = null)
    {
        string manifestJson = manifestJsonOverride ?? baseline?.Manifest ?? string.Empty;
        string manifestId = TryReadManifestId(manifestJson) ?? string.Empty;
        string server = ResolveCurrentServerBaseUrl();
        ActiveOverlayDto? active = ResolveActiveOverlay();
        if (string.IsNullOrWhiteSpace(manifestId)
            && active is not null
            && IsActiveForCurrentServer(active)
            && (active.ProfileArmed || EnsureProfileActivation()))
        {
            return active.Scope;
        }

        string seed = string.IsNullOrWhiteSpace(manifestId)
            ? server
            : server + "|" + manifestId;
        if (!string.IsNullOrWhiteSpace(seed))
        {
            return HashText(seed);
        }

        return string.IsNullOrWhiteSpace(active?.Scope) ? "unconfigured" : active!.Scope;
    }

    private static string ResolveCurrentServerBaseUrl()
    {
        return (session?.ReadContext().ServerBaseUrl ?? string.Empty).Trim().TrimEnd('/');
    }

    private static bool IsActiveForCurrentServer(ActiveOverlayDto active)
    {
        string current = ResolveCurrentServerBaseUrl();
        return !string.IsNullOrWhiteSpace(current)
            && !string.IsNullOrWhiteSpace(active.ServerBaseUrl)
            && string.Equals(current, active.ServerBaseUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool EnsureProfileActivation()
    {
        if (profileActivationChecked)
        {
            return profileActiveForCurrentProcess;
        }

        profileActivationChecked = true;
        ActiveOverlayDto? active = ResolveActiveOverlay();
        if (active?.ProfileArmed != true)
        {
            return false;
        }

        // Mod settings load before login, so a server profile must be explicitly armed for this boot.
        profileActiveForCurrentProcess = true;
        active.ProfileArmed = false;
        WriteActiveOverlay(active);
        return true;
    }

    private static bool ManifestDeclaresConfig(string manifestJson, string packageId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(manifestJson)
            || string.IsNullOrWhiteSpace(packageId)
            || string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        try
        {
            return ClientModConfigManifest.Read(manifestJson)?.Declares(packageId, fileName) == true;
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][Compatibility] Failed to inspect active config overlay manifest: " + ex.Message);
            return false;
        }
    }

    private static string? TryReadManifestId(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return null;
        }

        try
        {
            return ClientModConfigManifest.Read(manifestJson)?.Id;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveConfigFolder()
    {
        PropertyInfo? property = typeof(GenFilePaths).GetProperty(
            "ConfigFolderPath",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.GetValue(null) is string path && !string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return Path.Combine(GenFilePaths.SaveDataFolderPath, "Config");
    }

    private static string ResolveActiveOverlayPath()
    {
        return Path.Combine(ResolveConfigFolder(), OverlayRootFolderName, ActiveOverlayFileName);
    }

    private static void WriteActiveOverlay(ActiveOverlayDto active)
    {
        string path = ResolveActiveOverlayPath();
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var serializer = new DataContractJsonSerializer(typeof(ActiveOverlayDto));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        serializer.WriteObject(stream, active);
    }

    private static ActiveOverlayDto? ResolveActiveOverlay()
    {
        if (activeOverlayLoaded)
        {
            return cachedActiveOverlay;
        }

        activeOverlayLoaded = true;
        string path = ResolveActiveOverlayPath();
        if (!File.Exists(path))
        {
            cachedActiveOverlay = null;
            return null;
        }

        try
        {
            var serializer = new DataContractJsonSerializer(typeof(ActiveOverlayDto));
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            cachedActiveOverlay = serializer.ReadObject(stream) as ActiveOverlayDto;
            return cachedActiveOverlay;
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][Compatibility] Failed to read active config overlay: " + ex.Message);
            cachedActiveOverlay = null;
            return null;
        }
    }

    private static string HashText(string text)
    {
        using SHA256 sha = SHA256.Create();
        byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
        return string.Concat(bytes.Select(b => b.ToString("x2")));
    }

    [DataContract]
    private sealed class ActiveOverlayDto
    {
        [DataMember(Name = "scope")]
        public string Scope { get; set; } = string.Empty;

        [DataMember(Name = "manifestJson")]
        public string ManifestJson { get; set; } = string.Empty;

        [DataMember(Name = "serverBaseUrl")]
        public string ServerBaseUrl { get; set; } = string.Empty;

        [DataMember(Name = "profileArmed")]
        public bool ProfileArmed { get; set; }
    }

}
