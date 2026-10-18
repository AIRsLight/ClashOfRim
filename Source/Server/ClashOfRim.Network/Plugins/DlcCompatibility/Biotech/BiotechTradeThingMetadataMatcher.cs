using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;

internal sealed class BiotechTradeThingMetadataMatcher : ITradeThingMetadataMatcher
{
    public const string GeneDefNamesKey = BiotechThingMetadataKeys.GeneDefNames;
    public const string TargetGeneDefNameKey = BiotechThingMetadataKeys.TargetGeneDefNames;
    public const string XenotypeNameKey = BiotechThingMetadataKeys.XenotypeName;
    public const string XenotypeIconDefNameKey = BiotechThingMetadataKeys.XenotypeIconDefName;
    public const string ReproductiveSourcePrefix = BiotechThingMetadataKeys.ReproductiveSourcePrefix;
    public const string ReproductiveSourceCountKey = BiotechThingMetadataKeys.ReproductiveSourceCount;
    public const string ReproductiveSourceRoleField = BiotechThingMetadataKeys.SourceRole;
    public const string ReproductiveSourceRaceDefField = BiotechThingMetadataKeys.SourceRaceDef;
    public const string ReproductiveSourceEndogeneDefNamesField = BiotechThingMetadataKeys.SourceEndogeneDefNames;
    public const string ReproductiveSourceXenogeneDefNamesField = BiotechThingMetadataKeys.SourceXenogeneDefNames;

    public int RequirementStrictness(ThingReferenceDto requirement)
    {
        return BiotechTradeConstraints.RequirementStrictness(requirement.Metadata);
    }

    public bool Matches(ThingReferenceDto requirement, ThingReferenceDto candidate)
    {
        return BiotechTradeConstraints.Matches(requirement.Metadata, candidate.Metadata);
    }

    public IReadOnlyList<string> DescribeConstraints(ThingReferenceDto requirement)
    {
        IReadOnlyList<string> targetGenes = BiotechThingMetadataCodec.ReadList(requirement.Metadata, TargetGeneDefNameKey);
        IReadOnlyList<BiotechSourceConstraint> sources = BiotechTradeConstraints.ReadSources(requirement.Metadata);
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

    private static string SourceConstraintLabel(BiotechSourceConstraint source)
    {
        string role = string.IsNullOrWhiteSpace(source.Role) ? "source" : source.Role!;
        string race = string.IsNullOrWhiteSpace(source.RaceDefName) ? "unknown" : source.RaceDefName!;
        return role + ":" + race;
    }
}
