using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Plugins;

public readonly record struct WorldConfigurationExtensionKey(string ProviderId, string Kind);

public sealed record WorldConfigurationExtensionContext(
    string UserId,
    string ColonyId,
    string WorldConfigurationId);

public sealed record WorldConfigurationExtensionSnapshotContext(
    LatestSnapshotRecord Snapshot,
    string UserId,
    string ColonyId,
    IReadOnlyList<WorldConfigurationExtensionDto>? CurrentExtensions = null);

public sealed record WorldTileFloatLayerValue(int Tile, float Value);

public sealed record WorldTileFloatLayerProjection(
    string LayerId,
    IReadOnlyList<WorldTileFloatLayerValue> Values);

public sealed record WorldTileFloatLayerIncrease(
    string LayerId,
    int Tile,
    float PreviousValue,
    float CurrentValue,
    float Delta);

public sealed record WorldTileFloatLayerNotificationContext(
    string ActorLabel);

public sealed record WorldTileFloatLayerIncreaseNotification(
    string Kind,
    string Title,
    string Message,
    ServerNotificationSeverity Severity,
    int RadiusTiles);

public interface IWorldConfigurationExtensionProvider
{
    IReadOnlyList<WorldConfigurationExtensionKey> HandledKeys { get; }

    IReadOnlyList<WorldConfigurationExtensionDto> NormalizeSubmittedExtensions(
        WorldConfigurationExtensionContext context,
        IReadOnlyList<WorldConfigurationExtensionDto> incoming);

    IReadOnlyList<WorldConfigurationExtensionDto> MergeExtensions(
        WorldConfigurationExtensionContext context,
        IReadOnlyList<WorldConfigurationExtensionDto> current,
        IReadOnlyList<WorldConfigurationExtensionDto> incoming);

    IReadOnlyList<WorldConfigurationExtensionDto> RemoveColonyExtensions(
        string userId,
        string colonyId,
        IReadOnlyList<WorldConfigurationExtensionDto> current);

    IReadOnlyList<WorldConfigurationExtensionDto> BuildDeliveryExtensions(
        IReadOnlyList<WorldConfigurationExtensionDto> current);

    IReadOnlyList<WorldConfigurationExtensionDto> BuildExtensionsFromAcceptedSnapshot(
        WorldConfigurationExtensionSnapshotContext context);
}

public interface IWorldTileFloatLayerExtensionProvider : IWorldConfigurationExtensionProvider
{
    IReadOnlyList<string> HandledTileFloatLayers { get; }

    IReadOnlyList<string> ConfirmOnAcceptedSnapshotLayers { get; }

    IReadOnlyList<WorldTileFloatLayerValue> ReadTileFloatLayer(
        string layerId,
        IReadOnlyList<WorldConfigurationExtensionDto> extensions);

    WorldConfigurationExtensionDto? BuildTileFloatLayerExtension(
        string layerId,
        IReadOnlyList<WorldTileFloatLayerValue> values);

    IReadOnlyList<WorldTileFloatLayerValue> ProjectTileFloatLayerFromSave(string layerId, byte[] saveBytes);

    WorldTileFloatLayerIncreaseNotification? BuildIncreaseNotification(
        WorldTileFloatLayerNotificationContext context,
        WorldTileFloatLayerIncrease increase);
}

public sealed class WorldConfigurationExtensionService
{
    public static WorldConfigurationExtensionService Empty { get; } = new(Array.Empty<IWorldConfigurationExtensionProvider>());

    private readonly IReadOnlyList<IWorldConfigurationExtensionProvider> providers;
    private readonly HashSet<WorldConfigurationExtensionKey> handledKeys;

    public WorldConfigurationExtensionService(IReadOnlyList<IWorldConfigurationExtensionProvider>? providers)
    {
        this.providers = providers?.ToList() ?? new List<IWorldConfigurationExtensionProvider>();
        handledKeys = this.providers
            .SelectMany(provider => provider.HandledKeys)
            .ToHashSet();
    }

    public IReadOnlyList<IWorldConfigurationExtensionProvider> Providers => providers;

    public IReadOnlyList<WorldConfigurationExtensionDto> NormalizeSubmittedExtensions(
        WorldConfigurationExtensionContext context,
        IReadOnlyList<WorldConfigurationExtensionDto>? incoming)
    {
        var extensions = new List<WorldConfigurationExtensionDto>();
        foreach (IWorldConfigurationExtensionProvider provider in providers)
        {
            extensions.AddRange(TryInvokeProvider(
                nameof(NormalizeSubmittedExtensions),
                provider,
                () => provider.NormalizeSubmittedExtensions(context, incoming ?? Array.Empty<WorldConfigurationExtensionDto>())));
        }

        return Deduplicate(extensions);
    }

