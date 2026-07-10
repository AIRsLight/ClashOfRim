using AIRsLight.ClashOfRim.Compatibility;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Verse;

namespace AIRsLight.ClashOfRim.CompatibilityClient;

internal static class ClientCompatibilityManifestBuilder
{
    private const string FileHashCacheFileName = "ClashOfRimCompatibilityFileHashes.cache";
    private static readonly object CacheLock = new();
    private static readonly object FileHashCacheLock = new();
    private static CompatibilityManifest? cachedManifest;
    private static Dictionary<string, CachedFileHash>? cachedFileHashes;
    private static bool fileHashCacheDirty;

    private static readonly HashSet<Type> IgnoredVanillaDefTypes = new()
    {
        typeof(FeatureDef), typeof(HairDef),
        typeof(MainButtonDef), typeof(PawnTableDef),
        typeof(TransferableSorterDef), typeof(ConceptDef),
        typeof(InstructionDef), typeof(EffecterDef),
        typeof(KeyBindingCategoryDef),
        typeof(KeyBindingDef), typeof(RulePackDef),
        typeof(ScatterableDef), typeof(ShaderTypeDef),
        typeof(SongDef), typeof(SoundDef),
        typeof(SubcameraDef), typeof(PawnColumnDef)
    };

    public static CompatibilityManifest Build()
    {
        lock (CacheLock)
        {
            string currentLanguage = ResolveCurrentRimWorldLanguage() ?? string.Empty;
            if (cachedManifest is null
                || !string.Equals(cachedManifest.GameLanguage, currentLanguage, StringComparison.Ordinal))
            {
                cachedManifest = BuildStaticManifest();
            }

            return RefreshConfigs(cachedManifest);
        }
    }

    private static CompatibilityManifest BuildStaticManifest()
    {
        List<ModContentPack> runningMods = LoadedModManager.RunningModsListForReading.ToList();
        List<ModManifestEntry> mods = new();
        for (int i = 0; i < runningMods.Count; i++)
        {
            mods.Add(BuildModEntry(runningMods[i], i));
        }

        IReadOnlyList<DefSummary> defSummaries = BuildDefSummaries();
        string configSha = BuildConfigSha(mods);

        var manifest = new CompatibilityManifest
        {
            ProtocolVersion = ClashOfRimVersion.ProtocolVersion,
            RimWorldVersion = VersionControl.CurrentVersionString,
            GameLanguage = ResolveCurrentRimWorldLanguage() ?? string.Empty,
            DlcIds = runningMods
                .Where(mod => (mod.PackageIdPlayerFacing ?? string.Empty).StartsWith("ludeon.", StringComparison.OrdinalIgnoreCase))
                .Select(mod => mod.PackageIdPlayerFacing)
                .ToList(),
            ConfigVersion = configSha,
            ConfigSha256 = configSha,
            Mods = mods,
            DefSummaries = defSummaries
        };

        string stableBody = string.Join("\n", mods.Select(mod => mod.PackageId + "@" + mod.LoadOrder))
            + "\n" + manifest.GameLanguage
            + "\n" + string.Join("\n", mods.SelectMany(mod => mod.Files.Select(file => mod.PackageId + "/" + file.RelativePath + "=" + file.Sha256)))
            + "\n" + configSha
            + "\n" + string.Join("\n", defSummaries.Select(def => def.Name + ":" + def.Count + ":" + def.Hash));

        CompatibilityManifest result = manifest with { ManifestId = HashText(stableBody) };
        SaveFileHashCacheIfDirty();
        return result;
    }

