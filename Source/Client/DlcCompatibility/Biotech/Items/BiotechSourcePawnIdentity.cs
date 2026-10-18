using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Compatibility.Biotech;
using AIRsLight.ClashOfRim.Pawns;
using RimWorld;
using System;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Items;

internal static class BiotechSourcePawnIdentity
{
    internal const string PawnGlobalIdTagPrefix = "ClashOfRim.PawnGlobalId:";
    internal const string ReproductiveSourcePlaceholderTagPrefix = "ClashOfRim.ReproductiveSourcePlaceholder:";

    internal static string? OwnerFromRecord(ReproductiveSourceRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.OwnerUserId))
        {
            return record.OwnerUserId!.Trim();
        }

        if (IsPlayerLikeFactionDef(record.FactionDefName)
            && PawnGlobalIdUtility.TryExtractOwnerUserId(record.GlobalId, out string? ownerUserId))
        {
            return ownerUserId;
        }

        return null;
    }

    internal static string? OwnerFor(Pawn pawn, string? globalId, string? currentUserId)
    {
        string? ownerFromGlobalId = PawnGlobalIdUtility.TryExtractOwnerUserId(globalId, out string? parsedOwnerUserId)
            ? parsedOwnerUserId
            : null;
        if (PlayerFactionProxyUtility.IsServerPlayerProxy(pawn.Faction))
        {
            return ownerFromGlobalId ?? PlayerFactionProxyUtility.ProxyOwnerUserId(pawn.Faction);
        }

        if (pawn.Faction == Faction.OfPlayer || pawn.Faction?.IsPlayer == true)
        {
            return ownerFromGlobalId ?? (string.IsNullOrWhiteSpace(currentUserId) ? null : currentUserId);
        }

        if (!string.IsNullOrWhiteSpace(ownerFromGlobalId) && ExistingGlobalId(pawn) is not null)
        {
            return ownerFromGlobalId;
        }

        return null;
    }

    internal static bool IsPlayerLikeFactionDef(string? factionDefName)
    {
        if (string.IsNullOrWhiteSpace(factionDefName))
        {
            return false;
        }

        string trimmed = factionDefName!.Trim();
        if (string.Equals(trimmed, PlayerFactionProxyUtility.ProxyFactionDefName, StringComparison.Ordinal)
            || string.Equals(trimmed, Faction.OfPlayer?.def?.defName, StringComparison.Ordinal))
        {
            return true;
        }

        return DefDatabase<FactionDef>.GetNamedSilentFail(trimmed)?.isPlayer == true;
    }

    internal static string? ExistingGlobalId(Pawn pawn)
    {
        if (pawn?.questTags is null)
        {
            return null;
        }

        foreach (string tag in pawn.questTags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            if (tag.StartsWith(PawnGlobalIdTagPrefix, StringComparison.Ordinal))
            {
                string globalId = tag.Substring(PawnGlobalIdTagPrefix.Length).Trim();
                if (!string.IsNullOrWhiteSpace(globalId))
                {
                    return globalId;
                }
            }

            if (tag.StartsWith(ReproductiveSourcePlaceholderTagPrefix, StringComparison.Ordinal))
            {
                string globalId = tag.Substring(ReproductiveSourcePlaceholderTagPrefix.Length).Trim();
                if (!string.IsNullOrWhiteSpace(globalId))
                {
                    return globalId;
                }
            }
        }

        return null;
    }

    internal static void MarkGlobalId(Pawn pawn, string? globalId)
    {
        if (pawn is null || string.IsNullOrWhiteSpace(globalId))
        {
            return;
        }

        QuestUtility.AddQuestTag(ref pawn.questTags, PawnGlobalIdTagPrefix + globalId!.Trim());
    }
}
