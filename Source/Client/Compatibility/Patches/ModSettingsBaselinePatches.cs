using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
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
        if (ModSettingsBaselinePolicy.CanWriteModSettings(modIdentifier, modHandleName))
        {
            return true;
        }

        ModSettingsBaselinePolicy.NotifyBlocked(modIdentifier, modHandleName);
        return false;
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
            __result = overlayPath;
        }
    }
}

internal static class ModSettingsBaselinePolicy
{
    private static float nextNotificationAt;
    private static string? cachedManifestJson;
    private static CompatibilityManifestDto? cachedManifest;

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

    public static bool CanWriteModSettings(string modIdentifier, string modHandleName)
    {
        if (IsClashOfRimConfig(modIdentifier, modHandleName))
        {
            return true;
        }

        return CanEditAllModSettings() || !IsControlledConfig(modIdentifier, modHandleName);
    }

    public static bool TryGetOverlaySettingsFilename(string modIdentifier, string modHandleName, out string path)
    {
        path = string.Empty;
        if (IsClashOfRimConfig(modIdentifier, modHandleName)
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

    public static void InvalidateManifestCache()
    {
        cachedManifestJson = null;
        cachedManifest = null;
    }

    private static bool CanEditAllModSettings()
    {
        ClashOfRimMod? mod = LoadedModManager.GetMod<ClashOfRimMod>();
        return mod is null || mod.CanEditModSettings;
    }

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

        CompatibilityManifestDto? manifest = ReadServerManifest();
        if (manifest is null)
        {
            return false;
        }

        bool presentInBaseline = manifest.Mods.Any(mod =>
            string.Equals(mod.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
            && mod.Configs.Any(config => string.Equals(config.FileName, modHandleName, StringComparison.OrdinalIgnoreCase)));
        if (!presentInBaseline)
        {
            return false;
        }

        return string.Equals(ResolveConfigMode(manifest, packageId ?? modIdentifier, modHandleName), "Enforce", StringComparison.OrdinalIgnoreCase);
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

    private static CompatibilityManifestDto? ReadServerManifest()
    {
        ClashOfRimMod? mod = LoadedModManager.GetMod<ClashOfRimMod>();
        string json = mod?.ServerCompatibilityManifestJson ?? string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            json = CompatibilityConfigOverlayPath.ActiveManifestJson ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            cachedManifestJson = null;
            cachedManifest = null;
            return null;
        }

        if (string.Equals(cachedManifestJson, json, StringComparison.Ordinal) && cachedManifest is not null)
        {
            return cachedManifest;
        }

        try
        {
            var serializer = new DataContractJsonSerializer(typeof(CompatibilityManifestDto));
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            cachedManifest = serializer.ReadObject(stream) as CompatibilityManifestDto;
            cachedManifestJson = json;
            return cachedManifest;
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][Compatibility] Failed to parse server manifest for settings lock: " + ex.Message);
            cachedManifestJson = null;
            cachedManifest = null;
            return null;
        }
    }

    private static string ResolveConfigMode(CompatibilityManifestDto manifest, string packageId, string fileName)
    {
        CompatibilityConfigRuleDto? best = null;
        foreach (CompatibilityConfigRuleDto rule in manifest.ModConfigRules)
        {
            if (!string.Equals(rule.PackageId?.Trim(), packageId?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(rule.FileName)
                && !string.Equals(rule.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (best is null
                || (!string.IsNullOrWhiteSpace(rule.FileName) && string.IsNullOrWhiteSpace(best.FileName)))
            {
                best = rule;
            }
        }

        return string.IsNullOrWhiteSpace(best?.Mode) ? "Enforce" : best!.Mode!;
    }

    public static void NotifyBlocked(string? modIdentifier = null, string? modHandleName = null)
    {
        if (Time.realtimeSinceStartup < nextNotificationAt)
        {
            return;
        }

        nextNotificationAt = Time.realtimeSinceStartup + 3f;
        Log.Warning(
            "[ClashOfRim][Compatibility] Blocked mod settings write while server config baseline is active: "
            + (modIdentifier ?? string.Empty)
            + "/"
            + (modHandleName ?? string.Empty));
        Messages.Message(ClashOfRimText.Key("ClashOfRim.Compatibility.ModSettingsLockedMessage"), MessageTypeDefOf.RejectInput, historical: false);
    }

    [DataContract]
    private sealed class CompatibilityManifestDto
    {
        [DataMember(Name = "manifestId")]
        public string ManifestId { get; set; } = string.Empty;

        [DataMember(Name = "modConfigRules")]
        public List<CompatibilityConfigRuleDto> ModConfigRules { get; set; } = new();

        [DataMember(Name = "mods")]
        public List<CompatibilityModDto> Mods { get; set; } = new();
    }

    [DataContract]
    private sealed class CompatibilityConfigRuleDto
    {
        [DataMember(Name = "packageId")]
        public string PackageId { get; set; } = string.Empty;

        [DataMember(Name = "fileName")]
        public string? FileName { get; set; }

        [DataMember(Name = "mode")]
        public string Mode { get; set; } = "Enforce";
    }

    [DataContract]
    private sealed class CompatibilityModDto
    {
        [DataMember(Name = "packageId")]
        public string PackageId { get; set; } = string.Empty;

        [DataMember(Name = "configs")]
        public List<CompatibilityConfigDto> Configs { get; set; } = new();
    }

    [DataContract]
    private sealed class CompatibilityConfigDto
    {
        [DataMember(Name = "fileName")]
        public string FileName { get; set; } = string.Empty;
    }
}

internal static class CompatibilityConfigOverlayPath
{
    private const string OverlayRootFolderName = "ClashOfRimConfigOverlays";
    private const string ActiveOverlayFileName = "active.json";
    private static ActiveOverlayDto? cachedActiveOverlay;
    private static bool activeOverlayLoaded;

    public static string Resolve(string packageId, string fileName)
    {
        return ResolveOverlayConfigPath(ResolveCurrentScope(), packageId, fileName);
    }

    public static bool TryResolveActiveConfigPath(string packageId, string fileName, out string path)
    {
        path = string.Empty;
        ActiveOverlayDto? active = ResolveActiveOverlay();
        if (active is null
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

    public static string? ActiveManifestJson => ResolveActiveOverlay()?.ManifestJson;

    public static void Activate(string manifestJson)
    {
        string scope = ResolveCurrentScope(manifestJson);
        var active = new ActiveOverlayDto
        {
            Scope = scope,
            ManifestJson = manifestJson ?? string.Empty,
            ServerBaseUrl = ResolveCurrentServerBaseUrl()
        };
        string path = ResolveActiveOverlayPath();
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var serializer = new DataContractJsonSerializer(typeof(ActiveOverlayDto));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        serializer.WriteObject(stream, active);
        cachedActiveOverlay = active;
        activeOverlayLoaded = true;
    }

    public static void Deactivate()
    {
        cachedActiveOverlay = null;
        activeOverlayLoaded = true;
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
        ClashOfRimMod? mod = LoadedModManager.GetMod<ClashOfRimMod>();
        string manifestJson = manifestJsonOverride ?? mod?.ServerCompatibilityManifestJson ?? string.Empty;
        string manifestId = TryReadManifestId(manifestJson) ?? string.Empty;
        string server = ResolveCurrentServerBaseUrl();
        ActiveOverlayDto? active = ResolveActiveOverlay();
        if (string.IsNullOrWhiteSpace(manifestId) && active is not null && IsActiveForCurrentServer(active))
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
        return (LoadedModManager.GetMod<ClashOfRimMod>()?.ServerBaseUrl ?? string.Empty).Trim().TrimEnd('/');
    }

    private static bool IsActiveForCurrentServer(ActiveOverlayDto active)
    {
        string current = ResolveCurrentServerBaseUrl();
        return !string.IsNullOrWhiteSpace(current)
            && !string.IsNullOrWhiteSpace(active.ServerBaseUrl)
            && string.Equals(current, active.ServerBaseUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
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
            var serializer = new DataContractJsonSerializer(typeof(OverlayManifestDto));
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson));
            OverlayManifestDto? manifest = serializer.ReadObject(stream) as OverlayManifestDto;
            return manifest?.Mods.Any(mod => string.Equals(mod.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
                && mod.Configs.Any(config => string.Equals(config.FileName, fileName, StringComparison.OrdinalIgnoreCase))) == true;
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
            var serializer = new DataContractJsonSerializer(typeof(ManifestIdDto));
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson));
            return (serializer.ReadObject(stream) as ManifestIdDto)?.ManifestId;
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
    private sealed class ManifestIdDto
    {
        [DataMember(Name = "manifestId")]
        public string ManifestId { get; set; } = string.Empty;
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
    }

    [DataContract]
    private sealed class OverlayManifestDto
    {
        [DataMember(Name = "mods")]
        public List<OverlayModDto> Mods { get; set; } = new();
    }

    [DataContract]
    private sealed class OverlayModDto
    {
        [DataMember(Name = "packageId")]
        public string PackageId { get; set; } = string.Empty;

        [DataMember(Name = "configs")]
        public List<OverlayConfigDto> Configs { get; set; } = new();
    }

    [DataContract]
    private sealed class OverlayConfigDto
    {
        [DataMember(Name = "fileName")]
        public string FileName { get; set; } = string.Empty;
    }
}
