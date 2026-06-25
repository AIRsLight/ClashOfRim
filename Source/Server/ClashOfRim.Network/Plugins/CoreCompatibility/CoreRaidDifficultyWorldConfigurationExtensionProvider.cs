using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Plugins.CoreCompatibility;

internal sealed class CoreRaidDifficultyWorldConfigurationExtensionProvider : IWorldConfigurationExtensionProvider
{
    private static readonly IReadOnlyList<WorldConfigurationExtensionKey> Keys = new[]
    {
        new WorldConfigurationExtensionKey(
            CoreRaidDifficultyServerCompatibility.ProviderId,
            CoreRaidDifficultyServerCompatibility.RaidDifficultyBaseline)
    };

    public IReadOnlyList<WorldConfigurationExtensionKey> HandledKeys => Keys;

    public IReadOnlyList<WorldConfigurationExtensionDto> NormalizeSubmittedExtensions(
        WorldConfigurationExtensionContext context,
        IReadOnlyList<WorldConfigurationExtensionDto> incoming)
    {
        WorldConfigurationExtensionDto? extension =
            CoreRaidDifficultyServerCompatibility.BuildBaselineExtension(
                CoreRaidDifficultyServerCompatibility.ReadBaseline(incoming));
        return extension is null ? Array.Empty<WorldConfigurationExtensionDto>() : new[] { extension };
    }

    public IReadOnlyList<WorldConfigurationExtensionDto> MergeExtensions(
        WorldConfigurationExtensionContext context,
        IReadOnlyList<WorldConfigurationExtensionDto> current,
        IReadOnlyList<WorldConfigurationExtensionDto> incoming)
    {
        CoreRaidDifficultyBaselineDto? baseline =
            CoreRaidDifficultyServerCompatibility.ReadBaseline(incoming)
            ?? CoreRaidDifficultyServerCompatibility.ReadBaseline(current);
        WorldConfigurationExtensionDto? extension = CoreRaidDifficultyServerCompatibility.BuildBaselineExtension(baseline);
        return extension is null ? Array.Empty<WorldConfigurationExtensionDto>() : new[] { extension };
    }

    public IReadOnlyList<WorldConfigurationExtensionDto> RemoveColonyExtensions(
        string userId,
        string colonyId,
        IReadOnlyList<WorldConfigurationExtensionDto> current)
    {
        return BuildDeliveryExtensions(current);
    }

    public IReadOnlyList<WorldConfigurationExtensionDto> BuildDeliveryExtensions(
        IReadOnlyList<WorldConfigurationExtensionDto> current)
    {
        WorldConfigurationExtensionDto? extension =
            CoreRaidDifficultyServerCompatibility.BuildBaselineExtension(
                CoreRaidDifficultyServerCompatibility.ReadBaseline(current));
        return extension is null ? Array.Empty<WorldConfigurationExtensionDto>() : new[] { extension };
    }

    public IReadOnlyList<WorldConfigurationExtensionDto> BuildExtensionsFromAcceptedSnapshot(
        WorldConfigurationExtensionSnapshotContext context)
    {
        return Array.Empty<WorldConfigurationExtensionDto>();
    }
}
