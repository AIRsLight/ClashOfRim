using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;

internal sealed class BiotechTradeThingMetadataMatcher : ITradeThingMetadataMatcher
{
    public const string GeneDefNamesKey = "rimworld.biotech.geneDefNames";
    public const string TargetGeneDefNameKey = "rimworld.biotech.targetGeneDefName";
    public const string XenotypeNameKey = "rimworld.biotech.xenotypeName";
    public const string XenotypeIconDefNameKey = "rimworld.biotech.xenotypeIconDefName";

    public int RequirementStrictness(ThingReferenceDto requirement)
    {
        return TargetGeneDefNames(requirement).Count == 0 ? 0 : 1_000_000;
    }

    public bool Matches(ThingReferenceDto requirement, ThingReferenceDto candidate)
    {
        IReadOnlyList<string> targetGenes = TargetGeneDefNames(requirement);
        if (targetGenes.Count == 0)
        {
            return true;
        }

        IReadOnlyList<string> candidateGenes = CandidateGeneDefNames(candidate).ToList();
        return targetGenes.All(targetGene =>
            candidateGenes.Any(geneDefName => string.Equals(
                geneDefName,
                targetGene,
                StringComparison.OrdinalIgnoreCase)));
    }

    public IReadOnlyList<string> DescribeConstraints(ThingReferenceDto requirement)
    {
        IReadOnlyList<string> targetGenes = TargetGeneDefNames(requirement);
        if (targetGenes.Count == 0)
        {
            return Array.Empty<string>();
        }

        return new[]
        {
            ServerLocalization.Text(
                "Trade.RequirementTargetGene",
                new Dictionary<string, string?> { ["GENE"] = string.Join(", ", targetGenes) })
        };
    }

    private static IReadOnlyList<string> TargetGeneDefNames(ThingReferenceDto reference)
    {
        return MetadataList(reference, TargetGeneDefNameKey);
    }

    private static IEnumerable<string> CandidateGeneDefNames(ThingReferenceDto reference)
    {
        return MetadataList(reference, GeneDefNamesKey);
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
}
