using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.CompatibilityClient;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Compatibility;

internal interface IClientCompatibilityReportText
{
    string Text(string key, params (string Name, object? Value)[] args);
    void Diagnose(string message);
}

// Immutable diagnostic projection. Wire manifests and comparison algorithms never
// escape into the window or decide whether a session may execute a recovery action.
internal sealed class ClientCompatibilityReport
{
    private readonly IClientCompatibilityReportText text;
    private readonly UiManifest? serverManifest, localManifest;
    internal string ManifestJson { get; }
    internal string ServerId => serverManifest?.ManifestId ?? "";
    internal string LocalId => localManifest?.ManifestId ?? "";
    internal string ServerVersion => serverManifest?.RimWorldVersion ?? "";
    internal string LocalVersion => localManifest?.RimWorldVersion ?? "";
    internal string ServerProtocol { get; }
    internal string LocalProtocol => localManifest?.ProtocolVersion ?? ClashOfRimVersion.ProtocolVersion;
    internal string ServerLanguage { get; }
    internal string LocalLanguage => localManifest?.GameLanguage ?? "";
    internal bool LanguageMismatch { get; }
    internal bool ProtocolMismatch { get; }
    internal bool LanguageOnly { get; }
    internal bool CanOverride { get; }
    internal bool OnlyFileMismatch { get; }
    internal bool CanApplyManifest { get; }
    internal bool CanApplyConfig { get; }
    internal IReadOnlyList<UiDiffEntry> ManifestEntries { get; }
    internal IReadOnlyList<UiDiffEntry> HashEntries { get; }
    internal IReadOnlyList<UiDiffEntry> ConfigEntries { get; }
    internal IReadOnlyList<ModRow> ServerMods { get; }
    internal IReadOnlyList<ModRow> LocalMods { get; }
    internal IReadOnlyList<string>? ServerModOrder => serverManifest?.Mods.OrderBy(mod => mod.LoadOrder).Select(mod => mod.PackageId).ToArray();

