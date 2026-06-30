using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

public static class RemoteIdeoCatalog
{
    private static readonly Dictionary<Ideo, List<string>> globalKeysByIdeo = new();
    private static readonly Dictionary<string, Ideo> ideosByGlobalKey = new(System.StringComparer.Ordinal);
    private static readonly Dictionary<string, RemoteIdeoDisplayMetadata> metadataByGlobalKey = new(System.StringComparer.Ordinal);
    private static readonly Dictionary<string, List<string>> globalKeysByOwnerUserId = new(System.StringComparer.Ordinal);

    public static void Register(Ideo ideo, string globalKey, RemoteIdeoDisplayMetadata? metadata = null)
    {
        if (ideo is null || string.IsNullOrWhiteSpace(globalKey))
        {
            return;
        }

        if (ideosByGlobalKey.TryGetValue(globalKey, out Ideo? previousIdeo)
            && previousIdeo != ideo)
        {
            RemoveKeyFromIdeo(previousIdeo, globalKey);
            RemoveOwnerKey(globalKey);
        }

        if (!globalKeysByIdeo.TryGetValue(ideo, out List<string>? ideoKeys))
        {
            ideoKeys = new List<string>();
            globalKeysByIdeo[ideo] = ideoKeys;
        }

        if (!ContainsOrdinal(ideoKeys, globalKey))
        {
            ideoKeys.Add(globalKey);
        }

        ideosByGlobalKey[globalKey] = ideo;
        if (metadata is not null)
        {
            RemoveOwnerKey(globalKey);
            metadataByGlobalKey[globalKey] = metadata;
            if (!string.IsNullOrWhiteSpace(metadata.OwnerUserId))
            {
                if (!globalKeysByOwnerUserId.TryGetValue(metadata.OwnerUserId!, out List<string> ownerKeys))
                {
                    ownerKeys = new List<string>();
                    globalKeysByOwnerUserId[metadata.OwnerUserId!] = ownerKeys;
                }

                if (!ContainsOrdinal(ownerKeys, globalKey))
                {
                    ownerKeys.Add(globalKey);
                }
            }
        }
    }

    public static void Unregister(Ideo ideo)
    {
        if (ideo is null || !globalKeysByIdeo.TryGetValue(ideo, out List<string>? globalKeys))
        {
            return;
        }

        globalKeysByIdeo.Remove(ideo);
        foreach (string globalKey in globalKeys.ToList())
        {
            if (ideosByGlobalKey.TryGetValue(globalKey, out Ideo? registered) && registered == ideo)
            {
                ideosByGlobalKey.Remove(globalKey);
            }

            metadataByGlobalKey.Remove(globalKey);
            RemoveOwnerKey(globalKey);
        }
    }

    private static void RemoveKeyFromIdeo(Ideo ideo, string globalKey)
    {
        if (ideo is null || !globalKeysByIdeo.TryGetValue(ideo, out List<string>? globalKeys))
        {
            return;
        }

        for (int i = globalKeys.Count - 1; i >= 0; i--)
        {
            if (string.Equals(globalKeys[i], globalKey, StringComparison.Ordinal))
            {
                globalKeys.RemoveAt(i);
            }
        }

        if (globalKeys.Count == 0)
        {
            globalKeysByIdeo.Remove(ideo);
        }
    }

    private static void RemoveOwnerKey(string globalKey)
    {
        foreach (List<string> ownerKeys in globalKeysByOwnerUserId.Values)
        {
            for (int i = ownerKeys.Count - 1; i >= 0; i--)
            {
                if (string.Equals(ownerKeys[i], globalKey, StringComparison.Ordinal))
                {
                    ownerKeys.RemoveAt(i);
                }
            }
        }
    }

    public static bool TryGetIdeo(string globalKey, out Ideo? ideo)
    {
        if (!ideosByGlobalKey.TryGetValue(globalKey, out ideo)
            || !IsInCurrentIdeoManager(ideo))
        {
            ideo = null;
            return false;
        }

        return true;
    }

    public static bool TryGetCatalogIdeo(string globalKey, out Ideo? ideo)
    {
        return ideosByGlobalKey.TryGetValue(globalKey, out ideo);
    }

    public static bool IsInCurrentIdeoManager(Ideo? ideo)
    {
        return ideo is not null
            && Find.IdeoManager?.IdeosListForReading?.Contains(ideo) == true;
    }

    public static bool TryGetGlobalKey(Ideo ideo, out string? globalKey)
    {
        globalKey = null;
        if (ideo is null
            || !globalKeysByIdeo.TryGetValue(ideo, out List<string>? globalKeys)
            || globalKeys.Count == 0)
        {
            return false;
        }

        globalKey = globalKeys
            .OrderBy(key => key, StringComparer.Ordinal)
            .FirstOrDefault();
        return !string.IsNullOrWhiteSpace(globalKey);
    }

