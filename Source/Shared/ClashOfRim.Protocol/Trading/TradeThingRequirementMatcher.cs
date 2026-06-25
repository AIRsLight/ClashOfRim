using System;
using System.Collections.Generic;
using System.Linq;

namespace AIRsLight.ClashOfRim.Protocol;

public static class TradeThingRequirementMatcher
{
    private const string WeaponTraitKindMetadataKey = "ClashOfRim.WeaponTraitKind";
    private const string WeaponTraitKindSpecialized = "Specialized";
    private const string WeaponTraitKindPersona = "Persona";

    private static readonly IReadOnlyDictionary<string, int> QualityRank =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Awful"] = 0,
            ["Poor"] = 1,
            ["Normal"] = 2,
            ["Good"] = 3,
            ["Excellent"] = 4,
            ["Masterwork"] = 5,
            ["Legendary"] = 6
        };

    public static bool Satisfies(
        IReadOnlyCollection<ThingReferenceDto> requirements,
        IReadOnlyCollection<ThingReferenceDto> deliveredThings,
        out IReadOnlyList<string> missingRequirements,
        IReadOnlyList<ITradeThingMetadataMatcher>? metadataMatchers = null,
        string? qualityRequirementMode = null,
        string? hitPointsRequirementMode = null)
    {
        metadataMatchers ??= Array.Empty<ITradeThingMetadataMatcher>();
        string normalizedQualityMode = NormalizeQualityRequirementMode(qualityRequirementMode);
        string normalizedHitPointsMode = NormalizeRequirementMode(hitPointsRequirementMode);
        var missing = new List<string>();
        var deliveredStates = new List<DeliveredThingState>(deliveredThings.Count);
        foreach (ThingReferenceDto thing in deliveredThings)
        {
            deliveredStates.Add(new DeliveredThingState(thing, Math.Max(1, thing.StackCount)));
        }

        var sortedRequirements = new List<RequirementState>(requirements.Count);
        foreach (ThingReferenceDto requirement in requirements)
        {
            sortedRequirements.Add(new RequirementState(
                requirement,
                RequirementStrictness(requirement, metadataMatchers, normalizedQualityMode, normalizedHitPointsMode)));
        }

        sortedRequirements.Sort((left, right) => right.Strictness.CompareTo(left.Strictness));
        foreach (RequirementState requirementState in sortedRequirements)
        {
            ThingReferenceDto requirement = requirementState.Requirement;
            int requiredCount = Math.Max(1, requirement.StackCount);
            int deliveredCount = ConsumeMatchingCount(requirement, requiredCount, deliveredStates, metadataMatchers, normalizedQualityMode, normalizedHitPointsMode);
            if (deliveredCount < requiredCount)
            {
                missing.Add(DescribeRequirement(requirement, requiredCount, deliveredCount, metadataMatchers, normalizedQualityMode, normalizedHitPointsMode));
            }
        }

        missingRequirements = missing;
        return missing.Count == 0;
    }

    private static bool MatchesRequirement(
        ThingReferenceDto requirement,
        ThingReferenceDto candidate,
        IReadOnlyList<ITradeThingMetadataMatcher> metadataMatchers,
        string qualityRequirementMode,
        string hitPointsRequirementMode)
    {
        if (string.IsNullOrWhiteSpace(requirement.DefName)
            || !string.Equals(requirement.DefName, candidate.DefName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(requirement.MinifiedInnerDefName)
            && !string.Equals(requirement.MinifiedInnerDefName, candidate.MinifiedInnerDefName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!QualityMatches(candidate.MinifiedInnerQuality ?? candidate.Quality, requirement.MinifiedInnerQuality ?? requirement.Quality, qualityRequirementMode))
        {
            return false;
        }

        if (!StuffMatches(requirement.MinifiedInnerStuffDefName ?? requirement.StuffDefName, candidate.MinifiedInnerStuffDefName ?? candidate.StuffDefName))
        {
            return false;
        }

        if (!UniqueWeaponMatches(requirement, candidate))
        {
            return false;
        }

        foreach (ITradeThingMetadataMatcher matcher in metadataMatchers)
        {
            if (!SafeMatcherMatches(matcher, requirement, candidate))
            {
                return false;
            }
        }

        if (!HitPointsMatches(
                EffectiveHitPoints(candidate),
                EffectiveMaxHitPoints(candidate),
                EffectiveHitPoints(requirement),
                EffectiveMaxHitPoints(requirement),
                hitPointsRequirementMode))
        {
            return false;
        }

        return true;
    }

    private static int ConsumeMatchingCount(
        ThingReferenceDto requirement,
        int requiredCount,
        IReadOnlyList<DeliveredThingState> deliveredStates,
        IReadOnlyList<ITradeThingMetadataMatcher> metadataMatchers,
        string qualityRequirementMode,
        string hitPointsRequirementMode)
    {
        int deliveredCount = 0;
        foreach (DeliveredThingState state in deliveredStates)
        {
            if (state.RemainingCount <= 0
                || !MatchesRequirement(requirement, state.Thing, metadataMatchers, qualityRequirementMode, hitPointsRequirementMode))
            {
                continue;
            }

            int consumed = Math.Min(requiredCount - deliveredCount, state.RemainingCount);
            state.RemainingCount -= consumed;
            deliveredCount += consumed;
            if (deliveredCount >= requiredCount)
            {
                break;
            }
        }

        return deliveredCount;
    }

    private static int RequirementStrictness(
        ThingReferenceDto requirement,
        IReadOnlyList<ITradeThingMetadataMatcher> metadataMatchers,
        string qualityRequirementMode,
        string hitPointsRequirementMode)
    {
        string? quality = requirement.MinifiedInnerQuality ?? requirement.Quality;
        int qualityRank = !string.IsNullOrWhiteSpace(quality)
            && QualityRank.TryGetValue(quality!, out int rank)
                ? string.Equals(qualityRequirementMode, "AtMost", StringComparison.Ordinal) ? 6 - rank : rank
                : -1;
        int hitPoints = DisplayedHitPointsPercentOrRaw(EffectiveHitPoints(requirement), EffectiveMaxHitPoints(requirement)) ?? -1;
        if (string.Equals(hitPointsRequirementMode, "AtMost", StringComparison.Ordinal) && hitPoints >= 0)
        {
            hitPoints = 1_000_000 - hitPoints;
        }

        int metadataRank = metadataMatchers.Sum(matcher => SafeMatcherStrictness(matcher, requirement));
        int uniqueWeaponRank = requirement.UniqueWeapon == true
            ? 100_000 + (requirement.UniqueWeaponTraits?.Count ?? 0) * 10_000
            : 0;
        int stuffRank = string.IsNullOrWhiteSpace(requirement.MinifiedInnerStuffDefName ?? requirement.StuffDefName) ? 0 : 250_000;
        return metadataRank + uniqueWeaponRank + stuffRank + qualityRank * 1000 + hitPoints;
    }

    private static bool QualityAtLeast(string? candidate, string? requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(candidate)
            && QualityRank.TryGetValue(candidate!, out int candidateRank)
            && QualityRank.TryGetValue(requirement!, out int requirementRank)
            && candidateRank >= requirementRank;
    }

    private static bool QualityAtMost(string? candidate, string? requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(candidate)
            && QualityRank.TryGetValue(candidate!, out int candidateRank)
            && QualityRank.TryGetValue(requirement!, out int requirementRank)
            && candidateRank <= requirementRank;
    }

    private static bool QualityMatches(string? candidate, string? requirement, string qualityRequirementMode)
    {
        return string.Equals(qualityRequirementMode, "AtMost", StringComparison.Ordinal)
            ? QualityAtMost(candidate, requirement)
            : QualityAtLeast(candidate, requirement);
    }

    private static string NormalizeQualityRequirementMode(string? qualityRequirementMode)
    {
        return NormalizeRequirementMode(qualityRequirementMode);
    }

    private static bool HitPointsMatches(int? candidate, int? candidateMax, int? requirement, int? requirementMax, string hitPointsRequirementMode)
    {
        if (!requirement.HasValue)
        {
            return true;
        }

        if (!candidate.HasValue)
        {
            return false;
        }

        int candidateComparable = DisplayedHitPointsPercentOrRaw(candidate, candidateMax) ?? candidate.Value;
        int requirementComparable = DisplayedHitPointsPercentOrRaw(requirement, requirementMax) ?? requirement.Value;
        return string.Equals(hitPointsRequirementMode, "AtMost", StringComparison.Ordinal)
            ? candidateComparable <= requirementComparable
            : candidateComparable >= requirementComparable;
    }

    private static int? EffectiveHitPoints(ThingReferenceDto reference)
    {
        return reference.MinifiedInnerHitPoints ?? reference.HitPoints;
    }

    private static int? EffectiveMaxHitPoints(ThingReferenceDto reference)
    {
        return reference.MinifiedInnerMaxHitPoints ?? reference.MaxHitPoints;
    }

    private static int? DisplayedHitPointsPercentOrRaw(int? hitPoints, int? maxHitPoints)
    {
        if (!hitPoints.HasValue)
        {
            return null;
        }

        if (!maxHitPoints.HasValue || maxHitPoints.Value <= 0)
        {
            return hitPoints.Value;
        }

        int percent = (int)Math.Round(hitPoints.Value * 100.0 / Math.Max(1, maxHitPoints.Value));
        return Math.Max(1, Math.Min(100, percent));
    }

    private static string NormalizeRequirementMode(string? mode)
    {
        return string.Equals(mode, "AtMost", StringComparison.Ordinal)
            ? "AtMost"
            : "AtLeast";
    }

    private static bool StuffMatches(string? requiredStuffDefName, string? candidateStuffDefName)
    {
        return string.IsNullOrWhiteSpace(requiredStuffDefName)
            || string.Equals(requiredStuffDefName, candidateStuffDefName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool UniqueWeaponMatches(ThingReferenceDto requirement, ThingReferenceDto candidate)
    {
        if (requirement.UniqueWeapon != true)
        {
            return true;
        }

        HashSet<string> candidateTraits = new(candidate.UniqueWeaponTraits ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return candidate.UniqueWeapon == true
            && (requirement.UniqueWeaponTraits ?? Array.Empty<string>())
                .Where(trait => !string.IsNullOrWhiteSpace(trait))
                .All(candidateTraits.Contains);
    }

    private static string DescribeRequirement(
        ThingReferenceDto requirement,
        int requiredCount,
        int deliveredCount,
        IReadOnlyList<ITradeThingMetadataMatcher> metadataMatchers,
        string qualityRequirementMode,
        string hitPointsRequirementMode)
    {
        string defName = string.IsNullOrWhiteSpace(requirement.DefName)
            ? ServerLocalization.Text("Trade.RequirementUnknownThing")
            : requirement.DefName!;
        var constraints = new List<string>();
        if (!string.IsNullOrWhiteSpace(requirement.Quality))
        {
            constraints.Add(ServerLocalization.Text(
                string.Equals(qualityRequirementMode, "AtMost", StringComparison.Ordinal)
                    ? "Trade.RequirementQualityAtMost"
                    : "Trade.RequirementQualityAtLeast",
                new Dictionary<string, string?> { ["QUALITY"] = requirement.Quality }));
        }

        int? displayedHitPoints = DisplayedHitPointsPercentOrRaw(EffectiveHitPoints(requirement), EffectiveMaxHitPoints(requirement));
        if (displayedHitPoints.HasValue)
        {
            constraints.Add(ServerLocalization.Text(
                string.Equals(hitPointsRequirementMode, "AtMost", StringComparison.Ordinal)
                    ? "Trade.RequirementHitPointsAtMost"
                    : "Trade.RequirementHitPointsAtLeast",
                new Dictionary<string, string?> { ["HITPOINTS"] = displayedHitPoints.Value.ToString() + "%" }));
        }

        constraints.AddRange(metadataMatchers.SelectMany(matcher => SafeMatcherConstraints(matcher, requirement)));

        if (requirement.UniqueWeapon == true)
        {
            IReadOnlyList<string> traits = requirement.UniqueWeaponTraits
                .Where(trait => !string.IsNullOrWhiteSpace(trait))
                .ToList();
            constraints.Add(traits.Count == 0
                ? ServerLocalization.Text(WeaponTraitRequirementKey(requirement, withTraits: false))
                : ServerLocalization.Text(
                    WeaponTraitRequirementKey(requirement, withTraits: true),
                    new Dictionary<string, string?> { ["TRAITS"] = string.Join(", ", traits) }));
        }

        string? stuffDefName = requirement.MinifiedInnerStuffDefName ?? requirement.StuffDefName;
        if (!string.IsNullOrWhiteSpace(stuffDefName))
        {
            constraints.Add(ServerLocalization.Text(
                "Trade.RequirementTargetStuff",
                new Dictionary<string, string?> { ["STUFF"] = stuffDefName }));
        }

        string suffix = constraints.Count == 0
            ? string.Empty
            : ServerLocalization.Text(
                "Trade.RequirementConstraints",
                new Dictionary<string, string?>
                {
                    ["CONSTRAINTS"] = string.Join(
                        ServerLocalization.Text("Trade.RequirementConstraintSeparator"),
                        constraints)
                });
        return ServerLocalization.Text(
            "Trade.RequirementDescription",
            new Dictionary<string, string?>
            {
                ["DEF"] = defName,
                ["REQUIRED"] = requiredCount.ToString(),
                ["CONSTRAINTS"] = suffix,
                ["DELIVERED"] = deliveredCount.ToString()
            });
    }

    private static string WeaponTraitRequirementKey(ThingReferenceDto requirement, bool withTraits)
    {
        string? kind = requirement.Metadata.TryGetValue(WeaponTraitKindMetadataKey, out string? value)
            ? value
            : null;
        return kind switch
        {
            WeaponTraitKindPersona => withTraits ? "Trade.RequirementPersonaWeaponTraits" : "Trade.RequirementPersonaWeapon",
            WeaponTraitKindSpecialized => withTraits ? "Trade.RequirementSpecializedWeaponTraits" : "Trade.RequirementSpecializedWeapon",
            _ => withTraits ? "Trade.RequirementTraitWeaponTraits" : "Trade.RequirementTraitWeapon"
        };
    }

    private sealed class DeliveredThingState
    {
        public DeliveredThingState(ThingReferenceDto thing, int remainingCount)
        {
            Thing = thing;
            RemainingCount = remainingCount;
        }

        public ThingReferenceDto Thing { get; }

        public int RemainingCount { get; set; }
    }

    private sealed class RequirementState
    {
        public RequirementState(ThingReferenceDto requirement, int strictness)
        {
            Requirement = requirement;
            Strictness = strictness;
        }

        public ThingReferenceDto Requirement { get; }

        public int Strictness { get; }
    }

    private static bool SafeMatcherMatches(
        ITradeThingMetadataMatcher matcher,
        ThingReferenceDto requirement,
        ThingReferenceDto candidate)
    {
        try
        {
            return matcher.Matches(requirement, candidate);
        }
        catch (Exception ex)
        {
            LogMatcherException(nameof(ITradeThingMetadataMatcher.Matches), matcher, ex);
            return false;
        }
    }

    private static int SafeMatcherStrictness(ITradeThingMetadataMatcher matcher, ThingReferenceDto requirement)
    {
        try
        {
            return matcher.RequirementStrictness(requirement);
        }
        catch (Exception ex)
        {
            LogMatcherException(nameof(ITradeThingMetadataMatcher.RequirementStrictness), matcher, ex);
            return 0;
        }
    }

    private static IEnumerable<string> SafeMatcherConstraints(ITradeThingMetadataMatcher matcher, ThingReferenceDto requirement)
    {
        try
        {
            return matcher.DescribeConstraints(requirement) ?? Array.Empty<string>();
        }
        catch (Exception ex)
        {
            LogMatcherException(nameof(ITradeThingMetadataMatcher.DescribeConstraints), matcher, ex);
            return Array.Empty<string>();
        }
    }

    private static void LogMatcherException(string operation, ITradeThingMetadataMatcher matcher, Exception ex)
    {
        Console.Error.WriteLine(
            "[ClashOfRim][ServerPlugin][Error] TradeThingMetadataMatcherCallbackException: "
            + matcher.GetType().FullName
            + " failed during "
            + operation
            + ": "
            + ex.GetType().Name
            + " "
            + ex.Message);
    }
}
