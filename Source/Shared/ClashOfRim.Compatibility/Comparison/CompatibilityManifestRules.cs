namespace AIRsLight.ClashOfRim.Compatibility;

internal static class CompatibilityManifestRules
{
    internal static void CompareHeader(List<CompatibilityIssue> issues, ICompatibilityManifestMetadata server, ICompatibilityManifestMetadata client)
    {
        CompareScalar(issues, server.SchemaVersion, client.SchemaVersion, CompatibilityIssueCode.SchemaVersionMismatch, "Manifest schema version mismatch", "schemaVersion");
        CompareScalar(issues, server.ProtocolVersion, client.ProtocolVersion, CompatibilityIssueCode.ProtocolVersionMismatch, "Protocol version mismatch", "protocolVersion");
        CompareScalar(issues, server.RimWorldVersion, client.RimWorldVersion, CompatibilityIssueCode.RimWorldVersionMismatch, "RimWorld version mismatch", "rimWorldVersion");
        CompareSequence(issues, server.DlcIds, client.DlcIds, CompatibilityIssueCode.DlcListMismatch, "Enabled DLC list mismatch", "dlcIds");
    }

    internal static void CompareDefSummaries(List<CompatibilityIssue> issues, IReadOnlyList<DefSummary> serverDefs, IReadOnlyList<DefSummary> clientDefs)
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

    internal static CompatibilityIssue Error(CompatibilityIssueCode code, string message, string? subject)
    {
        return new CompatibilityIssue(CompatibilityIssueSeverity.Error, code, message, subject);
    }

    internal static string NormalizeId(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

}