    internal ClientCompatibilityReport(ModLoginResponseDto response, string? localJson, string? language, IClientCompatibilityReportText text)
    {
        this.text = text;
        var owned = ClientProtocolJson.Copy(response);
        ManifestJson = owned.ServerCompatibilityManifestJson ?? "";
        serverManifest = Read(ManifestJson); localManifest = Read(localJson);
        ManifestEntries = BuildManifestEntries(serverManifest, localManifest, owned.CompatibilityIssues).AsReadOnly();
        HashEntries = BuildHashEntries(serverManifest, localManifest, owned.CompatibilityIssues).AsReadOnly();
        ConfigEntries = BuildConfigEntries(serverManifest, localManifest, owned.CompatibilityIssues).AsReadOnly();
        ServerMods = Rows(serverManifest, localManifest, true);
        LocalMods = Rows(localManifest, serverManifest, false);
        ServerProtocol = serverManifest?.ProtocolVersion ?? owned.ServerProtocolVersion ?? "";
        ServerLanguage = CompatibilityLanguageMismatchPolicy.ResolveServerLanguage(language, serverManifest?.GameLanguage);
        LanguageMismatch = owned.CompatibilityIssues?.Any(issue => CompatibilityLanguageMismatchPolicy.IsLanguageMismatch(issue.Code)) == true;
        ProtocolMismatch = owned.CompatibilityIssues?.Any(issue => issue.Code == nameof(CompatibilityIssueCode.ProtocolVersionMismatch)) == true
            || (!string.IsNullOrWhiteSpace(ServerProtocol) && ServerProtocol != LocalProtocol);
        LanguageOnly = CompatibilityLanguageMismatchPolicy.CanContinue(owned);
        CanOverride = owned.CanOverrideCompatibilityBaseline;
        OnlyFileMismatch = !ProtocolMismatch && ManifestEntries.Count == 0 && ConfigEntries.Count == 0 && HashEntries.Count > 0;
        CanApplyManifest = ManifestEntries.Count > 0 && !OnlyFileMismatch;
        CanApplyConfig = ConfigEntries.Count > 0 && serverManifest is not null;
    }
    private UiManifest? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var manifest = ClientProtocolJson.Deserialize<UiManifest>(json!, static type => type);
            manifest.Mods ??= new(); manifest.DlcIds ??= new(); manifest.DefSummaries ??= new();
            // Validate identities once at the wire boundary; dictionaries below share
            // these exact keys. Ambiguous manifests fall back to server issue details.
            manifest.ModsById = manifest.Mods.ToDictionary(mod => NormalizeId(mod.PackageId));
            manifest.DefsByName = manifest.DefSummaries.ToDictionary(def => def.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var mod in manifest.Mods)
            {
                mod.Files ??= new(); mod.Configs ??= new();
                mod.FilesByPath = mod.Files.ToDictionary(file => NormalizePath(file.RelativePath));
                mod.ConfigsByName = mod.Configs.ToDictionary(config => config.FileName, StringComparer.OrdinalIgnoreCase);
            }
            return manifest;
        }
        catch (Exception ex) { text.Diagnose("Failed to parse compatibility report manifest: " + ex); return null; }
    }
    private IReadOnlyList<ModRow> Rows(UiManifest? manifest, UiManifest? other, bool serverSide)
    {
        if (manifest is null) return Array.Empty<ModRow>();
        return manifest.Mods.Select((mod, index) =>
        {
            int? otherIndex = other?.Mods.FindIndex(item => NormalizeId(item.PackageId) == NormalizeId(mod.PackageId));
            var status = otherIndex is null || otherIndex == index ? UiDiffStatus.Match
                : otherIndex < 0 ? serverSide ? UiDiffStatus.Missing : UiDiffStatus.Added : UiDiffStatus.Modified;
            return new ModRow(mod.PackageId, DisplayModName(mod), status);
        }).ToList().AsReadOnly();
    }
    internal string? WorkshopId(UiDiffEntry entry)
    {
        if (entry.Status != UiDiffStatus.Missing || string.IsNullOrWhiteSpace(entry.Subject)) return null;
        var mod = serverManifest?.Mods.FirstOrDefault(item => NormalizeId(item.PackageId) == NormalizeId(entry.Subject));
        return string.IsNullOrWhiteSpace(mod?.WorkshopId) ? null : mod!.WorkshopId.Trim();
    }
    private string T(string key, params (string Name, object? Value)[] args) => text.Text(key, args);

    private List<UiDiffEntry> BuildManifestEntries(
        UiManifest? server,
        UiManifest? local,
        IReadOnlyList<ModCompatibilityIssueDto>? fallbackIssues)
    {
        var entries = new List<UiDiffEntry>();
        if (server is null || local is null)
        {
            entries.AddRange(FallbackEntries(fallbackIssues, CompatibilityTab.Manifest));
            return entries;
        }

        AddScalar(entries, T("ClashOfRim.Compatibility.SchemaVersion"), server.SchemaVersion.ToString(), local.SchemaVersion.ToString());
        AddScalar(entries, T("ClashOfRim.Compatibility.RimWorldVersion"), server.RimWorldVersion, local.RimWorldVersion);
        AddSequence(entries, T("ClashOfRim.Compatibility.DlcList"), server.DlcIds, local.DlcIds);

        Dictionary<string, UiMod> localById = local.ModsById;
        Dictionary<string, UiMod> serverById = server.ModsById;
        foreach (UiMod serverMod in server.Mods)
        {
            if (!localById.ContainsKey(NormalizeId(serverMod.PackageId)))
            {
                entries.Add(new UiDiffEntry(UiDiffStatus.Missing, T("ClashOfRim.Compatibility.MissingRequiredMod"), DisplayModName(serverMod), serverMod.PackageId));
            }
        }

        foreach (UiMod localMod in local.Mods)
        {
            if (!serverById.ContainsKey(NormalizeId(localMod.PackageId)))
            {
                entries.Add(new UiDiffEntry(UiDiffStatus.Added, T("ClashOfRim.Compatibility.UnexpectedLocalMod"), DisplayModName(localMod), localMod.PackageId));
            }
        }

        int max = Math.Max(server.Mods.Count, local.Mods.Count);
        for (int i = 0; i < max; i++)
        {
            string serverId = i < server.Mods.Count ? server.Mods[i].PackageId : None();
            string localId = i < local.Mods.Count ? local.Mods[i].PackageId : None();
            if (!string.Equals(NormalizeId(serverId), NormalizeId(localId), StringComparison.Ordinal))
            {
                entries.Add(new UiDiffEntry(
                    UiDiffStatus.Modified,
                    T("ClashOfRim.Compatibility.LoadOrderMismatch", ("INDEX", (i + 1))),
                    FormatServerLocal(serverId, localId),
                    "mods"));
            }
        }

        AppendUnrepresentedFallbackEntries(entries, fallbackIssues, CompatibilityTab.Manifest);
        return entries;
    }

    private List<UiDiffEntry> BuildHashEntries(
        UiManifest? server,
        UiManifest? local,
        IReadOnlyList<ModCompatibilityIssueDto>? fallbackIssues)
    {
        var entries = new List<UiDiffEntry>();
        if (server is null || local is null)
        {
            entries.AddRange(FallbackEntries(fallbackIssues, CompatibilityTab.Hash));
            return entries;
        }

        Dictionary<string, UiMod> localById = local.ModsById;
        foreach (UiMod serverMod in server.Mods)
        {
            if (!localById.TryGetValue(NormalizeId(serverMod.PackageId), out UiMod? localMod))
            {
                continue;
            }

            Dictionary<string, UiFile> localFiles = localMod.FilesByPath;
            Dictionary<string, UiFile> serverFiles = serverMod.FilesByPath;
            foreach (UiFile serverFile in serverMod.Files)
            {
                string key = NormalizePath(serverFile.RelativePath);
                if (!localFiles.TryGetValue(key, out UiFile? localFile))
                {
                    entries.Add(new UiDiffEntry(UiDiffStatus.Missing, T("ClashOfRim.Compatibility.MissingLocalFile"), $"{serverMod.PackageId}/{serverFile.RelativePath}", serverMod.PackageId));
                    continue;
                }

                if (serverFile.Size != localFile.Size)
                {
                    string hint = BuildVersionHint(serverMod, localMod, serverFile, localFile);
                    entries.Add(new UiDiffEntry(
                        UiDiffStatus.Modified,
                        T("ClashOfRim.Compatibility.FileSizeMismatch"),
                        $"{serverMod.PackageId}/{serverFile.RelativePath}\n" + FormatServerLocal(
                            T("ClashOfRim.Compatibility.Bytes", ("COUNT", serverFile.Size)),
                            T("ClashOfRim.Compatibility.Bytes", ("COUNT", localFile.Size))) + hint,
                        serverMod.PackageId));
                }

                if (!string.Equals(serverFile.Sha256, localFile.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    string hint = BuildVersionHint(serverMod, localMod, serverFile, localFile);
                    entries.Add(new UiDiffEntry(
                        UiDiffStatus.Modified,
                        T("ClashOfRim.Compatibility.FileHashMismatch"),
                        $"{serverMod.PackageId}/{serverFile.RelativePath}\n" + FormatServerLocal(ShortHash(serverFile.Sha256), ShortHash(localFile.Sha256)) + hint,
                        serverMod.PackageId));
                }
            }

            foreach (UiFile localFile in localMod.Files)
            {
                if (!serverFiles.ContainsKey(NormalizePath(localFile.RelativePath)))
                {
                    entries.Add(new UiDiffEntry(UiDiffStatus.Added, T("ClashOfRim.Compatibility.UnexpectedLocalFile"), $"{localMod.PackageId}/{localFile.RelativePath}", localMod.PackageId));
                }
            }
        }

        Dictionary<string, UiDefSummary> localDefs = local.DefsByName;
        Dictionary<string, UiDefSummary> serverDefs = server.DefsByName;
        foreach (UiDefSummary serverDef in server.DefSummaries)
        {
            if (!localDefs.TryGetValue(serverDef.Name, out UiDefSummary? localDef))
            {
                entries.Add(new UiDiffEntry(UiDiffStatus.Missing, T("ClashOfRim.Compatibility.MissingDefSummary"), serverDef.Name, serverDef.Name));
                continue;
            }

            if (serverDef.Count != localDef.Count || serverDef.Hash != localDef.Hash)
            {
                entries.Add(new UiDiffEntry(
                    UiDiffStatus.Modified,
                    T("ClashOfRim.Compatibility.DefSummaryMismatch"),
                    $"{serverDef.Name}\n" + FormatServerLocal(
                        $"count={serverDef.Count}, hash={serverDef.Hash}",
                        $"count={localDef.Count}, hash={localDef.Hash}"),
                    serverDef.Name));
            }
        }

        foreach (UiDefSummary localDef in local.DefSummaries)
        {
            if (!serverDefs.ContainsKey(localDef.Name))
            {
                entries.Add(new UiDiffEntry(UiDiffStatus.Added, T("ClashOfRim.Compatibility.UnexpectedDefSummary"), localDef.Name, localDef.Name));
            }
        }

        AppendUnrepresentedFallbackEntries(entries, fallbackIssues, CompatibilityTab.Hash);
        return entries;
    }

    private string BuildVersionHint(UiMod serverMod, UiMod localMod, UiFile serverFile, UiFile localFile)
    {
        if (serverFile.LastWriteUtcUnix > 0 && localFile.LastWriteUtcUnix > 0)
        {
            if (localFile.LastWriteUtcUnix > serverFile.LastWriteUtcUnix)
            {
                return "\n" + T("ClashOfRim.Compatibility.FileTimeHintServerMayNeedUpdate");
            }

            if (serverFile.LastWriteUtcUnix > localFile.LastWriteUtcUnix)
            {
                return "\n" + T("ClashOfRim.Compatibility.FileTimeHintClientMayNeedUpdate");
            }
        }

        return BuildWorkshopVersionHint(serverMod, localMod);
    }

    private string BuildWorkshopVersionHint(UiMod serverMod, UiMod localMod)
    {
        if (string.IsNullOrWhiteSpace(serverMod.WorkshopId)
            || string.IsNullOrWhiteSpace(localMod.WorkshopId)
            || !string.Equals(serverMod.WorkshopId.Trim(), localMod.WorkshopId.Trim(), StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if ((localMod.WorkshopItemState ?? string.Empty).IndexOf("NeedsUpdate", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "\n" + T("ClashOfRim.Compatibility.WorkshopHintClientNeedsUpdate");
        }

        if (serverMod.WorkshopLocalInstalledAtUnix > 0 && localMod.WorkshopLocalInstalledAtUnix > 0)
        {
            if (localMod.WorkshopLocalInstalledAtUnix > serverMod.WorkshopLocalInstalledAtUnix)
            {
                return "\n" + T("ClashOfRim.Compatibility.WorkshopHintServerMayNeedUpdate");
            }

            if (serverMod.WorkshopLocalInstalledAtUnix > localMod.WorkshopLocalInstalledAtUnix)
            {
                return "\n" + T("ClashOfRim.Compatibility.WorkshopHintClientMayNeedUpdate");
            }
        }

        return "\n" + T("ClashOfRim.Compatibility.WorkshopHintUncertain");
    }

    private List<UiDiffEntry> BuildConfigEntries(
        UiManifest? server,
        UiManifest? local,
        IReadOnlyList<ModCompatibilityIssueDto>? fallbackIssues)
    {
        var entries = new List<UiDiffEntry>();
        if (server is null || local is null)
        {
            entries.AddRange(FallbackEntries(fallbackIssues, CompatibilityTab.Config));
            return entries;
        }

        AddScalar(entries, T("ClashOfRim.Compatibility.ConfigTotalHash"), server.ConfigSha256, local.ConfigSha256);
        Dictionary<string, UiMod> localById = local.ModsById;
        foreach (UiMod serverMod in server.Mods)
        {
            if (!localById.TryGetValue(NormalizeId(serverMod.PackageId), out UiMod? localMod))
            {
                continue;
            }

            Dictionary<string, UiConfig> localConfigs = localMod.ConfigsByName;
            foreach (UiConfig serverConfig in serverMod.Configs)
            {
                if (!localConfigs.TryGetValue(serverConfig.FileName, out UiConfig? localConfig))
                {
                    entries.Add(new UiDiffEntry(UiDiffStatus.Missing, T("ClashOfRim.Compatibility.MissingLocalConfig"), $"{serverMod.PackageId}/{serverConfig.FileName}", serverMod.PackageId));
                    continue;
                }

                if (serverConfig.HasSavedFile != localConfig.HasSavedFile
                    || (serverConfig.HasSavedFile
                        && !string.Equals(serverConfig.Sha256, localConfig.Sha256, StringComparison.OrdinalIgnoreCase)))
                {
                    entries.Add(new UiDiffEntry(
                        UiDiffStatus.Modified,
                        T("ClashOfRim.Compatibility.ConfigHashMismatch"),
                        $"{serverMod.PackageId}/{serverConfig.FileName}\n" + FormatServerLocal(ShortHash(serverConfig.Sha256), ShortHash(localConfig.Sha256)),
                        serverMod.PackageId));
                }
            }

            // Extra local configs are passthrough files and are intentionally omitted from the
            // mismatch list. The server baseline only owns config files it explicitly declares.
        }

        AppendUnrepresentedFallbackEntries(entries, fallbackIssues, CompatibilityTab.Config);
        return entries;
    }

    private IEnumerable<UiDiffEntry> FallbackEntries(
        IReadOnlyList<ModCompatibilityIssueDto>? issues,
        CompatibilityTab tab)
    {
        return (issues ?? new List<ModCompatibilityIssueDto>())
            .Where(issue => IssueBelongsToTab(issue.Code, tab))
            .Select(issue => new UiDiffEntry(
                IsSeverity(issue, "Error") ? UiDiffStatus.Modified : UiDiffStatus.Added,
                LocalizeCode(issue.Code),
                (string.IsNullOrWhiteSpace(issue.Subject) ? string.Empty : "[" + issue.Subject + "] ")
                + (string.IsNullOrWhiteSpace(issue.Message) ? T("ClashOfRim.Compatibility.ServerIssueFallback") : issue.Message.Trim()),
                issue.Subject));
    }

    private void AppendUnrepresentedFallbackEntries(
        List<UiDiffEntry> entries,
        IReadOnlyList<ModCompatibilityIssueDto>? issues,
        CompatibilityTab tab)
    {
        foreach (UiDiffEntry fallback in FallbackEntries(issues, tab))
        {
            bool hasMatchingSubject = !string.IsNullOrWhiteSpace(fallback.Subject)
                && entries.Any(entry => string.Equals(
                    NormalizeId(entry.Subject ?? string.Empty),
                    NormalizeId(fallback.Subject),
                    StringComparison.Ordinal));
            bool hasMatchingContent = entries.Any(entry => string.Equals(entry.Title, fallback.Title, StringComparison.Ordinal)
                && string.Equals(entry.Detail, fallback.Detail, StringComparison.Ordinal));
            if (!hasMatchingSubject && !hasMatchingContent)
            {
                entries.Add(fallback);
            }
        }
    }

    private bool IssueBelongsToTab(string? code, CompatibilityTab tab)
    {
        if (CompatibilityLanguageMismatchPolicy.IsLanguageMismatch(code))
        {
            return false;
        }

        if (!Enum.TryParse(code, ignoreCase: false, out CompatibilityIssueCode issueCode))
        {
            return tab == CompatibilityTab.Manifest;
        }

        CompatibilityIssueCategory category = CompatibilityIssueClassifier.CategoryFor(issueCode);
        return category switch
        {
            CompatibilityIssueCategory.Manifest => tab == CompatibilityTab.Manifest,
            CompatibilityIssueCategory.Hash => tab == CompatibilityTab.Hash,
            CompatibilityIssueCategory.Config => tab == CompatibilityTab.Config,
            CompatibilityIssueCategory.Overview => tab == CompatibilityTab.Overview,
            _ => false
        };
    }

    private void AddScalar(List<UiDiffEntry> entries, string title, string server, string local)
    {
        if (!string.Equals(server ?? string.Empty, local ?? string.Empty, StringComparison.Ordinal))
        {
            entries.Add(new UiDiffEntry(
                UiDiffStatus.Modified,
                title,
                FormatServerLocal(server ?? string.Empty, local ?? string.Empty),
                title));
        }
    }

    private void AddSequence(List<UiDiffEntry> entries, string title, IReadOnlyList<string> server, IReadOnlyList<string> local)
    {
        string serverText = string.Join(", ", server ?? new List<string>());
        string localText = string.Join(", ", local ?? new List<string>());
        if (!string.Equals(serverText, localText, StringComparison.Ordinal))
        {
            entries.Add(new UiDiffEntry(
                UiDiffStatus.Modified,
                title,
                FormatServerLocal(serverText, localText),
                title));
        }
    }

    private string FormatServerLocal(string server, string local)
    {
        return T(
            "ClashOfRim.Compatibility.ServerLocalLines",
            ("SERVER", server),
            ("LOCAL", local));
    }

    private string None()
    {
        return T("ClashOfRim.None");
    }

    private string DisplayModName(UiMod mod)
    {
        return string.IsNullOrWhiteSpace(mod.Name) ? mod.PackageId : mod.Name;
    }

    private string NormalizeId(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private string NormalizePath(string? value)
    {
        return (value ?? string.Empty).Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    }

    private string ShortHash(string? value)
    {
        string text = value ?? string.Empty;
        return text.Length <= 16 ? text : text.Substring(0, 16) + "...";
    }

    private bool IsSeverity(ModCompatibilityIssueDto issue, string severity)
    {
        return string.Equals(issue.Severity, severity, StringComparison.OrdinalIgnoreCase);
    }

    private string LocalizeCode(string? code)
    {
        return (code ?? string.Empty).Trim() switch
        {
            "SchemaVersionMismatch" => T("ClashOfRim.Compatibility.Issue.SchemaVersionMismatch"),
            "ProtocolVersionMismatch" => T("ClashOfRim.Compatibility.Issue.ProtocolVersionMismatch"),
            "RimWorldVersionMismatch" => T("ClashOfRim.Compatibility.Issue.RimWorldVersionMismatch"),
            "GameLanguageMismatch" => T("ClashOfRim.Compatibility.Issue.GameLanguageMismatch"),
            "DlcListMismatch" => T("ClashOfRim.Compatibility.Issue.DlcListMismatch"),
            "ModListMismatch" => T("ClashOfRim.Compatibility.Issue.ModListMismatch"),
            "ModOrderMismatch" => T("ClashOfRim.Compatibility.Issue.ModOrderMismatch"),
            "MissingMod" => T("ClashOfRim.Compatibility.Issue.MissingMod"),
            "UnexpectedMod" => T("ClashOfRim.Compatibility.Issue.UnexpectedMod"),
            "AllowedPureTranslationMod" => T("ClashOfRim.Compatibility.Issue.AllowedPureTranslationMod"),
            "FileMissing" => T("ClashOfRim.Compatibility.Issue.FileMissing"),
            "FileUnexpected" => T("ClashOfRim.Compatibility.Issue.FileUnexpected"),
            "FileHashMismatch" => T("ClashOfRim.Compatibility.Issue.FileHashMismatch"),
            "FileSizeMismatch" => T("ClashOfRim.Compatibility.Issue.FileSizeMismatch"),
            "ConfigVersionMismatch" => T("ClashOfRim.Compatibility.Issue.ConfigVersionMismatch"),
            "ConfigHashMismatch" => T("ClashOfRim.Compatibility.Issue.ConfigHashMismatch"),
            "ConfigFileMismatch" => T("ClashOfRim.Compatibility.Issue.ConfigFileMismatch"),
            "ConfigFileMissing" => T("ClashOfRim.Compatibility.Issue.ConfigFileMissing"),
            "ConfigFileUnexpected" => T("ClashOfRim.Compatibility.Issue.ConfigFileUnexpected"),
            "ConfigFileWarning" => T("ClashOfRim.Compatibility.Issue.ConfigFileWarning"),
            "DefSummaryMissing" => T("ClashOfRim.Compatibility.Issue.DefSummaryMissing"),
            "DefSummaryUnexpected" => T("ClashOfRim.Compatibility.Issue.DefSummaryUnexpected"),
            "DefSummaryCountMismatch" => T("ClashOfRim.Compatibility.Issue.DefSummaryCountMismatch"),
            "DefSummaryHashMismatch" => T("ClashOfRim.Compatibility.Issue.DefSummaryHashMismatch"),
            "" => T("ClashOfRim.Compatibility.Issue.Unknown"),
            var value => value
        };
    }


    internal enum CompatibilityTab { Overview, Manifest, Hash, Config }
    internal enum UiDiffStatus { Match, Missing, Added, Modified }
    internal sealed record UiDiffEntry(UiDiffStatus Status, string Title, string Detail, string? Subject);
    internal sealed record ModRow(string PackageId, string Name, UiDiffStatus Status);

    [DataContract]
    private sealed class UiManifest
    {
        internal Dictionary<string, UiMod> ModsById = new();
        internal Dictionary<string, UiDefSummary> DefsByName = new();
        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "manifestId")]
        public string ManifestId { get; set; } = string.Empty;

        [DataMember(Name = "protocolVersion")]
        public string ProtocolVersion { get; set; } = string.Empty;

        [DataMember(Name = "rimWorldVersion")]
        public string RimWorldVersion { get; set; } = string.Empty;

        [DataMember(Name = "gameLanguage")]
        public string GameLanguage { get; set; } = string.Empty;

        [DataMember(Name = "dlcIds")]
        public List<string> DlcIds { get; set; } = new();

        [DataMember(Name = "configSha256")]
        public string ConfigSha256 { get; set; } = string.Empty;

        [DataMember(Name = "mods")]
        public List<UiMod> Mods { get; set; } = new();

        [DataMember(Name = "defSummaries")]
        public List<UiDefSummary> DefSummaries { get; set; } = new();

    }

    [DataContract]
    private sealed class UiMod
    {
        internal Dictionary<string, UiFile> FilesByPath = new();
        internal Dictionary<string, UiConfig> ConfigsByName = new();
        [DataMember(Name = "loadOrder")]
        public int LoadOrder { get; set; }

        [DataMember(Name = "packageId")]
        public string PackageId { get; set; } = string.Empty;

        [DataMember(Name = "name")]
        public string Name { get; set; } = string.Empty;

        [DataMember(Name = "workshopId")]
        public string WorkshopId { get; set; } = string.Empty;

        [DataMember(Name = "workshopItemState")]
        public string WorkshopItemState { get; set; } = string.Empty;

        [DataMember(Name = "workshopLocalInstalledAtUnix")]
        public long WorkshopLocalInstalledAtUnix { get; set; }

        [DataMember(Name = "files")]
        public List<UiFile> Files { get; set; } = new();

        [DataMember(Name = "configs")]
        public List<UiConfig> Configs { get; set; } = new();

    }

    [DataContract]
    private sealed class UiFile
    {
        [DataMember(Name = "relativePath")]
        public string RelativePath { get; set; } = string.Empty;

        [DataMember(Name = "size")]
        public long Size { get; set; }

        [DataMember(Name = "sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [DataMember(Name = "lastWriteUtcUnix")]
        public long LastWriteUtcUnix { get; set; }

    }

    [DataContract]
    private sealed class UiConfig
    {
        [DataMember(Name = "fileName")]
        public string FileName { get; set; } = string.Empty;

        [DataMember(Name = "sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [DataMember(Name = "hasSavedFile")]
        public bool HasSavedFile { get; set; } = true;

    }

    [DataContract]
    private sealed class UiDefSummary
    {
        [DataMember(Name = "name")]
        public string Name { get; set; } = string.Empty;

        [DataMember(Name = "count")]
        public int Count { get; set; }

        [DataMember(Name = "hash")]
        public int Hash { get; set; }
    }
}