    private static CompatibilityManifest RefreshConfigs(CompatibilityManifest manifest)
    {
        List<ModContentPack> runningMods = LoadedModManager.RunningModsListForReading.ToList();
        var modsByPackageId = manifest.Mods.ToDictionary(mod => mod.PackageId, StringComparer.OrdinalIgnoreCase);
        List<ModManifestEntry> refreshedMods = new();
        for (int i = 0; i < runningMods.Count; i++)
        {
            ModContentPack runningMod = runningMods[i];
            string packageId = runningMod.PackageIdPlayerFacing ?? runningMod.PackageId ?? string.Empty;
            if (!modsByPackageId.TryGetValue(packageId, out ModManifestEntry? cachedMod))
            {
                return BuildStaticManifest();
            }

            refreshedMods.Add(cachedMod with
            {
                LoadOrder = i,
                Configs = BuildConfigs(runningMod)
                    .OrderBy(config => config.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            });
        }

        string configSha = BuildConfigSha(refreshedMods);
        string stableBody = string.Join("\n", refreshedMods.Select(mod => mod.PackageId + "@" + mod.LoadOrder))
            + "\n" + manifest.GameLanguage
            + "\n" + string.Join("\n", refreshedMods.SelectMany(mod => mod.Files.Select(file => mod.PackageId + "/" + file.RelativePath + "=" + file.Sha256)))
            + "\n" + configSha
            + "\n" + string.Join("\n", manifest.DefSummaries.Select(def => def.Name + ":" + def.Count + ":" + def.Hash));

        return manifest with
        {
            ConfigVersion = configSha,
            ConfigSha256 = configSha,
            Mods = refreshedMods,
            ManifestId = HashText(stableBody)
        };
    }

    private static string BuildConfigSha(IEnumerable<ModManifestEntry> mods)
    {
        return HashText(string.Join("\n", mods
            .SelectMany(mod => mod.Configs.Select(config => mod.PackageId + "/" + config.FileName + "=" + config.HasSavedFile + "=" + config.Sha256))
            .OrderBy(value => value, StringComparer.Ordinal)));
    }

    public static string BuildJson()
    {
        return CompatibilityManifestJsonWriter.Write(Build());
    }

    public static string BuildSummaryJson()
    {
        return CompatibilityManifestJsonWriter.WriteSummary(BuildSummary(Build()));
    }

    public static string BuildJsonForPackages(IReadOnlyCollection<string>? packageIds)
    {
        CompatibilityManifest manifest = Build();
        if (packageIds is null || packageIds.Count == 0 || packageIds.Contains("*"))
        {
            return CompatibilityManifestJsonWriter.Write(manifest);
        }

        var requested = new HashSet<string>(packageIds, StringComparer.OrdinalIgnoreCase);
        return CompatibilityManifestJsonWriter.Write(manifest with
        {
            Mods = manifest.Mods
                .Where(mod => requested.Contains(mod.PackageId))
                .ToList()
        });
    }

    private static CompatibilityManifestSummary BuildSummary(CompatibilityManifest manifest)
    {
        return new CompatibilityManifestSummary
        {
            SchemaVersion = manifest.SchemaVersion,
            ManifestId = manifest.ManifestId,
            ProtocolVersion = manifest.ProtocolVersion,
            RimWorldVersion = manifest.RimWorldVersion,
            GameLanguage = manifest.GameLanguage,
            DlcIds = manifest.DlcIds,
            ConfigSha256 = manifest.ConfigSha256,
            Mods = manifest.Mods
                .Select(mod => new ModManifestSummaryEntry
                {
                    LoadOrder = mod.LoadOrder,
                    PackageId = mod.PackageId,
                    Name = mod.Name,
                    Role = mod.Role,
                    Hash = ComputeModHash(mod)
                })
                .ToList(),
            DefSummaries = manifest.DefSummaries
        };
    }

    private static ModManifestEntry BuildModEntry(ModContentPack mod, int loadOrder)
    {
        List<ControlledFileEntry> files = EnumerateControlledFiles(mod)
            .Select(file => BuildFileEntry(mod, file))
            .Where(entry => entry is not null)
            .Cast<ControlledFileEntry>()
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<ModConfigDigest> configs = BuildConfigs(mod)
            .OrderBy(config => config.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        WorkshopMetadata workshop = ReadWorkshopMetadata(mod.ModMetaData);
        return new ModManifestEntry
        {
            LoadOrder = loadOrder,
            PackageId = mod.PackageIdPlayerFacing ?? mod.PackageId ?? string.Empty,
            Name = mod.Name ?? string.Empty,
            Source = mod.ModMetaData?.Source.ToString() ?? string.Empty,
            WorkshopId = workshop.WorkshopId,
            WorkshopItemState = workshop.ItemState,
            WorkshopLocalInstalledAtUnix = workshop.LocalInstalledAtUnix,
            Role = ModFileClassifier.IsPureTranslationMod(new ModManifestEntry { Files = files })
                ? ModCompatibilityRole.OptionalPureTranslation
                : ModCompatibilityRole.Required,
            Files = files,
            Configs = configs
        };
    }

    private static IEnumerable<FileInfo> EnumerateControlledFiles(ModContentPack mod)
    {
        foreach (FileInfo file in GetFiles(mod, "Assemblies/", extension => extension == ".dll"))
        {
            yield return file;
        }

        foreach (FileInfo file in GetFiles(mod, "Defs/", extension => extension == ".xml"))
        {
            yield return file;
        }

        foreach (FileInfo file in GetFiles(mod, "Patches/", extension => extension == ".xml"))
        {
            yield return file;
        }

        foreach (FileInfo file in GetFiles(mod, "Languages/", _ => true))
        {
            yield return file;
        }

        foreach (FileInfo file in GetFiles(mod, "About/", _ => true))
        {
            yield return file;
        }
    }

    private static IEnumerable<FileInfo> GetFiles(
        ModContentPack mod,
        string folder,
        Func<string, bool> extensionPredicate)
    {
        IEnumerable<Tuple<string, FileInfo>> files;
        try
        {
            files = ModContentPack.GetAllFilesForModPreserveOrder(
                mod,
                folder,
                extension => extensionPredicate((extension ?? string.Empty).ToLowerInvariant()));
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][Compatibility] Failed to enumerate " + folder + " for " + mod.Name + ": " + ex.Message);
            yield break;
        }

        foreach (Tuple<string, FileInfo> tuple in files)
        {
            yield return tuple.Item2;
        }
    }

    private static ControlledFileEntry? BuildFileEntry(ModContentPack mod, FileInfo file)
    {
        try
        {
            if (!file.Exists)
            {
                return null;
            }

            string relativePath = RelativePath(GetRootPath(mod), file.FullName);
            return new ControlledFileEntry
            {
                RelativePath = relativePath,
                Size = file.Length,
                Sha256 = HashFile(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks),
                LastWriteUtcUnix = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds(),
                Kind = ModFileClassifier.Classify(relativePath)
            };
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][Compatibility] Failed to hash " + file.FullName + ": " + ex.Message);
            return null;
        }
    }

