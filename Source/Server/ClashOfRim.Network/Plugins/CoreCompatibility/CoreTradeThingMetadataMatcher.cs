using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Plugins.CoreCompatibility;

internal sealed class CoreTradeThingMetadataMatcher : ITradeThingMetadataMatcher
{
    private const string TargetBookSkillDefNameKey = "clashofrim.core.targetBookSkillDefName";
    private const string BookSkillDefNamesKey = "clashofrim.core.bookSkillDefNames";
    private const string TargetResearchProjectDefNameKey = "clashofrim.core.targetResearchProjectDefName";
    private const string ResearchProjectDefNameKey = "clashofrim.core.researchProjectDefName";
    private const string SourceLabelsKey = "clashofrim.core.sourceLabels";
    private const string PawnGenderKey = "clashofrim.pawn.gender";
    private const string PawnBiologicalAgeTicksKey = "clashofrim.pawn.biologicalAgeTicks";
    private const string PawnMinBiologicalAgeYearsKey = "clashofrim.pawn.minBiologicalAgeYears";
    private const string PawnMaxBiologicalAgeYearsKey = "clashofrim.pawn.maxBiologicalAgeYears";
    private const long PawnTicksPerYear = 3600000L;

    public int RequirementStrictness(ThingReferenceDto requirement)
    {
        int bookRank = string.IsNullOrWhiteSpace(TargetBookSkillDefName(requirement)) ? 0 : 750_000;
        int researchProjectRank = string.IsNullOrWhiteSpace(TargetResearchProjectDefName(requirement)) ? 0 : 650_000;
        int pawnRank = PawnRequirementStrictness(requirement);
        return bookRank + researchProjectRank + pawnRank;
    }

    public bool Matches(ThingReferenceDto requirement, ThingReferenceDto candidate)
    {
        return BookRequirementMatches(requirement, candidate)
            && ResearchProjectRequirementMatches(requirement, candidate)
            && PawnRequirementMatches(requirement, candidate);
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

        constraints.AddRange(PawnRequirementConstraints(requirement));

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

    private static bool PawnRequirementMatches(ThingReferenceDto requirement, ThingReferenceDto candidate)
    {
        string? requiredGender = MetadataValue(requirement, PawnGenderKey);
        if (!string.IsNullOrWhiteSpace(requiredGender)
            && !string.Equals(requiredGender, CandidatePawnGender(candidate), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int? minAge = MetadataInt(requirement, PawnMinBiologicalAgeYearsKey);
        int? maxAge = MetadataInt(requirement, PawnMaxBiologicalAgeYearsKey);
        if (!minAge.HasValue && !maxAge.HasValue)
        {
            return true;
        }

        int? candidateAge = CandidatePawnAgeYears(candidate);
        return candidateAge.HasValue
            && (!minAge.HasValue || candidateAge.Value >= minAge.Value)
            && (!maxAge.HasValue || candidateAge.Value <= maxAge.Value);
    }

    private static string? CandidatePawnGender(ThingReferenceDto candidate)
    {
        return candidate.PawnPackage?.Identity?.Gender
            ?? MetadataValue(candidate, PawnGenderKey);
    }

    private static int? CandidatePawnAgeYears(ThingReferenceDto candidate)
    {
        long? ticks = candidate.PawnPackage?.Status?.BiologicalAgeTicks;
        if (!ticks.HasValue)
        {
            string? metadataTicks = MetadataValue(candidate, PawnBiologicalAgeTicksKey);
            if (long.TryParse(metadataTicks, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long parsedTicks))
            {
                ticks = parsedTicks;
            }
        }

        return ticks.HasValue && ticks.Value >= 0
            ? (int)Math.Floor(ticks.Value / (double)PawnTicksPerYear)
            : null;
    }

    private static int PawnRequirementStrictness(ThingReferenceDto requirement)
    {
        int strictness = 0;
        if (!string.IsNullOrWhiteSpace(MetadataValue(requirement, PawnGenderKey)))
        {
            strictness += 150_000;
        }

        if (MetadataInt(requirement, PawnMinBiologicalAgeYearsKey).HasValue)
        {
            strictness += 75_000;
        }

        if (MetadataInt(requirement, PawnMaxBiologicalAgeYearsKey).HasValue)
        {
            strictness += 75_000;
        }

        return strictness;
    }

    private static IEnumerable<string> PawnRequirementConstraints(ThingReferenceDto requirement)
    {
        string? gender = MetadataValue(requirement, PawnGenderKey);
        if (!string.IsNullOrWhiteSpace(gender))
        {
            yield return ServerLocalization.Text(
                "Trade.RequirementPawnGender",
                new Dictionary<string, string?> { ["GENDER"] = gender });
        }

        int? minAge = MetadataInt(requirement, PawnMinBiologicalAgeYearsKey);
        int? maxAge = MetadataInt(requirement, PawnMaxBiologicalAgeYearsKey);
        if (minAge.HasValue && maxAge.HasValue)
        {
            yield return ServerLocalization.Text(
                "Trade.RequirementPawnAgeRange",
                new Dictionary<string, string?>
                {
                    ["MIN"] = minAge.Value.ToString(),
                    ["MAX"] = maxAge.Value.ToString()
                });
        }
        else if (minAge.HasValue)
        {
            yield return ServerLocalization.Text(
                "Trade.RequirementPawnAgeMin",
                new Dictionary<string, string?> { ["AGE"] = minAge.Value.ToString() });
        }
        else if (maxAge.HasValue)
        {
            yield return ServerLocalization.Text(
                "Trade.RequirementPawnAgeMax",
                new Dictionary<string, string?> { ["AGE"] = maxAge.Value.ToString() });
        }
    }

    private static int? MetadataInt(ThingReferenceDto reference, string key)
    {
        string? value = MetadataValue(reference, key);
        return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    private static string? MetadataValue(ThingReferenceDto reference, string key)
    {
        return reference.Metadata is not null && reference.Metadata.TryGetValue(key, out string? value)
            ? value
            : null;
    }
}