    public IReadOnlyList<WorldConfigurationExtensionDto> MergeExtensions(
        WorldConfigurationExtensionContext context,
        IReadOnlyList<WorldConfigurationExtensionDto>? current,
        IReadOnlyList<WorldConfigurationExtensionDto>? incoming)
    {
        var extensions = new List<WorldConfigurationExtensionDto>();
        foreach (IWorldConfigurationExtensionProvider provider in providers)
        {
            extensions.AddRange(TryInvokeProvider(
                nameof(MergeExtensions),
                provider,
                () => provider.MergeExtensions(
                    context,
                    current ?? Array.Empty<WorldConfigurationExtensionDto>(),
                    incoming ?? Array.Empty<WorldConfigurationExtensionDto>())));
        }

        return Deduplicate(extensions);
    }

    public IReadOnlyList<WorldConfigurationExtensionDto> RemoveColonyExtensions(
        string userId,
        string colonyId,
        IReadOnlyList<WorldConfigurationExtensionDto>? current)
    {
        var extensions = new List<WorldConfigurationExtensionDto>();
        foreach (IWorldConfigurationExtensionProvider provider in providers)
        {
            extensions.AddRange(TryInvokeProvider(
                nameof(RemoveColonyExtensions),
                provider,
                () => provider.RemoveColonyExtensions(
                    userId,
                    colonyId,
                    current ?? Array.Empty<WorldConfigurationExtensionDto>())));
        }

        return Deduplicate(extensions);
    }

    public IReadOnlyList<WorldConfigurationExtensionDto> BuildDeliveryExtensions(
        IReadOnlyList<WorldConfigurationExtensionDto>? current,
        bool includeWorldExtensions)
    {
        if (!includeWorldExtensions)
        {
            return Array.Empty<WorldConfigurationExtensionDto>();
        }

        var extensions = new List<WorldConfigurationExtensionDto>();
        foreach (IWorldConfigurationExtensionProvider provider in providers)
        {
            extensions.AddRange(TryInvokeProvider(
                nameof(BuildDeliveryExtensions),
                provider,
                () => provider.BuildDeliveryExtensions(current ?? Array.Empty<WorldConfigurationExtensionDto>())));
        }

        return Deduplicate(extensions);
    }

    public IReadOnlyList<WorldConfigurationExtensionDto> BuildExtensionsFromAcceptedSnapshot(
        WorldConfigurationExtensionSnapshotContext context)
    {
        var extensions = new List<WorldConfigurationExtensionDto>();
        foreach (IWorldConfigurationExtensionProvider provider in providers)
        {
            extensions.AddRange(TryInvokeProvider(
                nameof(BuildExtensionsFromAcceptedSnapshot),
                provider,
                () => provider.BuildExtensionsFromAcceptedSnapshot(context)));
        }

        return Deduplicate(extensions);
    }

    public IReadOnlyList<WorldTileFloatLayerValue> ReadTileFloatLayer(
        IReadOnlyList<WorldConfigurationExtensionDto>? extensions,
        string layerId)
    {
        if (string.IsNullOrWhiteSpace(layerId))
        {
            return Array.Empty<WorldTileFloatLayerValue>();
        }

        return TileFloatLayerProviders(layerId)
            .SelectMany(provider => TryInvokeProvider(
                nameof(ReadTileFloatLayer),
                provider,
                () => provider.ReadTileFloatLayer(layerId, extensions ?? Array.Empty<WorldConfigurationExtensionDto>())))
            .Where(tile => tile.Tile >= 0 && tile.Value > 0f)
            .GroupBy(tile => tile.Tile)
            .Select(group => new WorldTileFloatLayerValue(group.Key, group.Max(tile => Math.Clamp(tile.Value, 0f, 1f))))
            .OrderBy(tile => tile.Tile)
            .ToList();
    }

    public IReadOnlyList<WorldTileFloatLayerValue> ProjectTileFloatLayerFromSave(byte[] saveBytes, string layerId)
    {
        ArgumentNullException.ThrowIfNull(saveBytes);
        if (string.IsNullOrWhiteSpace(layerId))
        {
            return Array.Empty<WorldTileFloatLayerValue>();
        }

        return TileFloatLayerProviders(layerId)
            .SelectMany(provider => TryInvokeProvider(
                nameof(ProjectTileFloatLayerFromSave),
                provider,
                () => provider.ProjectTileFloatLayerFromSave(layerId, saveBytes)))
            .Where(tile => tile.Tile >= 0 && tile.Value > 0f)
            .GroupBy(tile => tile.Tile)
            .Select(group => new WorldTileFloatLayerValue(group.Key, group.Max(tile => Math.Clamp(tile.Value, 0f, 1f))))
            .OrderBy(tile => tile.Tile)
            .ToList();
    }

