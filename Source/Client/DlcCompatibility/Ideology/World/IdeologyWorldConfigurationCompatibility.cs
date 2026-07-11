using System;
using System.Collections.Generic;
using System.Globalization;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class IdeologyWorldConfigurationCompatibility
{
    internal static IReadOnlyList<ModWorldIdeoSummaryDto> ReadWorldIdeoCatalog(ModWorldConfigurationDto configuration)
    {
        return WorldConfigurationExtensionPayloadJson.Read<ModWorldIdeoSummaryDto>(
            configuration.Extensions,
            IdeologyCompatibilityKeys.PackageId,
            IdeologyCompatibilityKeys.WorldIdeoCatalog);
    }

    internal static IReadOnlyList<ModWorldConfigurationExtensionDto> CollectWorldIdeoCatalogExtension(
        string? userId,
        string? colonyId,
        string worldConfigurationId)
    {
        ModWorldConfigurationExtensionDto? extension = BuildWorldIdeoCatalogExtension(
            IdeologyPawnReferenceCompatibility.ReadCurrentWorldIdeos(userId, colonyId, worldConfigurationId));
        return extension is null
            ? Array.Empty<ModWorldConfigurationExtensionDto>()
            : new[] { extension };
    }

    internal static int ApplyWorldIdeoCatalogExtension(
        ModWorldConfigurationDto configuration,
        string? localUserId,
        bool applyWorldState)
    {
        IReadOnlyList<ModWorldIdeoSummaryDto> ideos = ReadWorldIdeoCatalog(configuration);
        int appliedIdeos = RemoteIdeoMaterializer.ApplyServerCatalog(ideos, localUserId);
        foreach (ModWorldIdeoSummaryDto ideo in ideos)
        {
            if (!string.IsNullOrWhiteSpace(ideo.OwnerUserId)
                && !string.Equals(ideo.OwnerUserId, "server", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(ideo.OwnerUserId, localUserId, StringComparison.Ordinal))
            {
                PlayerFactionProxyUtility.EnsureProxyForUser(ideo.OwnerUserId!);
            }
        }

        RemoteIdeoDiagnostics.LogCatalogState(ideos, localUserId);

        if (appliedIdeos > 0)
        {
            ClashLog.Message($"[ClashOfRim] Applied server ideo catalog: {appliedIdeos}.");
        }

        return appliedIdeos;
    }

    internal static IReadOnlyList<WorldConfigurationExtensionSummaryItem> WorldIdeoCatalogSummary(
        ModWorldConfigurationDto configuration)
    {
        return new[]
        {
            new WorldConfigurationExtensionSummaryItem(
                "rimworld.ideology.world-ideo-catalog.count",
                ClashOfRimText.Key("ClashOfRim.WorldCatalog.SummaryIdeos"),
                ReadWorldIdeoCatalog(configuration).Count.ToString(CultureInfo.InvariantCulture))
        };
    }

    private static ModWorldConfigurationExtensionDto? BuildWorldIdeoCatalogExtension(
        IReadOnlyList<ModWorldIdeoSummaryDto> ideos)
    {
        if (ideos.Count == 0)
        {
            return null;
        }

        return new ModWorldConfigurationExtensionDto
        {
            ProviderId = IdeologyCompatibilityKeys.PackageId,
            Kind = IdeologyCompatibilityKeys.WorldIdeoCatalog,
            SchemaVersion = "1",
            PayloadJson = WorldConfigurationExtensionPayloadJson.Serialize(ideos),
            Metadata = new Dictionary<string, string?>
            {
                ["count"] = ideos.Count.ToString(CultureInfo.InvariantCulture)
            }
        };
    }
}
