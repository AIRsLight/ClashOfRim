using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Compatibility;

internal static class CompatibilityHandshakeComparison
{
    internal static CompatibilitySummaryHandshake CompareCompatibilitySummary(
        CompatibilityManifest server,
        CompatibilityManifestSummary client,
        CompatibilityComparisonOptions options)
    {
        var issues = new List<CompatibilityIssue>();
        CompareSummaryScalar(issues, server.SchemaVersion, client.SchemaVersion, CompatibilityIssueCode.SchemaVersionMismatch, "Manifest schema version mismatch", "schemaVersion");
        CompareSummaryScalar(issues, server.ProtocolVersion, client.ProtocolVersion, CompatibilityIssueCode.ProtocolVersionMismatch, "Protocol version mismatch", "protocolVersion");
        CompareSummaryScalar(issues, server.RimWorldVersion, client.RimWorldVersion, CompatibilityIssueCode.RimWorldVersionMismatch, "RimWorld version mismatch", "rimWorldVersion");
        CompareSummarySequence(issues, server.DlcIds, client.DlcIds, CompatibilityIssueCode.DlcListMismatch, "Enabled DLC list mismatch", "dlcIds");
        CompareSummaryDefSummaries(issues, server.DefSummaries, client.DefSummaries);

        var serverById = server.Mods.ToDictionary(mod => NormalizeCompatibilityId(mod.PackageId), mod => mod);
        var clientById = client.Mods.ToDictionary(mod => NormalizeCompatibilityId(mod.PackageId), mod => mod);
        var requested = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ModManifestEntry serverMod in server.Mods.OrderBy(mod => mod.LoadOrder))
        {
            string key = NormalizeCompatibilityId(serverMod.PackageId);
            if (!clientById.TryGetValue(key, out ModManifestSummaryEntry? clientMod))
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

                issues.Add(new CompatibilityIssue(
                    CompatibilityIssueSeverity.Error,
                    CompatibilityIssueCode.MissingMod,
                    $"Missing required mod: {serverMod.PackageId}",
                    serverMod.PackageId));
                continue;
            }

