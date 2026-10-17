using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Authorization;

internal sealed class SnapshotRequestAuthorization(
    SnapshotPrincipalAuthorization principals, ColonyAccessAuthorization colonies, SnapshotMutationCoordinator mutations)
{
    internal bool ReauthorizePrincipal(string? token, ProtocolIdentity identity) =>
        principals.TryAuthorize(token, DateTimeOffset.UtcNow, out var principal)
        && SnapshotPrincipalAuthorization.Matches(principal!, identity.UserId, identity.ColonyId);

    internal RecoveredSnapshotMutation EnterColony(string userId, string colonyId, string snapshotId,
        string? token, string? principalToken, [CallerMemberName] string operation = "") =>
        mutations.Enter(() =>
        {
            if (!ReauthorizePrincipal(principalToken, new ProtocolIdentity(userId, colonyId, snapshotId)))
                return ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, ServerLocalization.Text("Auth.Failed"));
            return colonies.TryAuthorize(token, userId, colonyId, null, null, DateTimeOffset.UtcNow, out string failure)
                ? null : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, failure);
        }, operation);
}
