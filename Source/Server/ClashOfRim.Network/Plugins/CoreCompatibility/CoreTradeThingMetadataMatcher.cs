using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Plugins.CoreCompatibility;

internal sealed class CoreTradeThingMetadataMatcher : ITradeThingMetadataMatcher
{
    private const string TargetBookSkillDefNameKey = "clashofrim.core.targetBookSkillDefName";
    private const string BookSkillDefNamesKey = "clashofrim.core.bookSkillDefNames";
    private const string TargetResearchProjectDefNameKey = "clashofrim.core.targetResearchProjectDefName";
    private const string ResearchProjectDefNameKey = "clashofrim.core.researchProjectDefName";
    private const string SourceLabelsKey = "clashofrim.core.sourceLabels";

    public int RequirementStrictness(ThingReferenceDto requirement)
    {
        int bookRank = string.IsNullOrWhiteSpace(TargetBookSkillDefName(requirement)) ? 0 : 750_000;
        int researchProjectRank = string.IsNullOrWhiteSpace(TargetResearchProjectDefName(requirement)) ? 0 : 650_000;
        return bookRank + researchProjectRank;
    }

    public bool Matches(ThingReferenceDto requirement, ThingReferenceDto candidate)
    {
        return BookRequirementMatches(requirement, candidate)
            && ResearchProjectRequirementMatches(requirement, candidate);
    }

    public IReadOnlyList<string> DescribeConstraints(ThingReferenceDto requirement)
    {
        var constraints = new List<string>();
        string? targetBookSkill = TargetBookSkillDefName(requirement);
        if (!string.IsNullOrWhiteSpace(targetBookSkill))
        {
            constraints.Add(ServerLocalization.Text(
                "Trade.RequirementTargetBookSkill",
                new Dictionary<string, string?> { ["SKILL"] = targetBookSkill }));
        }

        string? targetResearchProject = TargetResearchProjectDefName(requirement);
        if (!string.IsNullOrWhiteSpace(targetResearchProject))
        {
            constraints.Add(ServerLocalization.Text(
                "Trade.RequirementTargetResearchProject",
                new Dictionary<string, string?> { ["PROJECT"] = targetResearchProject }));
        }

        IReadOnlyList<string> sourceLabels = SourceLabels(requirement);
        if (sourceLabels.Count > 0)
        {
            constraints.Add(ServerLocalization.Text(
                "Trade.RequirementSourceLabels",
                new Dictionary<string, string?> { ["SOURCES"] = string.Join(", ", sourceLabels.Take(3)) }));
        }

        return constraints;
    }

    private static bool BookRequirementMatches(ThingReferenceDto requirement, ThingReferenceDto candidate)
    {
        string? targetBookSkill = TargetBookSkillDefName(requirement);
        if (string.IsNullOrWhiteSpace(targetBookSkill))
        {
            return true;
        }

        return CandidateBookSkillDefNames(candidate)
            .Any(skillDefName => string.Equals(skillDefName, targetBookSkill, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ResearchProjectRequirementMatches(ThingReferenceDto requirement, ThingReferenceDto candidate)
    {
        string? targetResearchProject = TargetResearchProjectDefName(requirement);
        if (string.IsNullOrWhiteSpace(targetResearchProject))
        {
            return true;
        }

        return string.Equals(
            targetResearchProject,
            ResearchProjectDefName(candidate),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? TargetBookSkillDefName(ThingReferenceDto reference)
    {
        return MetadataValue(reference, TargetBookSkillDefNameKey);
    }

    private static IEnumerable<string> CandidateBookSkillDefNames(ThingReferenceDto? requirement)
    {
        string? metadataValue = requirement is null ? null : MetadataValue(requirement, BookSkillDefNamesKey);
        if (!string.IsNullOrWhiteSpace(metadataValue))
        {
            return metadataValue!
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(skill => skill.Trim())
                .Where(skill => !string.IsNullOrWhiteSpace(skill))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        return Array.Empty<string>();
    }

    private static string? TargetResearchProjectDefName(ThingReferenceDto reference)
    {
        return MetadataValue(reference, TargetResearchProjectDefNameKey);
    }

    private static string? ResearchProjectDefName(ThingReferenceDto? reference)
    {
        return reference is null ? null : MetadataValue(reference, ResearchProjectDefNameKey);
    }

    private static IReadOnlyList<string> SourceLabels(ThingReferenceDto? reference)
    {
        if (reference is null)
        {
            return Array.Empty<string>();
        }

        string? metadataValue = MetadataValue(reference, SourceLabelsKey);
        if (string.IsNullOrWhiteSpace(metadataValue))
        {
            return Array.Empty<string>();
        }

        return metadataValue!
            .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(label => label.Trim())
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string? MetadataValue(ThingReferenceDto reference, string key)
    {
        return reference.Metadata is not null && reference.Metadata.TryGetValue(key, out string? value)
            ? value
            : null;
    }
}