    private static IEnumerable<ModConfigDigest> BuildConfigs(ModContentPack mod)
    {
        if (IsClashOfRimMod(mod))
        {
            return Array.Empty<ModConfigDigest>();
        }

        var configs = new Dictionary<string, ModConfigDigest>(StringComparer.OrdinalIgnoreCase);
        foreach (Mod modInstance in LoadedModManager.ModHandles)
        {
            if (!ReferenceEquals(modInstance.Content, mod))
            {
                continue;
            }

            if (!HasSettingsRegistration(modInstance))
            {
                continue;
            }

            string instanceName = modInstance.GetType().Name;
            string path = ResolveSettingsFileName(modInstance.Content.FolderName, instanceName);
            ModConfigDigest? digest = BuildConfigDigest(instanceName, path);
            if (digest is not null)
            {
                configs[digest.FileName] = digest;
            }
        }

        return configs.Values;
    }

    private static bool IsClashOfRimMod(ModContentPack mod)
    {
        if (mod is null)
        {
            return false;
        }

        return string.Equals(mod.PackageIdPlayerFacing, ClashOfRimMod.PackageId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mod.PackageId, ClashOfRimMod.PackageId, StringComparison.OrdinalIgnoreCase)
            || mod.assemblies?.loadedAssemblies?.Contains(typeof(ClashOfRimMod).Assembly) == true;
    }

