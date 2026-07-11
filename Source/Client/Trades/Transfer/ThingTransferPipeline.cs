using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using System;
using System.Collections.Generic;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

internal static class ThingTransferPipeline
{
    public static bool CanTransfer(Thing thing, string surface, out string? rejectionCode)
    {
        return ClashOfRimCompatibilityApi.CanTransferThing(
            thing,
            ThingTransferContext.Outbound(surface),
            out rejectionCode);
    }

    public static bool TryPrepareOutbound(
        Thing thing,
        ModThingReferenceDto reference,
        string surface,
        out string? rejectionCode)
    {
        Dictionary<string, string?> metadataBeforeCompatibility = SnapshotMetadata(reference);
        if (!ClashOfRimCompatibilityApi.PrepareThingTransfer(
                thing,
                reference,
                ThingTransferContext.Outbound(surface),
                out rejectionCode))
        {
            return false;
        }

        ClashOfRimCompatibilityApi.AppendThingReferenceMetadata(thing, reference);
        if (!MetadataChanged(metadataBeforeCompatibility, reference.Metadata))
        {
            ThingStatePackageUtility.TryAttachFallbackPackage(thing, reference);
        }

        ThingTransferPolicy.MarkAccepted(reference.Metadata);

        return true;
    }

    public static bool TryFinalizeInbound(
        ModThingReferenceDto reference,
        Thing thing,
        string surface,
        Faction? receivingFaction,
        out string? missingDefName)
    {
        if (!ClashOfRimCompatibilityApi.TryApplyThingReferenceMetadata(reference, thing, out missingDefName))
        {
            return false;
        }

        return ClashOfRimCompatibilityApi.FinalizeThingTransfer(
            reference,
            thing,
            ThingTransferContext.Inbound(surface, receivingFaction),
            out missingDefName);
    }

    public static string RejectionMessage(string? rejectionCode)
    {
        if (!string.IsNullOrWhiteSpace(rejectionCode)
            && rejectionCode!.StartsWith("ClashOfRim.", StringComparison.Ordinal))
        {
            return ClashOfRimText.Key(rejectionCode!);
        }

        return ClashOfRimText.Key("ClashOfRim.ThingTransfer.RejectUnsupported");
    }

    private static Dictionary<string, string?> SnapshotMetadata(ModThingReferenceDto reference)
    {
        return reference.Metadata is null
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : new Dictionary<string, string?>(reference.Metadata, StringComparer.Ordinal);
    }

    private static bool MetadataChanged(
        IReadOnlyDictionary<string, string?> previous,
        IReadOnlyDictionary<string, string?>? current)
    {
        if (current is null)
        {
            return previous.Count > 0;
        }

        if (previous.Count != current.Count)
        {
            return true;
        }

        foreach (KeyValuePair<string, string?> entry in previous)
        {
            if (!current.TryGetValue(entry.Key, out string? value)
                || !string.Equals(entry.Value, value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class ThingTransferRejectedException : InvalidOperationException
{
    public ThingTransferRejectedException(string rejectionCode)
        : base(rejectionCode)
    {
        RejectionCode = rejectionCode;
    }

    public string RejectionCode { get; }
}