            if (!string.Equals(ComputeServerModHash(serverMod), clientMod.Hash, StringComparison.OrdinalIgnoreCase))
            {
                requested.Add(serverMod.PackageId);
            }
        }

        foreach (ModManifestSummaryEntry clientMod in client.Mods)
        {
            if (!serverById.ContainsKey(NormalizeCompatibilityId(clientMod.PackageId)))
            {
                if (options.AllowExtraPureTranslationMods && clientMod.Role == ModCompatibilityRole.OptionalPureTranslation)
                {
                    issues.Add(new CompatibilityIssue(
                        CompatibilityIssueSeverity.Info,
                        CompatibilityIssueCode.AllowedPureTranslationMod,
                        $"Allowed extra pure translation mod: {clientMod.PackageId}",
                        clientMod.PackageId));
                    continue;
                }

                issues.Add(new CompatibilityIssue(
                    CompatibilityIssueSeverity.Error,
                    CompatibilityIssueCode.UnexpectedMod,
                    $"Unexpected unapproved mod: {clientMod.PackageId}",
                    clientMod.PackageId));
            }
        }

        string[] serverOrder = server.Mods
            .OrderBy(mod => mod.LoadOrder)
            .Select(mod => NormalizeCompatibilityId(mod.PackageId))
            .ToArray();
        string[] clientOrder = client.Mods
            .OrderBy(mod => mod.LoadOrder)
            .Select(mod => NormalizeCompatibilityId(mod.PackageId))
            .ToArray();
        if (!serverOrder.SequenceEqual(clientOrder))
        {
            issues.Add(new CompatibilityIssue(
                CompatibilityIssueSeverity.Error,
                CompatibilityIssueCode.ModOrderMismatch,
                "Mod load order mismatch, or an unapproved non-translation mod is present",
                "mods"));
        }

        bool hasError = issues.Any(issue => issue.Severity == CompatibilityIssueSeverity.Error);
        if (hasError)
        {
            return new CompatibilitySummaryHandshake(false, false, Array.Empty<string>(), issues);
        }

        if (requested.Count > 0)
        {
            return new CompatibilitySummaryHandshake(false, true, requested.ToArray(), issues);
        }

        return new CompatibilitySummaryHandshake(true, false, Array.Empty<string>(), issues);
    }

    internal static bool IsPartialCompatibilityManifest(CompatibilityManifest server, CompatibilityManifest client)
    {
        return client.Mods.Count > 0 && client.Mods.Count < server.Mods.Count;
    }

    internal static CompatibilityComparisonResult ComparePartialCompatibilityManifest(
        CompatibilityManifest server,
        CompatibilityManifest client,
        CompatibilityComparisonOptions options)
    {
        var issues = new List<CompatibilityIssue>();
        CompareSummaryScalar(issues, server.SchemaVersion, client.SchemaVersion, CompatibilityIssueCode.SchemaVersionMismatch, "Manifest schema version mismatch", "schemaVersion");
        CompareSummaryScalar(issues, server.ProtocolVersion, client.ProtocolVersion, CompatibilityIssueCode.ProtocolVersionMismatch, "Protocol version mismatch", "protocolVersion");
        CompareSummaryScalar(issues, server.RimWorldVersion, client.RimWorldVersion, CompatibilityIssueCode.RimWorldVersionMismatch, "RimWorld version mismatch", "rimWorldVersion");
        CompareSummarySequence(issues, server.DlcIds, client.DlcIds, CompatibilityIssueCode.DlcListMismatch, "Enabled DLC list mismatch", "dlcIds");
        CompareSummaryDefSummaries(issues, server.DefSummaries, client.DefSummaries);

        var serverById = server.Mods.ToDictionary(mod => NormalizeCompatibilityId(mod.PackageId), mod => mod);
        foreach (ModManifestEntry clientMod in client.Mods)
        {
            if (!serverById.TryGetValue(NormalizeCompatibilityId(clientMod.PackageId), out ModManifestEntry? serverMod))
            {
                issues.Add(new CompatibilityIssue(
                    CompatibilityIssueSeverity.Error,
                    CompatibilityIssueCode.UnexpectedMod,
                    $"Unexpected unapproved mod: {clientMod.PackageId}",
                    clientMod.PackageId));
                continue;
            }

            CompatibilityManifest partialServer = server with { Mods = new[] { serverMod } };
            CompatibilityManifest partialClient = client with { Mods = new[] { clientMod } };
            issues.AddRange(CompatibilityManifestComparer.Compare(partialServer, partialClient, options).Issues);
        }

        return new CompatibilityComparisonResult(issues);
    }

    private static void CompareSummaryScalar<T>(
        List<CompatibilityIssue> issues,
        T server,
        T client,
        CompatibilityIssueCode code,
        string message,
        string subject)
    {
        if (!EqualityComparer<T>.Default.Equals(server, client))
        {
            issues.Add(new CompatibilityIssue(CompatibilityIssueSeverity.Error, code, message, subject));
        }
    }

    private static void CompareSummarySequence(
        List<CompatibilityIssue> issues,
        IReadOnlyList<string> server,
        IReadOnlyList<string> client,
        CompatibilityIssueCode code,
        string message,
        string subject)
    {
        if (!server.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(client.OrderBy(value => value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new CompatibilityIssue(CompatibilityIssueSeverity.Error, code, message, subject));
        }
    }

    private static void CompareSummaryDefSummaries(
        List<CompatibilityIssue> issues,
        IReadOnlyList<DefSummary> serverDefs,
        IReadOnlyList<DefSummary> clientDefs)
    {
        var serverByName = serverDefs.ToDictionary(def => def.Name, StringComparer.OrdinalIgnoreCase);
        var clientByName = clientDefs.ToDictionary(def => def.Name, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, DefSummary> serverDefPair in serverByName)
        {
            if (!clientByName.TryGetValue(serverDefPair.Key, out DefSummary? clientDef)
                || serverDefPair.Value.Count != clientDef.Count
                || serverDefPair.Value.Hash != clientDef.Hash)
            {
                issues.Add(new CompatibilityIssue(
                    CompatibilityIssueSeverity.Error,
                    CompatibilityIssueCode.DefSummaryHashMismatch,
                    $"Def summary mismatch: {serverDefPair.Key}",
                    serverDefPair.Key));
            }
        }

        foreach (string name in clientByName.Keys.Except(serverByName.Keys, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new CompatibilityIssue(
                CompatibilityIssueSeverity.Error,
                CompatibilityIssueCode.DefSummaryUnexpected,
                $"Client has unapproved Def summary: {name}",
                name));
        }
    }

    private static string ComputeServerModHash(ModManifestEntry mod)
    {
        string stableBody = string.Join("\n", new[]
            {
                mod.LoadOrder.ToString(CultureInfo.InvariantCulture),
                mod.PackageId,
                mod.Name,
                mod.Source,
                mod.WorkshopId,
                mod.Role.ToString()
            })
            + "\n" + string.Join("\n", mod.Files
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(file => file.RelativePath + "|" + file.Size.ToString(CultureInfo.InvariantCulture) + "|" + file.Sha256 + "|" + file.Kind))
            + "\n" + string.Join("\n", mod.Configs
                .OrderBy(config => config.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(config => config.FileName + "|" + config.Sha256));
        using SHA256 sha = SHA256.Create();
        byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(stableBody));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormalizeCompatibilityId(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    internal sealed record CompatibilitySummaryHandshake(
        bool Accepted,
        bool RequiresFullManifest,
        IReadOnlyList<string> RequestedPackageIds,
        IReadOnlyList<CompatibilityIssue> Issues);

}
