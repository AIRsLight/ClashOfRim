using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;

internal sealed class AnomalyTradeThingMetadataMatcher : ITradeThingMetadataMatcher
{
    private const string BookKindKey = "clashofrim.anomaly.bookKind";
    private const string AnomalyResearchBookKind = "AnomalyResearch";

    public int RequirementStrictness(ThingReferenceDto requirement)
    {
        return RequiresAnomalyResearchBook(requirement) ? 700_000 : 0;
    }

    public bool Matches(ThingReferenceDto requirement, ThingReferenceDto candidate)
    {
        return !RequiresAnomalyResearchBook(requirement)
            || IsAnomalyResearchBook(candidate);
    }

    public IReadOnlyList<string> DescribeConstraints(ThingReferenceDto requirement)
    {
        return RequiresAnomalyResearchBook(requirement)
            ? new[] { ServerLocalization.Text("Trade.RequirementAnomalyResearchBook") }
            : Array.Empty<string>();
    }

    private static bool RequiresAnomalyResearchBook(ThingReferenceDto? reference)
    {
        return string.Equals(BookKind(reference), AnomalyResearchBookKind, StringComparison.Ordinal);
    }

    private static bool IsAnomalyResearchBook(ThingReferenceDto? reference)
    {
        return string.Equals(BookKind(reference), AnomalyResearchBookKind, StringComparison.Ordinal);
    }

    private static string? BookKind(ThingReferenceDto? reference)
    {
        return reference?.Metadata is not null
            && reference.Metadata.TryGetValue(BookKindKey, out string? value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
    }
}
