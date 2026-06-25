using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;

internal sealed class BiotechTradeThingMetadataMatcher : ITradeThingMetadataMatcher
{
    public const string GeneDefNamesKey = "rimworld.biotech.geneDefNames";
    public const string TargetGeneDefNameKey = "rimworld.biotech.targetGeneDefName";
    public const string XenotypeNameKey = "rimworld.biotech.xenotypeName";
    public const string XenotypeIconDefNameKey = "rimworld.biotech.xenotypeIconDefName";
    public const string ReproductiveSourcePrefix = "rimworld.biotech.reproductiveSource.";
    public const string ReproductiveSourceCountKey = ReproductiveSourcePrefix + "count";
    public const string ReproductiveSourceRoleField = "role";
    public const string ReproductiveSourceRaceDefField = "raceDef";
    public const string ReproductiveSourceEndogeneDefNamesField = "endogeneDefNames";
    public const string ReproductiveSourceXenogeneDefNamesField = "xenogeneDefNames";

    public int RequirementStrictness(ThingReferenceDto requirement)
    {
        int strictness = TargetGeneDefNames(requirement).Count == 0 ? 0 : 1_000_000;
        if (ReproductiveSources(requirement).Count > 0)
        {
            strictness += 900_000;
        }

        return strictness;
    }

    public bool Matches(ThingReferenceDto requirement, ThingReferenceDto candidate)
    {
        IReadOnlyList<string> targetGenes = TargetGeneDefNames(requirement);
        if (targetGenes.Count > 0)
        {
            IReadOnlyList<string> candidateGenes = CandidateGeneDefNames(candidate).ToList();
            if (!targetGenes.All(targetGene =>
                    candidateGenes.Any(geneDefName => string.Equals(
                        geneDefName,
                        targetGene,
                        StringComparison.OrdinalIgnoreCase))))
            {
                return false;
            }
        }

        return ReproductiveSourceRequirementMatches(requirement, candidate);
    }

    public IReadOnlyList<string> DescribeConstraints(ThingReferenceDto requirement)
    {
        IReadOnlyList<string> targetGenes = TargetGeneDefNames(requirement);
        IReadOnlyList<ReproductiveSourceRecord> sources = ReproductiveSources(requirement);
        if (targetGenes.Count == 0 && sources.Count == 0)
        {
            return Array.Empty<string>();
        }

        List<string> constraints = new();
        if (targetGenes.Count > 0)
        {
            constraints.Add(ServerLocalization.Text(
                "Trade.RequirementTargetGene",
                new Dictionary<string, string?> { ["GENE"] = string.Join(", ", targetGenes) }));
        }

        if (sources.Count > 0)
        {
            constraints.Add(ServerLocalization.Text(
                "Trade.RequirementBiotechReproductiveSource",
                new Dictionary<string, string?>
                {
                    ["SOURCE"] = string.Join(", ", sources.Select(SourceConstraintLabel))
                }));
        }

        return constraints;
    }

    private static IReadOnlyList<string> TargetGeneDefNames(ThingReferenceDto reference)
    {
        return MetadataList(reference, TargetGeneDefNameKey);
    }

    private static IEnumerable<string> CandidateGeneDefNames(ThingReferenceDto reference)
    {
        return MetadataList(reference, GeneDefNamesKey);
    }

    private static bool ReproductiveSourceRequirementMatches(ThingReferenceDto requirement, ThingReferenceDto candidate)
    {
        IReadOnlyList<ReproductiveSourceRecord> requiredSources = ReproductiveSources(requirement);
        if (requiredSources.Count == 0)
        {
            return true;
        }

        IReadOnlyList<ReproductiveSourceRecord> candidateSources = ReproductiveSources(candidate);
        if (candidateSources.Count == 0)
        {
            return false;
        }

        foreach (ReproductiveSourceRecord required in requiredSources)
        {
            if (!candidateSources.Any(candidateSource => ReproductiveSourceRecordMatches(required, candidateSource)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ReproductiveSourceRecordMatches(ReproductiveSourceRecord required, ReproductiveSourceRecord candidate)
    {
        return MetadataTextMatches(required.Role, candidate.Role)
            && MetadataTextMatches(required.RaceDefName, candidate.RaceDefName)
            && MetadataListMatches(required.EndogeneDefNames, candidate.EndogeneDefNames)
            && MetadataListMatches(required.XenogeneDefNames, candidate.XenogeneDefNames);
    }

    private static bool MetadataTextMatches(string? required, string? candidate)
    {
        return string.IsNullOrWhiteSpace(required)
            || string.Equals(required, candidate, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MetadataListMatches(IReadOnlyList<string> required, IReadOnlyList<string> candidate)
    {
        if (required.Count == 0)
        {
            return true;
        }

        return required.Count == candidate.Count
            && required.All(requiredValue => candidate.Any(candidateValue => string.Equals(
                requiredValue,
                candidateValue,
                StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlyList<ReproductiveSourceRecord> ReproductiveSources(ThingReferenceDto reference)
    {
        string? countText = MetadataValue(reference, ReproductiveSourceCountKey);
        if (!int.TryParse(countText, out int count) || count <= 0)
        {
            return Array.Empty<ReproductiveSourceRecord>();
        }

        count = Math.Min(count, 4);
        var records = new List<ReproductiveSourceRecord>(count);
        for (int i = 0; i < count; i++)
        {
            var record = new ReproductiveSourceRecord(
                Role: MetadataValue(reference, ReproductiveSourceKey(i, ReproductiveSourceRoleField)),
                RaceDefName: MetadataValue(reference, ReproductiveSourceKey(i, ReproductiveSourceRaceDefField)),
                EndogeneDefNames: MetadataList(reference, ReproductiveSourceKey(i, ReproductiveSourceEndogeneDefNamesField)),
                XenogeneDefNames: MetadataList(reference, ReproductiveSourceKey(i, ReproductiveSourceXenogeneDefNamesField)));
            if (!string.IsNullOrWhiteSpace(record.Role)
                || !string.IsNullOrWhiteSpace(record.RaceDefName)
                || record.EndogeneDefNames.Count > 0
                || record.XenogeneDefNames.Count > 0)
            {
                records.Add(record);
            }
        }

        return records;
    }

    private static string SourceConstraintLabel(ReproductiveSourceRecord source)
    {
        string role = string.IsNullOrWhiteSpace(source.Role) ? "source" : source.Role!;
        string race = string.IsNullOrWhiteSpace(source.RaceDefName) ? "unknown" : source.RaceDefName!;
        return role + ":" + race;
    }

    private static string ReproductiveSourceKey(int index, string field)
    {
        return ReproductiveSourcePrefix + index + "." + field;
    }

    private static IReadOnlyList<string> MetadataList(ThingReferenceDto reference, string key)
    {
        string? metadataGenes = MetadataValue(reference, key);
        if (string.IsNullOrWhiteSpace(metadataGenes))
        {
            return Array.Empty<string>();
        }

        return metadataGenes!
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(gene => gene.Trim())
            .Where(gene => !string.IsNullOrWhiteSpace(gene))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? MetadataValue(ThingReferenceDto reference, string key)
    {
        return reference.Metadata is not null && reference.Metadata.TryGetValue(key, out string? value)
            ? value
            : null;
    }

    private readonly record struct ReproductiveSourceRecord(
        string? Role,
        string? RaceDefName,
        IReadOnlyList<string> EndogeneDefNames,
        IReadOnlyList<string> XenogeneDefNames);
}
