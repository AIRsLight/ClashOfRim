using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.Logging;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal sealed class WorldFeatureNames(AccountService accounts, WorldConfigurationRegistry world,
    WorldConfigurationDelivery delivery, WorldConfigurationChanges changes, Func<ILogger> logger)
{
    internal SubmitWorldFeatureNamesResponse Submit(SubmitWorldFeatureNamesRequest request)
    {
        SubmitWorldFeatureNamesResponse Reject(ProtocolResponse failure) => new(failure, false, false, delivery.Build(world.Current));
        if (request.ProtocolVersion != ProtocolApiVersion.Current)
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, Format("Protocol.IncompatibleVersion")));
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.Language) || string.IsNullOrWhiteSpace(request.WorldConfigurationId) || request.Features.Count == 0)
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("WorldConfiguration.FeatureNamesMissingRequest")));
        var auth = accounts.Authenticate(request.UserId, request.SteamAuthTicket, request.Password, DateTimeOffset.UtcNow);
        if (!auth.Accepted || string.IsNullOrWhiteSpace(auth.AuthenticatedUserId))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, auth.Message ?? Format("Steam.Failed")));
        var result = world.SubmitWorldFeatureNames(request.UserId, request.ColonyId, request.Language, request.WorldConfigurationId, request.Features);
        var delivered = delivery.Build(result.WorldConfiguration);
        if (!result.Accepted)
            return new(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                Format("WorldConfiguration.FeatureNamesRejected", ("REASON", result.Message))), false, false, delivered);
        if (result.Created)
        {
            changes.Signal(request.UserId);
            logger().LogInformation("世界地区名目录已提交：user={UserId} colony={ColonyId} language={Language} features={FeatureCount}",
                request.UserId, request.ColonyId, request.Language, request.Features.Count);
        }
        return new(ProtocolResponse.Ok(Format(result.Created ? "WorldConfiguration.FeatureNamesRegistered" : "WorldConfiguration.FeatureNamesAlreadyRegistered")),
            true, result.Created, delivered);
    }
}
