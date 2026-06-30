using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using System.Globalization;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;

internal static class IdeologyServerCompatibility
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    public static ClashOfRimServerPluginDescriptor Descriptor { get; } =
        new(
            Id: "builtin.dlc." + IdeologyCompatibilityKeys.PackageId,
            Name: "Built-in DLC compatibility: Ideology",
            Version: "1.0.0",
            AssemblyName: "AIRsLight.ClashOfRim.Network",
            FileName: string.Empty,
            Capabilities: new[]
            {
                IdeologyCompatibilityKeys.PawnReference,
                IdeologyCompatibilityKeys.WorldIdeoCatalog
            },
            WorldConfigurationExtensionProviders: new IWorldConfigurationExtensionProvider[]
            {
                new IdeologyWorldConfigurationExtensionProvider()
            },
            SaveIndexExtensions: new ISaveIndexExtension[]
            {
                new IdeologySaveIndexExtension()
            },
            RequiredPackageIds: new[] { IdeologyCompatibilityKeys.PackageId },
            Order: 200);

    public static IReadOnlyList<WorldIdeoSummaryDto> NormalizeSubmittedIdeos(
        IReadOnlyList<WorldIdeoSummaryDto> ideos,
        string userId,
        string colonyId,
        string worldConfigurationId)
    {
        return ideos
            .Select(ideo => NormalizeWorldIdeo(ideo, userId, colonyId, worldConfigurationId))
            .GroupBy(ideo => ideo.GlobalKey, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();
    }

    public static IReadOnlyList<WorldIdeoSummaryDto> ReadWorldIdeoCatalog(
        IReadOnlyList<WorldConfigurationExtensionDto>? extensions)
    {
        return ReadWorldIdeoCatalogExtension(extensions);
    }

    public static WorldConfigurationExtensionDto? BuildWorldIdeoCatalogExtension(
        IReadOnlyList<WorldIdeoSummaryDto> ideos)
    {
        if (ideos.Count == 0)
        {
            return null;
        }

        return new WorldConfigurationExtensionDto(
            IdeologyCompatibilityKeys.PackageId,
            IdeologyCompatibilityKeys.WorldIdeoCatalog,
            "1",
            JsonSerializer.Serialize(ideos, PayloadJsonOptions),
            new Dictionary<string, string?>
            {
                ["count"] = ideos.Count.ToString(CultureInfo.InvariantCulture)
            });
    }

    private static IReadOnlyList<WorldIdeoSummaryDto> ReadWorldIdeoCatalogExtension(
        IReadOnlyList<WorldConfigurationExtensionDto>? extensions)
    {
        WorldConfigurationExtensionDto? extension = extensions?.FirstOrDefault(extension =>
            string.Equals(extension.ProviderId, IdeologyCompatibilityKeys.PackageId, StringComparison.Ordinal)
            && string.Equals(extension.Kind, IdeologyCompatibilityKeys.WorldIdeoCatalog, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(extension?.PayloadJson))
        {
            return Array.Empty<WorldIdeoSummaryDto>();
        }

        try
        {
            List<WorldIdeoSummaryDto>? parsed = JsonSerializer.Deserialize<List<WorldIdeoSummaryDto>>(
                extension.PayloadJson!,
                PayloadJsonOptions);
            return parsed is null ? Array.Empty<WorldIdeoSummaryDto>() : parsed;
        }
        catch (JsonException)
        {
            return Array.Empty<WorldIdeoSummaryDto>();
        }
    }

    public static IReadOnlyList<WorldIdeoSummaryDto> BuildCurrentWorldIdeos(WorldConfigurationDto configuration)
    {
        return BuildCurrentWorldIdeos(configuration.Extensions);
    }

    public static IReadOnlyList<WorldIdeoSummaryDto> BuildCurrentWorldIdeos(
        IReadOnlyList<WorldConfigurationExtensionDto>? extensions)
    {
        var byGlobalKey = new Dictionary<string, WorldIdeoSummaryDto>(StringComparer.Ordinal);
        foreach (WorldIdeoSummaryDto ideo in ReadWorldIdeoCatalog(extensions))
        {
            if (!string.IsNullOrWhiteSpace(ideo.GlobalKey))
            {
                byGlobalKey[ideo.GlobalKey] = ideo;
            }
        }

        return HydrateEquivalentIdeoPackages(byGlobalKey.Values.ToList())
            .OrderBy(ideo => ideo.OwnerUserId, StringComparer.Ordinal)
            .ThenBy(ideo => ideo.OwnerColonyId, StringComparer.Ordinal)
            .ThenBy(ideo => ideo.SourceSnapshotId, StringComparer.Ordinal)
            .ThenBy(ideo => ideo.LocalId, StringComparer.Ordinal)
            .ThenBy(ideo => ideo.GlobalKey, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<WorldIdeoSummaryDto> HydrateEquivalentIdeoPackages(
        IReadOnlyList<WorldIdeoSummaryDto> ideos)
    {
        var hydrated = new List<WorldIdeoSummaryDto>(ideos.Count);
        foreach (WorldIdeoSummaryDto ideo in ideos)
        {
            if (string.IsNullOrWhiteSpace(ideo.SavedIdeoPackageXml)
                && TryFindEquivalentCanonicalIdeo(ideo, ideos, out WorldIdeoSummaryDto? equivalent))
            {
                hydrated.Add(CopyEquivalentIdeoPackage(ideo, equivalent));
                continue;
            }

            hydrated.Add(ideo);
        }

        return hydrated;
    }

    public static IReadOnlyList<WorldIdeoSummaryDto> MergeWorldIdeos(
        IReadOnlyList<WorldIdeoSummaryDto> current,
        IReadOnlyList<WorldIdeoSummaryDto>? incoming,
        string userId,
        string colonyId)
    {
        var byGlobalKey = new Dictionary<string, WorldIdeoSummaryDto>(StringComparer.Ordinal);
        foreach (WorldIdeoSummaryDto ideo in current)
        {
            if (!string.IsNullOrWhiteSpace(ideo.GlobalKey))
            {
                byGlobalKey[ideo.GlobalKey] = ideo;
            }
        }

        foreach (WorldIdeoSummaryDto ideo in incoming ?? Array.Empty<WorldIdeoSummaryDto>())
        {
            if (string.IsNullOrWhiteSpace(ideo.GlobalKey)
                || !string.Equals(ideo.OwnerUserId, userId, StringComparison.Ordinal))
            {
                continue;
            }

            WorldIdeoSummaryDto normalized = string.Equals(ideo.OwnerColonyId, colonyId, StringComparison.Ordinal)
                ? ideo
                : new WorldIdeoSummaryDto(
                    ideo.GlobalKey,
                    ideo.OwnerUserId,
                    colonyId,
                    ideo.SourceSnapshotId,
                    ideo.LocalId,
                    ideo.Name,
                    ideo.Culture,
                    ideo.CultureLabel,
                    ideo.CultureIconPath,
                    ideo.PrimaryFactionColor,
                    ideo.PrimaryFactionColorHex,
                    ideo.FoundationDefName,
                    ideo.FactionDefName,
                    ideo.IconDefName,
                    ideo.IconPath,
                    ideo.ColorDefName,
                    ideo.ColorHex,
                    ideo.SavedIdeoPackageXml,
                    ideo.SavedIdeoPackageSha256,
                    ideo.UpdatedAtGameTicks,
                    ideo.MemeDefNames,
                    ideo.PreceptDefNames,
                    ideo.StyleCategoryDefNames,
                    ideo.Hidden,
                    ideo.InitialPlayerIdeo,
                    ideo.MemeCount,
                    ideo.PreceptCount);

            if (byGlobalKey.TryGetValue(normalized.GlobalKey, out WorldIdeoSummaryDto? existing))
            {
                normalized = PreserveExistingIdeoPackageWhenIncomingIsSummary(normalized, existing);
            }
            else if (TryFindEquivalentCanonicalIdeo(normalized, byGlobalKey.Values, out existing))
            {
                normalized = CopyEquivalentIdeoPackage(normalized, existing);
            }

            byGlobalKey[normalized.GlobalKey] = normalized;
        }

        return byGlobalKey.Values
            .OrderBy(ideo => ideo.OwnerUserId, StringComparer.Ordinal)
            .ThenBy(ideo => ideo.OwnerColonyId, StringComparer.Ordinal)
            .ThenBy(ideo => ideo.GlobalKey, StringComparer.Ordinal)
            .ToList();
    }

    public static WorldIdeoSummaryDto NormalizeWorldIdeo(
        WorldIdeoSummaryDto ideo,
        string userId,
        string colonyId,
        string worldConfigurationId)
    {
        string ownerUserId = string.IsNullOrWhiteSpace(ideo.OwnerUserId)
            ? userId
            : ideo.OwnerUserId;
        bool serverOwned = string.Equals(ownerUserId, "server", StringComparison.OrdinalIgnoreCase);
        string ownerColonyId = serverOwned
            ? (string.IsNullOrWhiteSpace(ideo.OwnerColonyId) ? "unknown" : ideo.OwnerColonyId!)
            : colonyId;
        string sourceSnapshotId = string.IsNullOrWhiteSpace(ideo.SourceSnapshotId)
            ? worldConfigurationId
            : ideo.SourceSnapshotId!;
        string? localId = string.IsNullOrWhiteSpace(ideo.LocalId)
            ? TryExtractLocalIdeoId(ideo.GlobalKey)
            : ideo.LocalId;
        string globalKey = BuildIdeoGlobalKey(ownerUserId, localId);

        return new WorldIdeoSummaryDto(
            globalKey,
            ownerUserId,
            ownerColonyId,
            sourceSnapshotId,
            localId,
            ideo.Name,
            ideo.Culture,
            ideo.CultureLabel,
            ideo.CultureIconPath,
            ideo.PrimaryFactionColor,
            ideo.PrimaryFactionColorHex,
            ideo.FoundationDefName,
            ideo.FactionDefName,
            ideo.IconDefName,
            ideo.IconPath,
            ideo.ColorDefName,
            ideo.ColorHex,
            ideo.SavedIdeoPackageXml,
            ideo.SavedIdeoPackageSha256,
            ideo.UpdatedAtGameTicks,
            ideo.MemeDefNames,
            ideo.PreceptDefNames,
            ideo.StyleCategoryDefNames,
            ideo.Hidden,
            ideo.InitialPlayerIdeo,
            Math.Max(0, ideo.MemeCount),
            Math.Max(0, ideo.PreceptCount));
    }

    public static IReadOnlyList<WorldIdeoSummaryDto> BuildWorldIdeosFromAcceptedSnapshot(
        LatestSnapshotRecord snapshot,
        string userId,
        string colonyId,
        IReadOnlyList<WorldConfigurationExtensionDto>? currentExtensions = null)
    {
        var byGlobalKey = BuildCurrentWorldIdeos(currentExtensions)
            .Where(ideo => !string.IsNullOrWhiteSpace(ideo.GlobalKey))
            .ToDictionary(ideo => ideo.GlobalKey, StringComparer.Ordinal);

        foreach (WorldIdeoSummaryDto summary in IdeologySaveIndexExtension.ReadIdeos(snapshot.Index.Extensions)
            .Where(ideo => !string.IsNullOrWhiteSpace(ideo.Id))
            .Select(ideo =>
            {
                string localId = NormalizeLocalIdeoId(ideo.Id);
                string ownerUserId = ideo.InitialPlayerIdeo ? userId : "server";
                string? ownerColonyId = ideo.InitialPlayerIdeo ? colonyId : null;
                string globalKey = BuildIdeoGlobalKey(ownerUserId, localId);
                WorldIdeoSummaryDto summary = new(
                    globalKey,
                    ownerUserId,
                    ownerColonyId,
                    snapshot.Identity.SnapshotId,
                    localId,
                    ideo.Name,
                    ideo.Culture,
                    ideo.CultureLabel,
                    ideo.CultureIconPath,
                    ideo.PrimaryFactionColor,
                    ideo.PrimaryFactionColorHex,
                    ideo.FoundationDefName,
                    ideo.FactionDefName,
                    ideo.IconDefName,
                    ideo.IconPath,
                    ideo.ColorDefName,
                    ideo.ColorHex,
                    savedIdeoPackageXml: null,
                    savedIdeoPackageSha256: null,
                    snapshot.Envelope.GameTicks,
                    ideo.MemeDefNames,
                    ideo.PreceptDefNames,
                    ideo.StyleCategoryDefNames,
                    ideo.Hidden,
                    ideo.InitialPlayerIdeo,
                    ideo.MemeCount,
                    ideo.PreceptCount);

                if (byGlobalKey.TryGetValue(globalKey, out WorldIdeoSummaryDto? existing))
                {
                    return PreserveExistingIdeoPackageWhenIncomingIsSummary(summary, existing);
                }

                return TryFindEquivalentCanonicalIdeo(summary, byGlobalKey.Values, out existing)
                    ? CopyEquivalentIdeoPackage(summary, existing)
                    : summary;
            }))
        {
            byGlobalKey[summary.GlobalKey] = summary;
        }

        return byGlobalKey.Values
            .OrderBy(ideo => ideo.OwnerUserId, StringComparer.Ordinal)
            .ThenBy(ideo => ideo.OwnerColonyId, StringComparer.Ordinal)
            .ThenBy(ideo => ideo.SourceSnapshotId, StringComparer.Ordinal)
            .ThenBy(ideo => ideo.LocalId, StringComparer.Ordinal)
            .ThenBy(ideo => ideo.GlobalKey, StringComparer.Ordinal)
            .ToList();
    }

    private static bool TryFindEquivalentCanonicalIdeo(
        WorldIdeoSummaryDto incoming,
        IEnumerable<WorldIdeoSummaryDto> current,
        out WorldIdeoSummaryDto existing)
    {
        foreach (WorldIdeoSummaryDto candidate in current)
        {
            if (string.Equals(candidate.GlobalKey, incoming.GlobalKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (HasSameSavedPackageHash(candidate, incoming)
                || HasSameIdeologyDefinition(candidate, incoming))
            {
                existing = candidate;
                return true;
            }
        }

        existing = null!;
        return false;
    }

    private static bool HasSameSavedPackageHash(WorldIdeoSummaryDto left, WorldIdeoSummaryDto right)
    {
        return !string.IsNullOrWhiteSpace(left.SavedIdeoPackageSha256)
            && !string.IsNullOrWhiteSpace(right.SavedIdeoPackageSha256)
            && string.Equals(left.SavedIdeoPackageSha256, right.SavedIdeoPackageSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSameIdeologyDefinition(WorldIdeoSummaryDto left, WorldIdeoSummaryDto right)
    {
        return string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            && string.Equals(left.Culture, right.Culture, StringComparison.Ordinal)
            && string.Equals(left.FoundationDefName, right.FoundationDefName, StringComparison.Ordinal)
            && string.Equals(left.IconDefName, right.IconDefName, StringComparison.Ordinal)
            && string.Equals(left.ColorDefName, right.ColorDefName, StringComparison.Ordinal)
            && string.Equals(left.PrimaryFactionColorHex, right.PrimaryFactionColorHex, StringComparison.OrdinalIgnoreCase)
            && SetEqualsOrdinal(left.MemeDefNames, right.MemeDefNames)
            && SetEqualsOrdinal(left.PreceptDefNames, right.PreceptDefNames)
            && SetEqualsOrdinal(left.StyleCategoryDefNames, right.StyleCategoryDefNames)
            && Math.Max(0, left.MemeCount) == Math.Max(0, right.MemeCount)
            && Math.Max(0, left.PreceptCount) == Math.Max(0, right.PreceptCount);
    }

    private static bool SetEqualsOrdinal(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        return new HashSet<string>(left.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.Ordinal)
            .SetEquals(right.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static WorldIdeoSummaryDto CopyEquivalentIdeoPackage(
        WorldIdeoSummaryDto incoming,
        WorldIdeoSummaryDto equivalent)
    {
        if (!string.IsNullOrWhiteSpace(incoming.SavedIdeoPackageXml)
            || string.IsNullOrWhiteSpace(equivalent.SavedIdeoPackageXml))
        {
            return incoming;
        }

        return new WorldIdeoSummaryDto(
            incoming.GlobalKey,
            incoming.OwnerUserId,
            incoming.OwnerColonyId,
            incoming.SourceSnapshotId,
            incoming.LocalId,
            incoming.Name,
            incoming.Culture,
            incoming.CultureLabel,
            incoming.CultureIconPath,
            incoming.PrimaryFactionColor,
            incoming.PrimaryFactionColorHex,
            incoming.FoundationDefName,
            incoming.FactionDefName,
            incoming.IconDefName,
            incoming.IconPath,
            incoming.ColorDefName,
            incoming.ColorHex,
            equivalent.SavedIdeoPackageXml,
            equivalent.SavedIdeoPackageSha256,
            incoming.UpdatedAtGameTicks,
            incoming.MemeDefNames,
            incoming.PreceptDefNames,
            incoming.StyleCategoryDefNames,
            incoming.Hidden,
            incoming.InitialPlayerIdeo,
            incoming.MemeCount,
            incoming.PreceptCount);
    }

    private static WorldIdeoSummaryDto PreserveExistingIdeoPackageWhenIncomingIsSummary(
        WorldIdeoSummaryDto incoming,
        WorldIdeoSummaryDto existing)
    {
        bool incomingHasPackage = !string.IsNullOrWhiteSpace(incoming.SavedIdeoPackageXml);
        bool existingHasPackage = !string.IsNullOrWhiteSpace(existing.SavedIdeoPackageXml);
        if (incomingHasPackage || !existingHasPackage)
        {
            return incoming;
        }

        return new WorldIdeoSummaryDto(
            incoming.GlobalKey,
            incoming.OwnerUserId,
            incoming.OwnerColonyId,
            incoming.SourceSnapshotId,
            incoming.LocalId,
            incoming.Name,
            incoming.Culture,
            incoming.CultureLabel,
            incoming.CultureIconPath,
            incoming.PrimaryFactionColor,
            incoming.PrimaryFactionColorHex,
            incoming.FoundationDefName,
            incoming.FactionDefName,
            incoming.IconDefName,
            incoming.IconPath,
            incoming.ColorDefName,
            incoming.ColorHex,
            existing.SavedIdeoPackageXml,
            existing.SavedIdeoPackageSha256,
            incoming.UpdatedAtGameTicks,
            incoming.MemeDefNames,
            incoming.PreceptDefNames,
            incoming.StyleCategoryDefNames,
            incoming.Hidden,
            incoming.InitialPlayerIdeo,
            incoming.MemeCount,
            incoming.PreceptCount);
    }

    private static string BuildIdeoGlobalKey(string ownerUserId, string? localId)
    {
        string safeLocalId = string.IsNullOrWhiteSpace(localId)
            ? "unknown"
            : localId!;
        return $"owner:{ownerUserId}/ideo:{safeLocalId}";
    }

    private static string? TryExtractLocalIdeoId(string? globalKey)
    {
        if (string.IsNullOrWhiteSpace(globalKey))
        {
            return null;
        }

        int marker = globalKey!.LastIndexOf("/ideo:", StringComparison.Ordinal);
        return marker < 0 ? null : NormalizeLocalIdeoId(globalKey[(marker + "/ideo:".Length)..]);
    }

    private static string NormalizeLocalIdeoId(string? localId)
    {
        string trimmed = localId?.Trim() ?? string.Empty;
        return trimmed.StartsWith("Ideo_", StringComparison.Ordinal)
            ? trimmed["Ideo_".Length..]
            : trimmed;
    }

}
