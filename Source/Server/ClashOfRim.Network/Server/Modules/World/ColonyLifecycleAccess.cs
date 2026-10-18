using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal sealed class ColonyLifecycleAccess(ColonyAccessAuthorization authorization, PlayerRegistry players)
{
    internal ProtocolResponse? Authorize(string? token, string userId, string colonyId)
    {
        if (players.IsDeleted(userId, colonyId))
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerText.Format("Auth.IdentityDenied"));
        return authorization.Authorize(token, userId, colonyId, null, null, DateTimeOffset.UtcNow);
    }
}