    public static bool TryGetGlobalKeyForOwner(Ideo ideo, string? ownerUserId, out string? globalKey)
    {
        globalKey = null;
        if (ideo is null
            || string.IsNullOrWhiteSpace(ownerUserId)
            || !globalKeysByIdeo.TryGetValue(ideo, out List<string>? globalKeys))
        {
            return false;
        }

        globalKey = globalKeys
            .Where(key => OwnerMatches(key, ownerUserId))
            .OrderBy(key => key, StringComparer.Ordinal)
            .FirstOrDefault();
        return !string.IsNullOrWhiteSpace(globalKey);
    }

    public static bool TryGetDisplayMetadata(string globalKey, out RemoteIdeoDisplayMetadata? metadata)
    {
        return metadataByGlobalKey.TryGetValue(globalKey, out metadata);
    }

    public static bool TryFindPrimaryIdeoForOwner(string? ownerUserId, out Ideo? ideo)
    {
        ideo = null;
        if (string.IsNullOrWhiteSpace(ownerUserId)
            || !globalKeysByOwnerUserId.TryGetValue(ownerUserId!, out List<string> ownerKeys))
        {
            return false;
        }

        string? selectedKey = null;
        Ideo? selectedIdeo = null;
        RemoteIdeoDisplayMetadata? selectedMetadata = null;
        foreach (string key in ownerKeys)
        {
            if (!metadataByGlobalKey.TryGetValue(key, out RemoteIdeoDisplayMetadata? metadata)
                || !ideosByGlobalKey.TryGetValue(key, out Ideo? candidate)
                || !IsInCurrentIdeoManager(candidate))
            {
                continue;
            }

            if (IsBetterPrimaryOwnerIdeo(metadata, selectedMetadata))
            {
                selectedKey = key;
                selectedIdeo = candidate;
                selectedMetadata = metadata;
            }
        }

        ideo = selectedIdeo;
        return selectedKey is not null && ideo is not null;
    }

    public static bool TryFindIdeoByLocalId(
        string localId,
        string? preferredOwnerUserId,
        out Ideo? ideo,
        out string? globalKey)
    {
        ideo = null;
        globalKey = null;
        if (string.IsNullOrWhiteSpace(localId))
        {
            return false;
        }

        string suffix = "/ideo:" + localId;
        string? selectedKey = null;
        Ideo? selectedIdeo = null;
        bool selectedPreferredOwnerMatch = false;
        bool selectedServerOwnerMatch = false;
        foreach (KeyValuePair<string, Ideo> entry in ideosByGlobalKey)
        {
            string key = entry.Key;
            if (!key.EndsWith(suffix, StringComparison.Ordinal)
                || !IsInCurrentIdeoManager(entry.Value))
            {
                continue;
            }

            bool preferredOwnerMatch = OwnerMatches(key, preferredOwnerUserId);
            bool serverOwnerMatch = OwnerMatches(key, "server");
            if (IsBetterLocalIdMatch(
                    key,
                    preferredOwnerMatch,
                    serverOwnerMatch,
                    selectedKey,
                    selectedPreferredOwnerMatch,
                    selectedServerOwnerMatch))
            {
                selectedKey = key;
                selectedIdeo = entry.Value;
                selectedPreferredOwnerMatch = preferredOwnerMatch;
                selectedServerOwnerMatch = serverOwnerMatch;
            }
        }

        if (selectedKey is null || selectedIdeo is null)
        {
            return false;
        }

        globalKey = selectedKey;
        ideo = selectedIdeo;
        return true;
    }

    public static bool TryFindIdeoByLocalIdForOwner(
        string localId,
        string? ownerUserId,
        out Ideo? ideo,
        out string? globalKey)
    {
        ideo = null;
        globalKey = null;
        if (string.IsNullOrWhiteSpace(localId) || string.IsNullOrWhiteSpace(ownerUserId))
        {
            return false;
        }

        string suffix = "/ideo:" + localId;
        string? selectedKey = null;
        Ideo? selectedIdeo = null;
        foreach (KeyValuePair<string, Ideo> entry in ideosByGlobalKey)
        {
            string key = entry.Key;
            if (!key.EndsWith(suffix, StringComparison.Ordinal)
                || !OwnerMatches(key, ownerUserId)
                || !IsInCurrentIdeoManager(entry.Value))
            {
                continue;
            }

            if (selectedKey is null || string.CompareOrdinal(key, selectedKey) < 0)
            {
                selectedKey = key;
                selectedIdeo = entry.Value;
            }
        }

        if (selectedKey is null || selectedIdeo is null)
        {
            return false;
        }

        globalKey = selectedKey;
        ideo = selectedIdeo;
        return true;
    }

