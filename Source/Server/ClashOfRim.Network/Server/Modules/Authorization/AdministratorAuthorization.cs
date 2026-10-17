using AIRsLight.ClashOfRim.Protocol;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.Authorization;

internal sealed class AdministratorAuthorization(AuthTokenRegistry tokens, LoginSessionRegistry sessions, WorldConfigurationRegistry world)
{
    internal ProtocolResponse? Authorize(string userId, string? colonyId, string? token) =>
        TryAuthorize(userId, colonyId ?? string.Empty, token, DateTimeOffset.UtcNow, out string failure)
            ? null : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, failure);

    internal bool TryAuthorize(string userId, string colonyId, string? token, DateTimeOffset nowUtc, out string failure)
    {
        failure = Format("Admin.Unauthorized");
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(colonyId))
        { failure = Format("Admin.IdentityMissing"); return false; }
        if (!tokens.TryGetPrincipal(token, nowUtc, out var principal) || principal is null
            || !string.Equals(principal.UserId, userId, StringComparison.Ordinal)
            || !string.Equals(principal.ColonyId, colonyId, StringComparison.Ordinal)) return false;
        if (!sessions.Refresh(principal.UserId, principal.ColonyId, principal.SessionId, nowUtc))
        { failure = Format("Auth.SessionExpired"); return false; }
        return world.IsAdministrator(principal.UserId);
    }
}