    private static ModConfigDigest? BuildConfigDigest(string modHandleName, string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(modHandleName))
            {
                return null;
            }

            if (!File.Exists(path))
            {
                return new ModConfigDigest
                {
                    FileName = modHandleName,
                    HasSavedFile = false
                };
            }

            string canonical = ModConfigXmlCanonicalizer.Canonicalize(File.ReadAllText(path));
            return new ModConfigDigest
            {
                FileName = modHandleName,
                HasSavedFile = true,
                CanonicalXml = canonical,
                Sha256 = ModConfigXmlCanonicalizer.Sha256(canonical)
            };
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][Compatibility] Failed to read config " + path + ": " + ex.Message);
            return null;
        }
    }

    private static bool HasSettingsRegistration(Mod modInstance)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(modInstance.SettingsCategory());
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][Compatibility] Failed to inspect settings registration for "
                + modInstance.Content?.Name + ": " + ex.Message);
            return false;
        }
    }

    private static IReadOnlyList<DefSummary> BuildDefSummaries()
    {
        var summaries = new List<DefSummary>();
        foreach (Type defType in GenTypes.AllLeafSubclasses(typeof(Def)))
        {
            if (defType.Assembly != typeof(Game).Assembly || IgnoredVanillaDefTypes.Contains(defType))
            {
                continue;
            }

            IEnumerable<Def> defs = GenDefDatabase.GetAllDefsInDatabaseForDef(defType);
            summaries.Add(new DefSummary
            {
                Name = defType.Name,
                Count = defs.Count(),
                Hash = AggregateHash(defs.Select(def => GenText.StableStringHash(def.defName)).OrderBy(hash => hash))
            });
        }

        return summaries.OrderBy(summary => summary.Name, StringComparer.Ordinal).ToList();
    }

    private static int AggregateHash(IEnumerable<int> values)
    {
        unchecked
        {
            int hash = 17;
            foreach (int value in values)
            {
                hash = (hash * 397) ^ value;
            }

            return hash;
        }
    }

    private static string RelativePath(string root, string path)
    {
        string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string relative = path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            ? path.Substring(normalizedRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : Path.GetFileName(path);
        return relative.Replace('\\', '/');
    }

    private static string GetRootPath(ModContentPack mod)
    {
        return mod.RootDir ?? string.Empty;
    }

    private static string ResolveSettingsFileName(string modIdentifier, string modHandleName)
    {
        MethodInfo? method = typeof(LoadedModManager).GetMethod(
            "GetSettingsFilename",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (method is not null)
        {
            object? value = method.Invoke(null, new object[] { modIdentifier, modHandleName });
            if (value is string path && !string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        string configFolder = ResolveConfigFolder();
        return Path.Combine(configFolder, "Mod_" + modIdentifier + "_" + modHandleName + ".xml");
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

    private static string ReadWorkshopId(ModMetaData? metadata)
    {
        if (metadata is null)
        {
            return string.Empty;
        }

        try
        {
            object? value = metadata.GetType()
                .GetMethod("GetPublishedFileId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.Invoke(metadata, Array.Empty<object>());
            return value?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static WorkshopMetadata ReadWorkshopMetadata(ModMetaData? metadata)
    {
        string workshopId = ReadWorkshopId(metadata);
        if (string.IsNullOrWhiteSpace(workshopId) || !ulong.TryParse(workshopId, out ulong rawId) || rawId == 0UL)
        {
            return new WorkshopMetadata(workshopId, string.Empty, 0L);
        }

        try
        {
            Type? steamManagerType = FindLoadedType("Verse.Steam.SteamManager");
            object? initializedValue = steamManagerType?.GetProperty("Initialized", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            bool initialized = initializedValue is bool value && value;
            if (!initialized)
            {
                return new WorkshopMetadata(workshopId, string.Empty, 0L);
            }

            Type? steamUgcType = FindLoadedType("Steamworks.SteamUGC");
            Type? publishedFileIdType = FindLoadedType("Steamworks.PublishedFileId_t");
            if (steamUgcType is null || publishedFileIdType is null)
            {
                return new WorkshopMetadata(workshopId, string.Empty, 0L);
            }

            object? publishedFileId = Activator.CreateInstance(publishedFileIdType, rawId);
            if (publishedFileId is null)
            {
                return new WorkshopMetadata(workshopId, string.Empty, 0L);
            }

            string itemState = ReadWorkshopItemState(steamUgcType, publishedFileIdType, publishedFileId);
            long localInstalledAtUnix = ReadWorkshopLocalInstalledAtUnix(steamUgcType, publishedFileIdType, publishedFileId);
            return new WorkshopMetadata(workshopId, itemState, localInstalledAtUnix);
        }
        catch
        {
            return new WorkshopMetadata(workshopId, string.Empty, 0L);
        }
    }

    private static string ReadWorkshopItemState(Type steamUgcType, Type publishedFileIdType, object publishedFileId)
    {
        try
        {
            MethodInfo? method = steamUgcType.GetMethod("GetItemState", BindingFlags.Static | BindingFlags.Public, null, new[] { publishedFileIdType }, null);
            object? value = method?.Invoke(null, new[] { publishedFileId });
            if (value is uint flags)
            {
                return FormatWorkshopItemState(flags);
            }

            return value?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static long ReadWorkshopLocalInstalledAtUnix(Type steamUgcType, Type publishedFileIdType, object publishedFileId)
    {
        try
        {
            MethodInfo? method = steamUgcType.GetMethod("GetItemInstallInfo", BindingFlags.Static | BindingFlags.Public);
            if (method is null)
            {
                return 0L;
            }

            object[] args = { publishedFileId, 0UL, string.Empty, 1024U, 0U };
            object? result = method.Invoke(null, args);
            bool ok = result is bool value && value;
            if (!ok)
            {
                return 0L;
            }

            return args[4] switch
            {
                uint timestamp => timestamp,
                int timestamp => timestamp,
                long timestamp => timestamp,
                _ => 0L
            };
        }
        catch
        {
            return 0L;
        }
    }

    private static Type? FindLoadedType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, throwOnError: false);
            if (type is not null)
            {
                return type;
            }
        }

        return null;
    }

    private static string FormatWorkshopItemState(uint flags)
    {
        if (flags == 0U)
        {
            return "None";
        }

        var names = new List<string>();
        if ((flags & 1U) != 0U)
        {
            names.Add("Subscribed");
        }

        if ((flags & 2U) != 0U)
        {
            names.Add("LegacyItem");
        }

        if ((flags & 4U) != 0U)
        {
            names.Add("Installed");
        }

        if ((flags & 8U) != 0U)
        {
            names.Add("NeedsUpdate");
        }

        if ((flags & 16U) != 0U)
        {
            names.Add("Downloading");
        }

        if ((flags & 32U) != 0U)
        {
            names.Add("DownloadPending");
        }

        return names.Count == 0 ? flags.ToString() : string.Join(",", names);
    }

    private static string HashFile(string path, long length, long lastWriteUtcTicks)
    {
        string normalizedPath = Path.GetFullPath(path);
        lock (FileHashCacheLock)
        {
            EnsureFileHashCacheLoaded();
            if (cachedFileHashes!.TryGetValue(normalizedPath, out CachedFileHash? cached)
                && cached.Length == length
                && cached.LastWriteUtcTicks == lastWriteUtcTicks
                && !string.IsNullOrWhiteSpace(cached.Sha256))
            {
                return cached.Sha256;
            }
        }

        string sha256 = HashFile(path);
        lock (FileHashCacheLock)
        {
            EnsureFileHashCacheLoaded();
            cachedFileHashes![normalizedPath] = new CachedFileHash(length, lastWriteUtcTicks, sha256);
            fileHashCacheDirty = true;
        }

        return sha256;
    }

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using SHA256 sha = SHA256.Create();
        return ToHex(sha.ComputeHash(stream));
    }

    private static void EnsureFileHashCacheLoaded()
    {
        if (cachedFileHashes is not null)
        {
            return;
        }

        cachedFileHashes = new Dictionary<string, CachedFileHash>(StringComparer.OrdinalIgnoreCase);
        string path = ResolveFileHashCachePath();
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            foreach (string line in File.ReadAllLines(path))
            {
                string[] parts = line.Split('\t');
                if (parts.Length != 4
                    || !long.TryParse(parts[1], out long length)
                    || !long.TryParse(parts[2], out long lastWriteUtcTicks)
                    || string.IsNullOrWhiteSpace(parts[3]))
                {
                    continue;
                }

                string cachedPath = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
                cachedFileHashes[cachedPath] = new CachedFileHash(length, lastWriteUtcTicks, parts[3]);
            }
        }
        catch (Exception ex)
        {
            cachedFileHashes.Clear();
            Log.Warning("[ClashOfRim][Compatibility] Failed to read file hash cache: " + ex.Message);
        }
    }

    private static void SaveFileHashCacheIfDirty()
    {
        lock (FileHashCacheLock)
        {
            if (!fileHashCacheDirty || cachedFileHashes is null)
            {
                return;
            }

            try
            {
                string path = ResolveFileHashCachePath();
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllLines(
                    path,
                    cachedFileHashes
                        .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(entry =>
                            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(entry.Key))
                            + "\t" + entry.Value.Length
                            + "\t" + entry.Value.LastWriteUtcTicks
                            + "\t" + entry.Value.Sha256));
                fileHashCacheDirty = false;
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][Compatibility] Failed to write file hash cache: " + ex.Message);
            }
        }
    }

    private static string ResolveFileHashCachePath()
    {
        return Path.Combine(ResolveConfigFolder(), FileHashCacheFileName);
    }

    private static string HashText(string text)
    {
        using SHA256 sha = SHA256.Create();
        return ToHex(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text ?? string.Empty)));
    }

    private static string ComputeModHash(ModManifestEntry mod)
    {
        string stableBody = string.Join("\n", new[]
            {
                mod.LoadOrder.ToString(),
                mod.PackageId,
                mod.Name,
                mod.Source,
                mod.WorkshopId,
                mod.Role.ToString()
            })
            + "\n" + string.Join("\n", mod.Files
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(file => file.RelativePath + "|" + file.Size + "|" + file.Sha256 + "|" + file.Kind))
            + "\n" + string.Join("\n", mod.Configs
                .OrderBy(config => config.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(config => config.FileName + "|" + config.HasSavedFile + "|" + config.Sha256));
        return HashText(stableBody);
    }

    private static string? ResolveCurrentRimWorldLanguage()
    {
        try
        {
            return LanguageDatabase.activeLanguage?.folderName
                ?? Prefs.LangFolderName;
        }
        catch
        {
            return null;
        }
    }

    private static string ToHex(byte[] bytes)
    {
        char[] chars = new char[bytes.Length * 2];
        const string hex = "0123456789abcdef";
        for (int i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = hex[bytes[i] >> 4];
            chars[i * 2 + 1] = hex[bytes[i] & 0x0F];
        }

        return new string(chars);
    }

    private readonly struct WorkshopMetadata
    {
        public WorkshopMetadata(string workshopId, string itemState, long localInstalledAtUnix)
        {
            WorkshopId = workshopId;
            ItemState = itemState;
            LocalInstalledAtUnix = localInstalledAtUnix;
        }

        public string WorkshopId { get; }
        public string ItemState { get; }
        public long LocalInstalledAtUnix { get; }
    }

    private sealed class CachedFileHash
    {
        public CachedFileHash(long length, long lastWriteUtcTicks, string sha256)
        {
            Length = length;
            LastWriteUtcTicks = lastWriteUtcTicks;
            Sha256 = sha256;
        }

        public long Length { get; }

        public long LastWriteUtcTicks { get; }

        public string Sha256 { get; }
    }
}