    public IReadOnlyList<WorldTileFloatLayerProjection> ProjectConfirmOnAcceptedSnapshotLayersFromSave(byte[] saveBytes)
    {
        ArgumentNullException.ThrowIfNull(saveBytes);

        var projections = new List<WorldTileFloatLayerProjection>();
        foreach (IWorldTileFloatLayerExtensionProvider provider in providers.OfType<IWorldTileFloatLayerExtensionProvider>())
        {
            foreach (string layerId in provider.ConfirmOnAcceptedSnapshotLayers.Where(layerId => !string.IsNullOrWhiteSpace(layerId)))
            {
                IReadOnlyList<WorldTileFloatLayerValue> values = TryInvokeProvider(
                        nameof(ProjectConfirmOnAcceptedSnapshotLayersFromSave),
                        provider,
                        () => provider.ProjectTileFloatLayerFromSave(layerId, saveBytes))
                    .Where(tile => tile.Tile >= 0 && tile.Value > 0f)
                    .GroupBy(tile => tile.Tile)
                    .Select(group => new WorldTileFloatLayerValue(
                        group.Key,
                        group.Max(tile => Math.Clamp(tile.Value, 0f, 1f))))
                    .OrderBy(tile => tile.Tile)
                    .ToList();
                if (values.Count > 0)
                {
                    projections.Add(new WorldTileFloatLayerProjection(layerId, values));
                }
            }
        }

        return projections;
    }

    public WorldTileFloatLayerIncreaseNotification? BuildIncreaseNotification(
        WorldTileFloatLayerNotificationContext context,
        WorldTileFloatLayerIncrease increase)
    {
        foreach (IWorldTileFloatLayerExtensionProvider provider in TileFloatLayerProviders(increase.LayerId))
        {
            WorldTileFloatLayerIncreaseNotification? notification = TryInvokeProvider(
                nameof(BuildIncreaseNotification),
                provider,
                () => provider.BuildIncreaseNotification(context, increase));
            if (notification is not null)
            {
                return notification;
            }
        }

        return null;
    }

    public IReadOnlyList<WorldConfigurationExtensionDto> ReplaceTileFloatLayer(
        IReadOnlyList<WorldConfigurationExtensionDto>? current,
        string layerId,
        IReadOnlyList<WorldTileFloatLayerValue> values)
    {
        var extensions = new List<WorldConfigurationExtensionDto>();
        foreach (IWorldConfigurationExtensionProvider provider in providers)
        {
            if (provider is IWorldTileFloatLayerExtensionProvider layerProvider
                && layerProvider.HandledTileFloatLayers.Contains(layerId, StringComparer.Ordinal))
            {
                WorldConfigurationExtensionDto? layerExtension = TryInvokeProvider(
                    nameof(ReplaceTileFloatLayer),
                    layerProvider,
                    () => layerProvider.BuildTileFloatLayerExtension(layerId, values));
                if (layerExtension is not null)
                {
                    extensions.Add(layerExtension);
                }
            }
            else
            {
                extensions.AddRange(TryInvokeProvider(
                    nameof(ReplaceTileFloatLayer),
                    provider,
                    () => provider.BuildDeliveryExtensions(current ?? Array.Empty<WorldConfigurationExtensionDto>())));
            }
        }

        return Deduplicate(extensions);
    }

    private IReadOnlyList<WorldConfigurationExtensionDto> Deduplicate(IEnumerable<WorldConfigurationExtensionDto> extensions)
    {
        return extensions
            .Where(extension => IsHandled(extension) && !string.IsNullOrWhiteSpace(extension.PayloadJson))
            .GroupBy(extension => new WorldConfigurationExtensionKey(extension.ProviderId, extension.Kind))
            .Select(group => group.Last())
            .OrderBy(extension => extension.ProviderId, StringComparer.Ordinal)
            .ThenBy(extension => extension.Kind, StringComparer.Ordinal)
            .ToList();
    }

    private bool IsHandled(WorldConfigurationExtensionDto extension)
    {
        return handledKeys.Contains(new WorldConfigurationExtensionKey(extension.ProviderId, extension.Kind));
    }

    private IEnumerable<IWorldTileFloatLayerExtensionProvider> TileFloatLayerProviders(string layerId)
    {
        return providers
            .OfType<IWorldTileFloatLayerExtensionProvider>()
            .Where(provider => provider.HandledTileFloatLayers.Contains(layerId, StringComparer.Ordinal));
    }

    private static IReadOnlyList<T> TryInvokeProvider<T>(
        string operation,
        object provider,
        Func<IReadOnlyList<T>> callback)
    {
        try
        {
            return callback() ?? Array.Empty<T>();
        }
        catch (Exception ex)
        {
            LogProviderException(operation, provider, ex);
            return Array.Empty<T>();
        }
    }

    private static T? TryInvokeProvider<T>(
        string operation,
        object provider,
        Func<T?> callback)
        where T : class
    {
        try
        {
            return callback();
        }
        catch (Exception ex)
        {
            LogProviderException(operation, provider, ex);
            return null;
        }
    }

    private static void LogProviderException(string operation, object provider, Exception ex)
    {
        Console.Error.WriteLine(
            "[ClashOfRim][ServerPlugin][Error] WorldConfigurationExtensionProviderCallbackException: "
            + provider.GetType().FullName
            + " failed during "
            + operation
            + ": "
            + ex.GetType().Name
            + " "
            + ex.Message);
    }
}
