using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;

internal sealed class IdeologyWorldConfigurationExtensionProvider : IWorldConfigurationExtensionProvider
{
    private static readonly IReadOnlyList<WorldConfigurationExtensionKey> Keys = new[]
    {
        new WorldConfigurationExtensionKey(
            IdeologyCompatibilityKeys.PackageId,
            IdeologyCompatibilityKeys.WorldIdeoCatalog)
    };

    public IReadOnlyList<WorldConfigurationExtensionKey> HandledKeys => Keys;

    public IReadOnlyList<WorldConfigurationExtensionDto> NormalizeSubmittedExtensions(
        WorldConfigurationExtensionContext context,
        IReadOnlyList<WorldConfigurationExtensionDto> incoming)
    {
        IReadOnlyList<WorldIdeoSummaryDto> normalized = IdeologyServerCompatibility.NormalizeSubmittedIdeos(
            IdeologyServerCompatibility.ReadWorldIdeoCatalog(incoming),
            context.UserId,
            context.ColonyId,
            context.WorldConfigurationId);
        WorldConfigurationExtensionDto? extension = IdeologyServerCompatibility.BuildWorldIdeoCatalogExtension(normalized);
        return extension is null ? Array.Empty<WorldConfigurationExtensionDto>() : new[] { extension };
    }

    public IReadOnlyList<WorldConfigurationExtensionDto> MergeExtensions(
        WorldConfigurationExtensionContext context,
        IReadOnlyList<WorldConfigurationExtensionDto> current,
        IReadOnlyList<WorldConfigurationExtensionDto> incoming)
    {
        IReadOnlyList<WorldIdeoSummaryDto> normalizedIncoming = IdeologyServerCompatibility.NormalizeSubmittedIdeos(
            IdeologyServerCompatibility.ReadWorldIdeoCatalog(incoming),
            context.UserId,
            context.ColonyId,
            context.WorldConfigurationId);
        IReadOnlyList<WorldIdeoSummaryDto> merged = IdeologyServerCompatibility.MergeWorldIdeos(
            IdeologyServerCompatibility.BuildCurrentWorldIdeos(current),
            normalizedIncoming,
            context.UserId,
            context.ColonyId);
        WorldConfigurationExtensionDto? extension = IdeologyServerCompatibility.BuildWorldIdeoCatalogExtension(merged);
        return extension is null ? Array.Empty<WorldConfigurationExtensionDto>() : new[] { extension };
    }

    public IReadOnlyList<WorldConfigurationExtensionDto> RemoveColonyExtensions(
        string userId,
        string colonyId,
        IReadOnlyList<WorldConfigurationExtensionDto> current)
    {
        IReadOnlyList<WorldIdeoSummaryDto> retained = IdeologyServerCompatibility.BuildCurrentWorldIdeos(current)
            .Where(ideo => !string.Equals(ideo.OwnerUserId, userId, StringComparison.Ordinal)
                || !string.Equals(ideo.OwnerColonyId, colonyId, StringComparison.Ordinal))
            .ToList();
        WorldConfigurationExtensionDto? extension = IdeologyServerCompatibility.BuildWorldIdeoCatalogExtension(retained);
        return extension is null ? Array.Empty<WorldConfigurationExtensionDto>() : new[] { extension };
    }

    public IReadOnlyList<WorldConfigurationExtensionDto> BuildDeliveryExtensions(
        IReadOnlyList<WorldConfigurationExtensionDto> current)
    {
        WorldConfigurationExtensionDto? extension = IdeologyServerCompatibility.BuildWorldIdeoCatalogExtension(
            IdeologyServerCompatibility.BuildCurrentWorldIdeos(current));
        return extension is null ? Array.Empty<WorldConfigurationExtensionDto>() : new[] { extension };
    }

    public IReadOnlyList<WorldConfigurationExtensionDto> BuildExtensionsFromAcceptedSnapshot(
        WorldConfigurationExtensionSnapshotContext context)
    {
        WorldConfigurationExtensionDto? extension = IdeologyServerCompatibility.BuildWorldIdeoCatalogExtension(
            IdeologyServerCompatibility.BuildWorldIdeosFromAcceptedSnapshot(
                context.Snapshot,
                context.UserId,
                context.ColonyId,
                context.CurrentExtensions));
        return extension is null ? Array.Empty<WorldConfigurationExtensionDto>() : new[] { extension };
    }
}
