using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.Compatibility;
using Verse;

namespace AIRsLight.ClashOfRim.CompatibilityClient;

internal static class CompatibilityBaselineApplicator
{
    public static bool TryApplyServerManifest(string manifestJson, out string message)
    {
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            message = T("ClashOfRim.Compatibility.ManifestMissing");
            return false;
        }

        CompatibilityManifestDto? manifest = ReadManifest(manifestJson);
        if (manifest is null)
        {
            message = T("ClashOfRim.Compatibility.ManifestParseFailed");
            return false;
        }

        ApplyConfigOverlays(manifest, manifestJson);
        CompatibilityConfigOverlayPath.Activate(manifestJson);
        ApplyModList(manifest);
        ModsConfig.Save();
        message = T("ClashOfRim.Compatibility.ApplyRestarting");
        GenCommandLine.Restart();
        return true;
    }

    public static bool TryApplyServerConfigOverlay(string manifestJson, out string message)
    {
        message = string.Empty;
        CompatibilityManifestDto? manifest = ReadManifest(manifestJson);
        if (manifest is null)
        {
            message = string.IsNullOrWhiteSpace(manifestJson)
                ? T("ClashOfRim.Compatibility.ManifestMissing")
                : T("ClashOfRim.Compatibility.ManifestParseFailed");
            return false;
        }

        ApplyConfigOverlays(manifest, manifestJson);
        CompatibilityConfigOverlayPath.Activate(manifestJson);
        message = T("ClashOfRim.Compatibility.ConfigApplyOverlayRestarting");
        GenCommandLine.Restart();
        return true;
    }

    public static bool TryOverwriteLocalConfigs(string manifestJson, out string message)
    {
        message = string.Empty;
        CompatibilityManifestDto? manifest = ReadManifest(manifestJson);
        if (manifest is null)
        {
            message = string.IsNullOrWhiteSpace(manifestJson)
                ? T("ClashOfRim.Compatibility.ManifestMissing")
                : T("ClashOfRim.Compatibility.ManifestParseFailed");
            return false;
        }

        ApplyConfigFiles(manifest, ResolveLocalConfigPath);
        CompatibilityConfigOverlayPath.Deactivate();
        message = T("ClashOfRim.Compatibility.ConfigOverwriteLocalRestarting");
        GenCommandLine.Restart();
        return true;
    }

    private static string T(string key)
    {
        return ClashOfRimText.Key(key);
    }

    private static CompatibilityManifestDto? ReadManifest(string json)
    {
        try
        {
            var serializer = new DataContractJsonSerializer(typeof(CompatibilityManifestDto));
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var manifest = serializer.ReadObject(stream) as CompatibilityManifestDto;
            ValidateManifest(manifest);
            return manifest;
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][Compatibility] Failed to parse server manifest: " + ex);
            return null;
        }
    }

    // Validate the entire input before any file, mod order or next-boot activation is changed.
    private static void ValidateManifest(CompatibilityManifestDto? manifest)
    {
        if (manifest?.Mods is null || manifest.Mods.Count == 0)
            throw new SerializationException("The server baseline has no mod list.");
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in manifest.Mods)
        {
            if (mod is null || string.IsNullOrWhiteSpace(mod.PackageId) || !packages.Add(mod.PackageId)
                || mod.Configs is null)
                throw new SerializationException("The server baseline contains an invalid or duplicate mod.");
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var config in mod.Configs)
            {
                if (config is null || string.IsNullOrWhiteSpace(config.FileName) || !files.Add(config.FileName))
                    throw new SerializationException("The server baseline contains an invalid or duplicate config.");
                if (!config.HasSavedFile)
                {
                    if (!string.IsNullOrWhiteSpace(config.CanonicalXml))
                        throw new SerializationException("An absent config cannot contain saved XML.");
                    continue;
                }
                using var reader = XmlReader.Create(new StringReader(config.CanonicalXml ?? string.Empty),
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                _ = XDocument.Load(reader);
                if (!string.IsNullOrWhiteSpace(config.Sha256) && !string.Equals(config.Sha256,
                    ModConfigXmlCanonicalizer.Sha256(config.CanonicalXml!), StringComparison.OrdinalIgnoreCase))
                    throw new SerializationException("The server config does not match its declared hash.");
            }
        }
    }

    private static void ApplyModList(CompatibilityManifestDto manifest)
    {
        List<string> activeMods = manifest.Mods
            .OrderBy(mod => mod.LoadOrder)
            .Select(ToLocalActiveModId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();
        object? data = typeof(ModsConfig)
            .GetField("data", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null);
        FieldInfo? activeModsField = data?.GetType()
            .GetField("activeMods", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        activeModsField?.SetValue(data, activeMods);
    }

    private static void ApplyConfigOverlays(CompatibilityManifestDto manifest, string manifestJson)
    {
        ApplyConfigFiles(manifest, (packageId, fileName) => CompatibilityConfigOverlayPath.Resolve(packageId, fileName, manifestJson));
    }

    private static void ApplyConfigFiles(
        CompatibilityManifestDto manifest,
        Func<string, string, string> pathResolver)
    {
        foreach (CompatibilityModDto mod in manifest.Mods)
        {
            if (mod.Configs.Count == 0)
            {
                continue;
            }

            foreach (CompatibilityConfigDto config in mod.Configs)
            {
                if (string.IsNullOrWhiteSpace(config.FileName))
                {
                    continue;
                }

                string path = pathResolver(mod.PackageId, config.FileName);
                if (!config.HasSavedFile || string.IsNullOrWhiteSpace(config.CanonicalXml))
                {
                    CompatibilityConfigOverlayPath.Delete(path);
                    continue;
                }

                CompatibilityConfigOverlayPath.EnsureDirectoryFor(path);

                File.WriteAllText(path, config.CanonicalXml);
            }
        }
    }

    private static string ResolveLocalConfigPath(string packageId, string fileName)
    {
        ModContentPack? mod = LoadedModManager.RunningModsListForReading.FirstOrDefault(candidate =>
            string.Equals(candidate.PackageIdPlayerFacing, packageId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
        string identifier = string.IsNullOrWhiteSpace(mod?.FolderName) ? packageId : mod!.FolderName;
        string file = GenText.SanitizeFilename($"Mod_{identifier}_{fileName}.xml");
        return Path.Combine(GenFilePaths.ConfigFolderPath, file);
    }

    private static string ToLocalActiveModId(CompatibilityModDto serverMod)
    {
        ModMetaData? installed = FindInstalledMod(serverMod.PackageId);
        if (installed is not null)
        {
            return installed.PackageId;
        }

        return serverMod.PackageId.StartsWith("ludeon.", StringComparison.OrdinalIgnoreCase)
            ? serverMod.PackageId
            : serverMod.PackageId + ModMetaData.SteamModPostfix;
    }

    private static ModMetaData? FindInstalledMod(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        return ModLister.GetModWithIdentifier(packageId)
            ?? ModLister.GetModWithIdentifier(packageId + ModMetaData.SteamModPostfix);
    }

    [DataContract]
    private sealed class CompatibilityManifestDto
    {
        [DataMember(Name = "mods", IsRequired = true)]
        public List<CompatibilityModDto> Mods { get; set; } = new();
    }

    [DataContract]
    private sealed class CompatibilityModDto
    {
        [DataMember(Name = "loadOrder")]
        public int LoadOrder { get; set; }

        [DataMember(Name = "packageId")]
        public string PackageId { get; set; } = string.Empty;

        [DataMember(Name = "configs", IsRequired = true)]
        public List<CompatibilityConfigDto> Configs { get; set; } = new();
    }

    [DataContract]
    private sealed class CompatibilityConfigDto
    {
        [DataMember(Name = "fileName")]
        public string FileName { get; set; } = string.Empty;

        [DataMember(Name = "hasSavedFile", IsRequired = true)]
        public bool HasSavedFile { get; set; } = true;

        [DataMember(Name = "canonicalXml")]
        public string CanonicalXml { get; set; } = string.Empty;

        [DataMember(Name = "sha256")]
        public string? Sha256 { get; set; }
    }
}
