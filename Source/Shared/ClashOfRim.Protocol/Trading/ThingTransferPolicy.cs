using System;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Protocol;

public static class ThingTransferPolicy
{
    public const string VersionMetadataKey = "clashofrim.transfer.policyVersion";
    public const string DecisionMetadataKey = "clashofrim.transfer.decision";
    public const string CurrentVersion = "1";
    public const string AcceptedDecision = "accepted";

    public static Dictionary<string, string?> AcceptedMetadata()
    {
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [VersionMetadataKey] = CurrentVersion,
            [DecisionMetadataKey] = AcceptedDecision
        };
    }

    public static void MarkAccepted(IDictionary<string, string?> metadata)
    {
        metadata[VersionMetadataKey] = CurrentVersion;
        metadata[DecisionMetadataKey] = AcceptedDecision;
    }

    public static bool IsAcceptedConcreteReference(ThingReferenceDto reference, out string failure)
    {
        failure = string.Empty;
        if (!IsConcreteThingReference(reference))
        {
            return true;
        }

        if (!reference.Metadata.TryGetValue(VersionMetadataKey, out string? version)
            || !string.Equals(version, CurrentVersion, StringComparison.Ordinal))
        {
            failure = "missing or unsupported transfer policy version";
            return false;
        }

        if (!reference.Metadata.TryGetValue(DecisionMetadataKey, out string? decision)
            || !string.Equals(decision, AcceptedDecision, StringComparison.Ordinal))
        {
            failure = "thing transfer preprocessing was not accepted";
            return false;
        }

        return true;
    }

    public static bool IsConcreteThingReference(ThingReferenceDto reference)
    {
        string key = reference.GlobalKey ?? string.Empty;
        return key.StartsWith("owner:", StringComparison.Ordinal)
            && key.IndexOf("/thing:", StringComparison.Ordinal) >= 0;
    }
}
