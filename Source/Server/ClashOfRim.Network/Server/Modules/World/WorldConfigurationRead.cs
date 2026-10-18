using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal sealed class WorldConfigurationRead(AccountService accounts, WorldConfigurationRegistry world, WorldConfigurationDelivery delivery)
{
    internal GetWorldConfigurationResponse Get(GetWorldConfigurationRequest request)
    {
        if (request.ProtocolVersion != ProtocolApiVersion.Current)
            return new(ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, ServerLocalization.Text("Protocol.IncompatibleVersion")), null);
        if (string.IsNullOrWhiteSpace(request.UserId))
            return new(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("WorldSession.MissingIdentity")), null);
        var auth = accounts.Authenticate(request.UserId, request.SteamAuthTicket, request.Password, DateTimeOffset.UtcNow);
        if (!auth.Accepted || string.IsNullOrWhiteSpace(auth.AuthenticatedUserId))
            return new(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, auth.Message ?? ServerLocalization.Text("Steam.Failed")), null);
        var configuration = delivery.Build(world.Current, request.IncludeGenerationBaseline, request.IncludePlayerColonySites, request.IncludeWorldExtensions);
        return configuration is null
            ? new(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("WorldSession.WaitingForAdmin")), null)
            : new(ProtocolResponse.Ok(ServerLocalization.Text("Compatibility.Validated")), configuration);
    }
}