    public static bool TryFindIdeoByPackageHash(string? packageSha256, out Ideo? ideo)
    {
        ideo = null;
        if (string.IsNullOrWhiteSpace(packageSha256))
        {
            return false;
        }

        foreach (KeyValuePair<string, RemoteIdeoDisplayMetadata> entry in metadataByGlobalKey)
        {
            if (!string.Equals(entry.Value.SavedIdeoPackageSha256, packageSha256, StringComparison.OrdinalIgnoreCase)
                || !ideosByGlobalKey.TryGetValue(entry.Key, out Ideo? candidate)
                || !IsInCurrentIdeoManager(candidate))
            {
                continue;
            }

            ideo = candidate;
            return true;
        }

        return false;
    }

    public static bool IsRemoteShadow(Ideo ideo)
    {
        return ideo is not null
            && globalKeysByIdeo.ContainsKey(ideo)
            && !ideo.initialPlayerIdeo
            && Faction.OfPlayer?.ideos?.PrimaryIdeo != ideo;
    }

    private static bool OwnerMatches(string globalKey, string? ownerUserId)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId)
            || !metadataByGlobalKey.TryGetValue(globalKey, out RemoteIdeoDisplayMetadata? metadata))
        {
            return false;
        }

        return string.Equals(metadata.OwnerUserId, ownerUserId, StringComparison.Ordinal);
    }

    private static bool IsBetterPrimaryOwnerIdeo(
        RemoteIdeoDisplayMetadata candidate,
        RemoteIdeoDisplayMetadata? selected)
    {
        if (selected is null)
        {
            return true;
        }

        if (candidate.InitialPlayerIdeo != selected.InitialPlayerIdeo)
        {
            return candidate.InitialPlayerIdeo;
        }

        return string.CompareOrdinal(candidate.GlobalKey, selected.GlobalKey) < 0;
    }

    private static bool IsBetterLocalIdMatch(
        string candidateKey,
        bool candidatePreferredOwnerMatch,
        bool candidateServerOwnerMatch,
        string? selectedKey,
        bool selectedPreferredOwnerMatch,
        bool selectedServerOwnerMatch)
    {
        if (selectedKey is null)
        {
            return true;
        }

        if (candidatePreferredOwnerMatch != selectedPreferredOwnerMatch)
        {
            return candidatePreferredOwnerMatch;
        }

        if (candidateServerOwnerMatch != selectedServerOwnerMatch)
        {
            return candidateServerOwnerMatch;
        }

        return string.CompareOrdinal(candidateKey, selectedKey) < 0;
    }

    private static bool ContainsOrdinal(List<string> values, string value)
    {
        foreach (string candidate in values)
        {
            if (string.Equals(candidate, value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class RemoteIdeoDisplayMetadata
{
    public RemoteIdeoDisplayMetadata(
        string globalKey,
        string? ownerUserId,
        string? ownerColonyId,
        string? name,
        string? cultureDefName,
        string? cultureLabel,
        string? cultureIconPath,
        string? iconDefName,
        string? iconPath,
        string? colorDefName,
        string? colorHex,
        string? primaryFactionColorHex,
        string? savedIdeoPackageSha256,
        bool initialPlayerIdeo)
    {
        GlobalKey = globalKey;
        OwnerUserId = ownerUserId;
        OwnerColonyId = ownerColonyId;
        Name = name;
        CultureDefName = cultureDefName;
        CultureLabel = cultureLabel;
        CultureIconPath = cultureIconPath;
        IconDefName = iconDefName;
        IconPath = iconPath;
        ColorDefName = colorDefName;
        ColorHex = colorHex;
        PrimaryFactionColorHex = primaryFactionColorHex;
        SavedIdeoPackageSha256 = savedIdeoPackageSha256;
        InitialPlayerIdeo = initialPlayerIdeo;
    }

    public string GlobalKey { get; }

    public string? OwnerUserId { get; }

    public string? OwnerColonyId { get; }

    public string? Name { get; }

    public string? CultureDefName { get; }

    public string? CultureLabel { get; }

    public string? CultureIconPath { get; }

    public string? IconDefName { get; }

    public string? IconPath { get; }

    public string? ColorDefName { get; }

    public string? ColorHex { get; }

    public string? PrimaryFactionColorHex { get; }

    public string? SavedIdeoPackageSha256 { get; }

    public bool InitialPlayerIdeo { get; }
}
