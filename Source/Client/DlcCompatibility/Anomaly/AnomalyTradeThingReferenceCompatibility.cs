using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class AnomalyTradeThingReferenceCompatibility
{
    internal const string AnomalyResearchBookKind = "AnomalyResearch";
    private const string MetadataBookKind = "clashofrim.anomaly.bookKind";

    internal static bool HasRemoteStateGuards =>
        ClashOfRimCompatibilityApi.HasCompatibilityCapability(AnomalyCompatibilityKeys.RemoteStateGuards);

    internal static bool HasTradeMetadata =>
        ClashOfRimCompatibilityApi.HasCompatibilityCapability(AnomalyCompatibilityKeys.TradeMetadata);

    internal static void ApplyDefaultThingReferenceMetadata(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        if (!HasTradeMetadata || item is null || !IsAnomalyResearchBookDef(def))
        {
            return;
        }

        SetRequiresAnomalyResearchBook(item, true);
    }

    internal static void AppendThingReferenceMetadata(Thing metadataThing, ModThingReferenceDto reference)
    {
        if (!HasTradeMetadata
            || metadataThing is not Book book
            || !book.BookComp.TryGetDoer<ReadingOutcomeDoerGainAnomalyResearch>(out _))
        {
            return;
        }

        reference.Metadata ??= new Dictionary<string, string?>(StringComparer.Ordinal);
        reference.Metadata[MetadataBookKind] = AnomalyResearchBookKind;
    }

    internal static bool ThingReferenceMatches(ModThingReferenceDto requirement, Thing metadataThing)
    {
        if (!RequiresAnomalyResearchBook(requirement))
        {
            return true;
        }

        return HasTradeMetadata
            && metadataThing is Book book
            && book.BookComp.TryGetDoer<ReadingOutcomeDoerGainAnomalyResearch>(out _);
    }

    internal static bool ThingReferenceDtoMatches(ModThingReferenceDto requirement, ModThingReferenceDto candidate)
    {
        if (!RequiresAnomalyResearchBook(requirement))
        {
            return true;
        }

        return string.Equals(BookKind(candidate), AnomalyResearchBookKind, StringComparison.Ordinal);
    }

    internal static bool TryApplyThingReferenceMetadata(ModThingReferenceDto reference, Thing thing, out string? missingDefName)
    {
        missingDefName = null;
        return !RequiresAnomalyResearchBook(reference)
            || (thing is Book book && book.BookComp.TryGetDoer<ReadingOutcomeDoerGainAnomalyResearch>(out _));
    }

    internal static int ThingReferenceStrictness(ModThingReferenceDto requirement)
    {
        return RequiresAnomalyResearchBook(requirement) ? 700_000 : 0;
    }

    internal static void AppendThingReferenceDisplayParts(ModThingReferenceDto thing, bool asRequirement, List<string> parts)
    {
        if (parts is null || !string.Equals(BookKind(thing), AnomalyResearchBookKind, StringComparison.Ordinal))
        {
            return;
        }

        parts.Add(ClashOfRimText.Key("ClashOfRim.Trade.BookKindAnomalyResearch"));
    }

    internal static bool SuppressesStandardThingStats(ThingDef? def)
    {
        return false;
    }

    internal static IEnumerable<string> ThingReferenceCacheKeyParts(ModThingReferenceDto thing)
    {
        yield return BookKind(thing) ?? string.Empty;
    }

    private static bool RequiresAnomalyResearchBook(ModThingReferenceDto? reference)
    {
        return string.Equals(BookKind(reference), AnomalyResearchBookKind, StringComparison.Ordinal);
    }

    private static void SetRequiresAnomalyResearchBook(ModThingReferenceDto? reference, bool value)
    {
        if (reference is null)
        {
            return;
        }

        if (value)
        {
            reference.Metadata ??= new Dictionary<string, string?>(StringComparer.Ordinal);
            reference.Metadata[MetadataBookKind] = AnomalyResearchBookKind;
            return;
        }

        reference.Metadata?.Remove(MetadataBookKind);
    }

    private static string? BookKind(ModThingReferenceDto? reference)
    {
        return reference?.Metadata is not null
            && reference.Metadata.TryGetValue(MetadataBookKind, out string? value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
    }

    private static bool IsAnomalyResearchBookDef(ThingDef? def)
    {
        if (def is null || !def.HasComp<CompBook>())
        {
            return false;
        }

        try
        {
            return BookUtility.MakeBook(def, ArtGenerationContext.Outsider) is Book book
                && book.BookComp.TryGetDoer<ReadingOutcomeDoerGainAnomalyResearch>(out _);
        }
        catch
        {
            return false;
        }
    }
}
