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

        CompareScalar(issues, server.SchemaVersion, client.SchemaVersion, CompatibilityIssueCode.SchemaVersionMismatch, "Manifest schema version mismatch", "schemaVersion");
        CompareScalar(issues, server.ProtocolVersion, client.ProtocolVersion, CompatibilityIssueCode.ProtocolVersionMismatch, "Protocol version mismatch", "protocolVersion");
        CompareScalar(issues, server.RimWorldVersion, client.RimWorldVersion, CompatibilityIssueCode.RimWorldVersionMismatch, "RimWorld version mismatch", "rimWorldVersion");
        CompareSequence(issues, server.DlcIds, client.DlcIds, CompatibilityIssueCode.DlcListMismatch, "Enabled DLC list mismatch", "dlcIds");
        CompareMods(issues, server.Mods, client.Mods, options);
        CompareDefSummaries(issues, server.DefSummaries, client.DefSummaries);

        return new CompatibilityComparisonResult(issues);
    }

    private static void CompareMods(
        List<CompatibilityIssue> issues,
        IReadOnlyList<ModManifestEntry> serverMods,
        IReadOnlyList<ModManifestEntry> clientMods,
        CompatibilityComparisonOptions options)
    {
        var serverById = serverMods.ToDictionary(mod => NormalizeId(mod.PackageId), mod => mod);
        var clientById = clientMods.ToDictionary(mod => NormalizeId(mod.PackageId), mod => mod);

        foreach (ModManifestEntry serverMod in serverMods.OrderBy(mod => mod.LoadOrder))
        {
            if (!clientById.TryGetValue(NormalizeId(serverMod.PackageId), out ModManifestEntry? clientMod))
            {
                if (serverMod.Role is ModCompatibilityRole.Optional or ModCompatibilityRole.OptionalPureTranslation)
                {
                    issues.Add(new CompatibilityIssue(
                        CompatibilityIssueSeverity.Info,
                        CompatibilityIssueCode.MissingMod,
                        $"Optional mod not installed: {serverMod.PackageId}",
                        serverMod.PackageId));
                    continue;
                }

                issues.Add(Error(CompatibilityIssueCode.MissingMod, $"Missing required mod: {serverMod.PackageId}", serverMod.PackageId));
                continue;
            }

            CompareModFiles(issues, serverMod, clientMod);
            CompareModConfigs(issues, serverMod, clientMod, options);
        }

        foreach (ModManifestEntry clientMod in clientMods)
        {
            if (serverById.ContainsKey(NormalizeId(clientMod.PackageId)))
            {
                continue;
            }

            if (options.AllowExtraPureTranslationMods && ModFileClassifier.IsPureTranslationMod(clientMod))
            {
                issues.Add(new CompatibilityIssue(
                    CompatibilityIssueSeverity.Info,
                    CompatibilityIssueCode.AllowedPureTranslationMod,
                    $"Allowed extra pure translation mod: {clientMod.PackageId}",
                    clientMod.PackageId));
            }
            else
            {
                issues.Add(Error(CompatibilityIssueCode.UnexpectedMod, $"Unexpected unapproved mod: {clientMod.PackageId}", clientMod.PackageId));
            }
        }

        var serverOrder = serverMods
            .Where(mod => clientById.ContainsKey(NormalizeId(mod.PackageId))
                || mod.Role == ModCompatibilityRole.Required)
            .OrderBy(mod => mod.LoadOrder)
            .Select(mod => NormalizeId(mod.PackageId))
            .ToArray();
        var clientOrder = clientMods
            .Where(mod => serverById.ContainsKey(NormalizeId(mod.PackageId))
                || !options.AllowExtraPureTranslationMods
                || !ModFileClassifier.IsPureTranslationMod(mod))
            .OrderBy(mod => mod.LoadOrder)
            .Select(mod => NormalizeId(mod.PackageId))
            .ToArray();

        if (!serverOrder.SequenceEqual(clientOrder))
        {
            issues.Add(Error(
                CompatibilityIssueCode.ModOrderMismatch,
                "Mod load order mismatch, or an unapproved non-translation mod is present",
                "mods"));
        }
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
            bool mismatch = missing || !string.Equals(serverConfig.Sha256, clientConfig!.Sha256, StringComparison.OrdinalIgnoreCase);
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

    private static void CompareDefSummaries(List<CompatibilityIssue> issues, IReadOnlyList<DefSummary> serverDefs, IReadOnlyList<DefSummary> clientDefs)
    {
        var serverByName = serverDefs.ToDictionary(def => def.Name, StringComparer.OrdinalIgnoreCase);
        var clientByName = clientDefs.ToDictionary(def => def.Name, StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, DefSummary> serverDefPair in serverByName)
        {
            string name = serverDefPair.Key;
            DefSummary serverDef = serverDefPair.Value;
            if (!clientByName.TryGetValue(name, out DefSummary? clientDef))
            {
                issues.Add(Error(CompatibilityIssueCode.DefSummaryMissing, $"Missing Def summary: {name}", name));
                continue;
            }

            if (serverDef.Count != clientDef.Count)
            {
                issues.Add(Error(CompatibilityIssueCode.DefSummaryCountMismatch, $"Def count mismatch: {name}", name));
            }

            if (serverDef.Hash != clientDef.Hash)
            {
                issues.Add(Error(CompatibilityIssueCode.DefSummaryHashMismatch, $"Def aggregate hash mismatch: {name}", name));
            }
        }

        foreach (string name in clientByName.Keys.Except(serverByName.Keys, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(Error(CompatibilityIssueCode.DefSummaryUnexpected, $"Client has unapproved Def summary: {name}", name));
        }
    }

    private static void CompareScalar<T>(List<CompatibilityIssue> issues, T server, T client, CompatibilityIssueCode code, string message, string subject)
    {
        if (!EqualityComparer<T>.Default.Equals(server, client))
        {
            issues.Add(Error(code, message, subject));
        }
    }

    private static void CompareSequence(List<CompatibilityIssue> issues, IReadOnlyList<string> server, IReadOnlyList<string> client, CompatibilityIssueCode code, string message, string subject)
    {
        string[] serverValues = server.Select(NormalizeId).ToArray();
        string[] clientValues = client.Select(NormalizeId).ToArray();
        if (!serverValues.SequenceEqual(clientValues))
        {
            issues.Add(Error(code, message, subject));
        }
    }

    private static CompatibilityIssue Error(CompatibilityIssueCode code, string message, string? subject)
    {
        return new CompatibilityIssue(CompatibilityIssueSeverity.Error, code, message, subject);
    }

    private static string NormalizeId(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static string NormalizePath(string value)
    {
        return value.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    }
}
