using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.Logging;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal sealed class WorldConfigurationSubmission(AdministratorAuthorization authorization, CompatibilityBaselineRegistry baseline,
    ServerPluginRegistry plugins, SnapshotMutationCoordinator mutations, WorldConfigurationRegistry world,
    PlayerSessionActivity activity, WorldConfigurationDelivery delivery, Func<ILogger> logger)
{
    internal SubmitWorldConfigurationResponse Submit(SubmitWorldConfigurationRequest request)
    {
        SubmitWorldConfigurationResponse Reject(ProtocolResponse failure) => new(failure, false, false, null, null);
        if (!string.Equals(request.ProtocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, Format("Protocol.IncompatibleVersion")));
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId) || request.Configuration is null)
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("WorldConfiguration.MissingRequest")));
        ProtocolResponse? Authorize() => authorization.Authorize(request.UserId, request.ColonyId, request.AuthToken);
        if (Authorize() is { } initialFailure) return Reject(initialFailure);

        // Extension normalization can be expensive. Keep it outside the mutation gate,
        // and refuse to publish if its compatibility policy changed while preparing.
        CompatibilityManifest? compatibility = baseline.Current;
        WorldConfigurationExtensionService activeWorldExtensions = plugins.ActiveWorldConfigurationExtensions(compatibility);
        WorldConfigurationDto configuration = NormalizeWorldConfiguration(
            request.Configuration, request.UserId, request.ColonyId, activeWorldExtensions);
        if (WorldGenerationBaseline.ValidationFailure(configuration) is { } failureKey)
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                Format("WorldConfiguration.InvalidBaseline", ("REASON", Format(failureKey)))));
        ProtocolResponse? AuthorizePrepared()
        {
            if (Authorize() is { } failure) return failure;
            return ReferenceEquals(compatibility, baseline.Current) ? null
                : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, Format("WorldConfiguration.CompatibilityChanged"));
        }
        using (var recovery = mutations.Enter(AuthorizePrepared))
        {
            if (recovery.Rejection is { } failure) return Reject(failure);
            WorldSessionState after = world.Submit(request.UserId, configuration, activeWorldExtensions);
            if (!after.IsAdministrator)
                return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, Format("Admin.Unauthorized")));
            activity.Record(request.UserId, request.ColonyId, currentSnapshotId: null, DateTimeOffset.UtcNow);
            logger().LogInformation(
                "世界基线已提交：user={UserId} colony={ColonyId} administrator={AdministratorUserId} configured={WorldConfigured} playerSites={PlayerSiteCount}",
                request.UserId, request.ColonyId, after.AdministratorUserId, after.WorldConfigured,
                after.WorldConfiguration?.PlayerColonySites.Count ?? 0);
            return new SubmitWorldConfigurationResponse(
                ProtocolResponse.Ok(Format("WorldConfiguration.Registered")), after.IsAdministrator, after.WorldConfigured,
                after.AdministratorUserId, delivery.Build(after.WorldConfiguration));
        }
    }

    private static WorldConfigurationDto NormalizeWorldConfiguration(
        WorldConfigurationDto configuration,
        string userId,
        string colonyId,
        WorldConfigurationExtensionService worldExtensions)
    {
        string worldConfigurationId = string.IsNullOrWhiteSpace(configuration.WorldConfigurationId)
            ? $"world:{Guid.NewGuid():N}"
            : configuration.WorldConfigurationId;
        IReadOnlyList<WorldConfigurationExtensionDto> extensions =
            worldExtensions.NormalizeSubmittedExtensions(
                new WorldConfigurationExtensionContext(userId, colonyId, worldConfigurationId),
                configuration.Extensions);
        return new WorldConfigurationDto(
            worldConfigurationId,
            userId,
            colonyId,
            configuration.ConfiguredAtUtc == default ? DateTimeOffset.UtcNow : configuration.ConfiguredAtUtc,
            configuration.SeedString,
            configuration.PlanetCoverage,
            configuration.OverallRainfall,
            configuration.OverallTemperature,
            configuration.OverallPopulation,
            configuration.LandmarkDensity,
            configuration.TileCount,
            configuration.FactionDefNames,
            configuration.Features,
            configuration.Factions,
            configuration.Roads,
            configuration.WorldObjects,
            configuration.PlayerColonySites,
            configuration.StorytellerDefName,
            configuration.DifficultyDefName,
            configuration.TileGeometry,
            configuration.DifficultyValuesXml,
            extensions,
            configuration.GameLanguage,
            configuration.FeatureNameCatalogs);
    }

}
