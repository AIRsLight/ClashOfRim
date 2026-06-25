using System;
using Verse;

namespace AIRsLight.ClashOfRim.Pawns;

internal static class PawnGlobalIdUtility
{
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

    private static string Segment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        return value!.Trim().Replace("/", "_");
    }
}
