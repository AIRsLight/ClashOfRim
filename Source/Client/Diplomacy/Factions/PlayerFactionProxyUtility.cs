using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Diplomacy;

internal static class PlayerFactionProxyUtility
{
    public const string ProxyFactionDefName = "ClashOfRim_PlayerProxy";
    private static readonly Dictionary<int, string> ProxyOwnerUserIdsByLoadId = new();
    private static readonly Dictionary<string, Faction> ProxyFactionsByOwnerUserId = new(StringComparer.Ordinal);

    public static Faction? EnsureProxyForUser(string? userId, string? factionDefName = null, string? displayFactionName = null)
    {
        if (Find.World?.factionManager is null || string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        string normalizedUserId = userId!;
        Faction? existing = FindProxyForUser(normalizedUserId);
        if (existing is not null)
        {
            ApplyProxyPresentation(existing, normalizedUserId, displayFactionName);
            NotifyProxyFactionPrepared(existing, normalizedUserId);
            return existing;
        }

        FactionDef? factionDef = ResolveProxyFactionDef(factionDefName);
        if (factionDef is null)
        {
            return null;
        }

        Faction proxy = new()
        {
            def = factionDef,
            loadID = Find.UniqueIDsManager.GetNextFactionID(),
            temporary = true,
            hidden = false,
            allowGoodwillRewards = false,
            allowRoyalFavorRewards = false,
            color = ColorFromStableText(normalizedUserId)
        };
        proxy.Name = BuildDisplayFactionName(normalizedUserId, displayFactionName);
        ApplyProxyPresentation(proxy, normalizedUserId, displayFactionName);
        NotifyProxyFactionPrepared(proxy, normalizedUserId);

        foreach (Faction other in Find.World.factionManager.AllFactionsListForReading.ToList())
        {
            proxy.SetRelation(new FactionRelation(other, FactionRelationKind.Neutral));
            other.SetRelation(new FactionRelation(proxy, FactionRelationKind.Neutral));
        }

        Find.World.factionManager.Add(proxy);
        AutomaticRaidFactionPolicy.RegisterServerPlayerFactionId(proxy.GetUniqueLoadID());
        return proxy;
    }

    public static Faction? FindProxyForUser(string? userId)
    {
        if (Find.World?.factionManager is null || string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        string normalizedUserId = userId!;
        if (TryGetCachedProxyForUser(normalizedUserId, out Faction? cached))
        {
            return cached;
        }

        return Find.World.factionManager.AllFactions.FirstOrDefault(faction =>
        {
            bool match = faction.temporary
                && (string.Equals(FindKnownProxyOwnerUserId(faction), normalizedUserId, StringComparison.Ordinal)
                    || string.Equals(faction.Name, normalizedUserId, StringComparison.Ordinal)
                    || IsDisplayNameForUser(faction.Name, normalizedUserId));
            if (match)
            {
                ProxyFactionsByOwnerUserId[normalizedUserId] = faction;
            }

            return match;
        });
    }

    public static bool IsServerPlayerProxy(Faction? faction)
    {
        if (faction is null || faction.IsPlayer)
        {
            return false;
        }

        return string.Equals(faction.def?.defName, ProxyFactionDefName, StringComparison.Ordinal)
            || AutomaticRaidFactionPolicy.IsRegisteredServerPlayerFactionId(faction.GetUniqueLoadID());
    }

    public static string ProxyOwnerUserId(Faction faction)
    {
        string? known = FindKnownProxyOwnerUserId(faction);
        if (!string.IsNullOrWhiteSpace(known))
        {
            return known!;
        }

        string name = faction.Name ?? string.Empty;
        int suffixStart = name.LastIndexOf('(');
        if (suffixStart >= 0 && name.EndsWith(")", StringComparison.Ordinal) && suffixStart < name.Length - 1)
        {
            return name.Substring(suffixStart + 1, name.Length - suffixStart - 2);
        }

        return name;
    }

    public static string BuildReportText(Faction faction)
    {
        return ClashOfRimText.Key(
            "ClashOfRim.PlayerFaction.Report",
            ProxyOwnerUserId(faction).Named("PLAYER"));
    }

    public static bool SetPlayerRelation(
        string? userId,
        FactionRelationKind relationKind,
        string? reason = null,
        bool canSendHostilityLetter = true)
    {
        Faction? proxy = EnsureProxyForUser(userId);
        if (proxy is null || Faction.OfPlayer is null)
        {
            return false;
        }

        proxy.SetRelationDirect(
            Faction.OfPlayer,
            relationKind,
            canSendHostilityLetter,
            string.IsNullOrWhiteSpace(reason) ? "ClashOfRim" : reason);
        return true;
    }

    private static FactionDef? ResolveProxyFactionDef(string? originalFactionDefName)
    {
        FactionDef? proxy = DefDatabase<FactionDef>.GetNamedSilentFail(ProxyFactionDefName);
        if (proxy is not null)
        {
            return proxy;
        }

        FactionDef? original = string.IsNullOrWhiteSpace(originalFactionDefName)
            ? null
            : DefDatabase<FactionDef>.GetNamedSilentFail(originalFactionDefName);
        if (original is not null && !original.isPlayer)
        {
            return original;
        }

        return DefDatabase<FactionDef>.GetNamedSilentFail("OutlanderCivil")
            ?? DefDatabase<FactionDef>.AllDefsListForReading.FirstOrDefault(def =>
                def.humanlikeFaction && !def.isPlayer && !def.permanentEnemy);
    }

    private static void ApplyProxyPresentation(Faction faction, string userId, string? displayFactionName = null)
    {
        FactionDef? proxyDef = DefDatabase<FactionDef>.GetNamedSilentFail(ProxyFactionDefName);
        if (proxyDef is not null)
        {
            faction.def = proxyDef;
        }

        ProxyOwnerUserIdsByLoadId[faction.loadID] = userId;
        ProxyFactionsByOwnerUserId[userId] = faction;
        if (!string.IsNullOrWhiteSpace(displayFactionName)
            || string.IsNullOrWhiteSpace(faction.Name)
            || string.Equals(faction.Name, userId, StringComparison.Ordinal)
            || !IsDisplayNameForUser(faction.Name, userId))
        {
            faction.Name = BuildDisplayFactionName(userId, displayFactionName);
        }
        faction.hidden = false;
        faction.temporary = true;
        faction.allowGoodwillRewards = false;
        faction.allowRoyalFavorRewards = false;
        faction.leader = null;
        if (faction.color is null)
        {
            faction.color = ColorFromStableText(userId);
        }

        AutomaticRaidFactionPolicy.RegisterServerPlayerFactionId(faction.GetUniqueLoadID());
    }

    private static void NotifyProxyFactionPrepared(Faction faction, string ownerUserId)
    {
        ClashOfRimCompatibilityApi.NotifyPlayerProxyFactionPrepared(faction, ownerUserId);
    }

    private static string? FindKnownProxyOwnerUserId(Faction faction)
    {
        return ProxyOwnerUserIdsByLoadId.TryGetValue(faction.loadID, out string userId) ? userId : null;
    }

    private static bool TryGetCachedProxyForUser(string userId, out Faction? faction)
    {
        faction = null;
        if (!ProxyFactionsByOwnerUserId.TryGetValue(userId, out Faction? cached)
            || cached is null
            || Find.World?.factionManager?.AllFactionsListForReading?.Contains(cached) != true)
        {
            ProxyFactionsByOwnerUserId.Remove(userId);
            return false;
        }

        faction = cached;
        return true;
    }

    private static string BuildDisplayFactionName(string userId, string? displayFactionName)
    {
        string trimmed = string.IsNullOrWhiteSpace(displayFactionName) ? string.Empty : displayFactionName!.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? userId : $"{trimmed}({userId})";
    }

    private static bool IsDisplayNameForUser(string? factionName, string userId)
    {
        return !string.IsNullOrWhiteSpace(factionName)
            && factionName!.EndsWith("(" + userId + ")", StringComparison.Ordinal);
    }

    private static Color ColorFromStableText(string text)
    {
        uint hash = StableHash(text);
        float hue = (hash & 0xFFFF) / 65535f;
        return Color.HSVToRGB(hue, 0.55f, 0.9f);
    }

    private static uint StableHash(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char ch in text)
            {
                hash ^= ch;
                hash *= 16777619;
            }

            return hash;
        }
    }
}
