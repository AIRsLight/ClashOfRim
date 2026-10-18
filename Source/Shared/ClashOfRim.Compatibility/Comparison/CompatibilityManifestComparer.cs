using static AIRsLight.ClashOfRim.Compatibility.CompatibilityManifestRules;

namespace AIRsLight.ClashOfRim.Compatibility;

public static class CompatibilityManifestComparer
{
    public static CompatibilityComparisonResult Compare(CompatibilityManifest server, CompatibilityManifest client)
    {
        return Compare(server, client, CompatibilityComparisonOptions.Default);
    }

    public static CompatibilityComparisonResult Compare(
        CompatibilityManifest server,
        CompatibilityManifest client,
        CompatibilityComparisonOptions options)
    {
        var issues = new List<CompatibilityIssue>();
        options ??= CompatibilityComparisonOptions.Default;

        CompareHeader(issues, server, client);
        CompareMods(issues, server.Mods, client.Mods, options);
        CompareDefSummaries(issues, server.DefSummaries, client.DefSummaries);

        return new CompatibilityComparisonResult(issues);
    }

    private static void CompareMods(List<CompatibilityIssue> issues, IReadOnlyList<ModManifestEntry> serverMods,
        IReadOnlyList<ModManifestEntry> clientMods, CompatibilityComparisonOptions options)
        => CompatibilityModSetComparison.Compare(issues, serverMods, clientMods,
            mod => options.AllowExtraPureTranslationMods && ModFileClassifier.IsPureTranslationMod(mod),
            (server, client) => { CompareModFiles(issues, server, client); CompareModConfigs(issues, server, client, options); },
            mod => issues.Add(new(CompatibilityIssueSeverity.Info, CompatibilityIssueCode.AllowedPureTranslationMod,
                $"Allowed extra pure translation mod: {mod.PackageId}", mod.PackageId)));

    // Compare only details requested by the summary exchange. The caller owns selection;
    // unknown packages remain rejected and common metadata is checked once per response.
    internal static CompatibilityComparisonResult CompareSelected(CompatibilityManifest server, CompatibilityManifest client,
        CompatibilityComparisonOptions options)
    {
        var issues = new List<CompatibilityIssue>();
        CompareHeader(issues, server, client);
        var byId = server.Mods.ToDictionary(mod => NormalizeId(mod.PackageId));
        foreach (var mod in client.Mods)
        {
            if (!byId.TryGetValue(NormalizeId(mod.PackageId), out var baseline))
            { issues.Add(Error(CompatibilityIssueCode.UnexpectedMod, $"Unexpected unapproved mod: {mod.PackageId}", mod.PackageId)); continue; }
            CompareModFiles(issues, baseline, mod);
            CompareModConfigs(issues, baseline, mod, options);
        }
        CompareDefSummaries(issues, server.DefSummaries, client.DefSummaries);
        return new(issues);
    }

    private static void CompareModFiles(List<CompatibilityIssue> issues, ModManifestEntry serverMod, ModManifestEntry clientMod)
    {
        var serverFiles = serverMod.Files.ToDictionary(file => NormalizePath(file.RelativePath), file => file);
        var clientFiles = clientMod.Files.ToDictionary(file => NormalizePath(file.RelativePath), file => file);

        foreach (KeyValuePair<string, ControlledFileEntry> serverFilePair in serverFiles)
        {
            string path = serverFilePair.Key;
            ControlledFileEntry serverFile = serverFilePair.Value;
            if (!clientFiles.TryGetValue(path, out ControlledFileEntry? clientFile))
            {
                issues.Add(Error(CompatibilityIssueCode.FileMissing, $"Mod file missing: {serverMod.PackageId}/{path}", serverMod.PackageId));
                continue;
            }

            if (serverFile.Size != clientFile.Size)
            {
                issues.Add(Error(CompatibilityIssueCode.FileSizeMismatch, $"Mod file size mismatch: {serverMod.PackageId}/{path}", serverMod.PackageId));
            }

            if (!string.Equals(serverFile.Sha256, clientFile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(Error(CompatibilityIssueCode.FileHashMismatch, $"Mod file hash mismatch: {serverMod.PackageId}/{path}", serverMod.PackageId));
            }
        }

        foreach (string path in clientFiles.Keys.Except(serverFiles.Keys, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(Error(CompatibilityIssueCode.FileUnexpected, $"Mod contains unapproved file: {clientMod.PackageId}/{path}", clientMod.PackageId));
        }
    }

    private static void CompareModConfigs(
        List<CompatibilityIssue> issues,
        ModManifestEntry serverMod,
        ModManifestEntry clientMod,
        CompatibilityComparisonOptions options)
    {
        var serverConfigs = serverMod.Configs.ToDictionary(config => config.FileName, StringComparer.OrdinalIgnoreCase);
        var clientConfigs = clientMod.Configs.ToDictionary(config => config.FileName, StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, ModConfigDigest> serverConfigPair in serverConfigs)
        {
            string fileName = serverConfigPair.Key;
            ModConfigDigest serverConfig = serverConfigPair.Value;
            ModConfigComparisonMode mode = options.ResolveConfigMode(serverMod.PackageId, fileName);
            if (mode == ModConfigComparisonMode.Ignore)
            {
                continue;
            }

            bool missing = !clientConfigs.TryGetValue(fileName, out ModConfigDigest? clientConfig);
            bool mismatch = missing
                || serverConfig.HasSavedFile != clientConfig!.HasSavedFile
                || (serverConfig.HasSavedFile
                    && !string.Equals(serverConfig.Sha256, clientConfig.Sha256, StringComparison.OrdinalIgnoreCase));
            if (mismatch)
            {
                issues.Add(new CompatibilityIssue(
                    mode == ModConfigComparisonMode.Warn ? CompatibilityIssueSeverity.Warning : CompatibilityIssueSeverity.Error,
                    mode == ModConfigComparisonMode.Warn ? CompatibilityIssueCode.ConfigFileWarning :
                        missing ? CompatibilityIssueCode.ConfigFileMissing : CompatibilityIssueCode.ConfigFileMismatch,
                    missing
                        ? $"Mod config missing: {serverMod.PackageId}/{fileName}"
                        : $"Mod config mismatch: {serverMod.PackageId}/{fileName}",
                    serverMod.PackageId));
            }
        }

        // Extra local config files are treated as passthrough state. Mods such as blueprint managers
        // store user data in the config directory; the server only controls config files present in
        // its baseline and should not reject or delete unrelated local files.
    }

    public static void CompareGameLanguage(List<CompatibilityIssue> issues, string? server, string? client)
    {
        string serverLanguage = NormalizeLanguage(server);
        string clientLanguage = NormalizeLanguage(client);
        if (string.IsNullOrWhiteSpace(serverLanguage)
            || string.IsNullOrWhiteSpace(clientLanguage)
            || string.Equals(serverLanguage, clientLanguage, StringComparison.Ordinal))
        {
            return;
        }

        issues.Add(new CompatibilityIssue(
            CompatibilityIssueSeverity.Warning,
            CompatibilityIssueCode.GameLanguageMismatch,
            $"Game language mismatch: server={server}, client={client}",
            "gameLanguage"));
    }

    private static string NormalizeLanguage(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string NormalizePath(string value)
    {
        return value.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    }
}
