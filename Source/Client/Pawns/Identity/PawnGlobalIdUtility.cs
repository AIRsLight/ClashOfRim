using System;
using Verse;

namespace AIRsLight.ClashOfRim.Pawns;

internal static class PawnGlobalIdUtility
{
    private const string OwnerPrefix = "owner:";

    public static string Build(string? ownerUserId, Pawn pawn)
    {
        return Build(ownerUserId, pawn?.ThingID);
    }

    public static string Build(string? ownerUserId, string? localThingId)
    {
        string safeOwner = Segment(ownerUserId);
        string safeLocalId = string.IsNullOrWhiteSpace(localThingId)
            ? "unknown"
            : localThingId!.Trim();
        if (safeLocalId.StartsWith("Thing_", StringComparison.Ordinal))
        {
            safeLocalId = safeLocalId.Substring("Thing_".Length);
        }

        return $"owner:{safeOwner}/pawn:{safeLocalId}";
    }

    public static bool TryExtractOwnerUserId(string? globalId, out string? ownerUserId)
    {
        ownerUserId = null;
        if (string.IsNullOrWhiteSpace(globalId))
        {
            return false;
        }

        string value = globalId!.Trim();
        if (!value.StartsWith(OwnerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        int segmentEnd = value.IndexOf('/', OwnerPrefix.Length);
        if (segmentEnd < 0)
        {
            segmentEnd = value.Length;
        }

        string owner = value.Substring(OwnerPrefix.Length, segmentEnd - OwnerPrefix.Length).Trim();
        if (string.IsNullOrWhiteSpace(owner) || string.Equals(owner, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ownerUserId = owner;
        return true;
    }

    private static string Segment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        return value!.Trim().Replace("/", "_");
    }
}
