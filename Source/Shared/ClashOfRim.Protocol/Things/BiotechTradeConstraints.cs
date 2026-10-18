using System;
using System.Collections.Generic;
using System.Linq;

namespace AIRsLight.ClashOfRim.Protocol;

public static class BiotechTradeConstraints
{
    public static int RequirementStrictness(IReadOnlyDictionary<string, string?>? metadata)
    {
        int strictness = BiotechThingMetadataCodec.ReadList(metadata, BiotechThingMetadataKeys.TargetGeneDefNames).Count > 0 ? 1_000_000 : 0;
        return strictness + (ReadSources(metadata).Count > 0 ? 900_000 : 0);
    }

    public static bool Matches(IReadOnlyDictionary<string, string?>? requirement, IReadOnlyDictionary<string, string?>? candidate)
    {
        return MatchesGenes(requirement, BiotechThingMetadataCodec.ReadList(candidate, BiotechThingMetadataKeys.GeneDefNames))
            && MatchesSources(requirement, ReadSources(candidate));
    }

    public static bool MatchesGenes(IReadOnlyDictionary<string, string?>? requirement, IReadOnlyCollection<string>? candidateGenes)
    {
        IReadOnlyList<string> required = BiotechThingMetadataCodec.ReadList(requirement, BiotechThingMetadataKeys.TargetGeneDefNames);
        return required.Count == 0 || (candidateGenes is not null && ContainsAll(candidateGenes, required));
    }

    public static bool MatchesSources(IReadOnlyDictionary<string, string?>? requirement, IReadOnlyList<BiotechSourceConstraint> candidateSources)
    {
        // Each required source must be represented. Repeated requirements do not consume distinct candidates.
        return ReadSources(requirement).All(required => candidateSources.Any(candidate =>
            TextMatches(required.Role, candidate.Role)
            && TextMatches(required.RaceDefName, candidate.RaceDefName)
            && SourceGenesMatch(required.EndogeneDefNames, candidate.EndogeneDefNames)
            && SourceGenesMatch(required.XenogeneDefNames, candidate.XenogeneDefNames)));
    }

    public static IReadOnlyList<BiotechSourceConstraint> ReadSources(IReadOnlyDictionary<string, string?>? metadata)
    {
        int count = BiotechThingMetadataCodec.SourceCount(metadata);
        var sources = new List<BiotechSourceConstraint>(count);
        for (int i = 0; i < count; i++)
        {
            var source = new BiotechSourceConstraint(
                BiotechThingMetadataCodec.ReadText(metadata, BiotechThingMetadataCodec.SourceKey(i, BiotechThingMetadataKeys.SourceRole)),
                BiotechThingMetadataCodec.ReadText(metadata, BiotechThingMetadataCodec.SourceKey(i, BiotechThingMetadataKeys.SourceRaceDef)),
                BiotechThingMetadataCodec.ReadList(metadata, BiotechThingMetadataCodec.SourceKey(i, BiotechThingMetadataKeys.SourceEndogeneDefNames)),
                BiotechThingMetadataCodec.ReadList(metadata, BiotechThingMetadataCodec.SourceKey(i, BiotechThingMetadataKeys.SourceXenogeneDefNames)));
            if (source.HasConstraints)
            {
                sources.Add(source);
            }
        }

        return sources;
    }

    private static bool TextMatches(string? required, string? candidate)
    {
        return string.IsNullOrWhiteSpace(required) || string.Equals(required, candidate, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SourceGenesMatch(IReadOnlyList<string> required, IReadOnlyList<string> candidate)
    {
        // An omitted source gene set is a wildcard; a specified set must match exactly.
        return required.Count == 0 || (required.Count == candidate.Count && ContainsAll(candidate, required));
    }

    private static bool ContainsAll(IReadOnlyCollection<string> candidate, IReadOnlyList<string> required)
    {
        return required.All(value => candidate.Contains(value, StringComparer.OrdinalIgnoreCase));
    }
}
