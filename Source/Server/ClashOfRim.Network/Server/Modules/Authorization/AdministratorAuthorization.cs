using AIRsLight.ClashOfRim.Protocol;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.Authorization;

internal sealed class AdministratorAuthorization(SessionPrincipalAuthorization principals, WorldConfigurationRegistry world)
{
    internal ProtocolResponse? Authorize(string userId, string? colonyId, string? token,
        ProtocolErrorCode rejectionCode = ProtocolErrorCode.ServerRejected)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(colonyId))
            return ProtocolResponse.Reject(rejectionCode, Format("Admin.IdentityMissing"));
        return principals.AuthorizeOwner(token, userId, colonyId, DateTimeOffset.UtcNow, rejectionCode, "Admin.Unauthorized")
            ?? (world.IsAdministrator(userId) ? null : ProtocolResponse.Reject(rejectionCode, Format("Admin.Unauthorized")));
    }
}
