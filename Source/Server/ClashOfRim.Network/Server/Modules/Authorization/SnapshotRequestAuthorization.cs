using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Authorization;

internal sealed class SnapshotRequestAuthorization(
    SessionPrincipalAuthorization principals, ColonyAccessAuthorization colonies, SnapshotMutationCoordinator mutations)
{
    internal ProtocolResponse? AuthorizePrincipal(string? token, ProtocolIdentity identity) =>
        principals.AuthorizeOwner(token, identity.UserId, identity.ColonyId, DateTimeOffset.UtcNow);

    internal RecoveredSnapshotMutation EnterPrincipal(string? token, ProtocolIdentity identity,
        [CallerMemberName] string operation = "") =>
        mutations.Enter(() => AuthorizePrincipal(token, identity), operation);

    internal RecoveredSnapshotMutation EnterColony(string? token, string userId, string colonyId,
        [CallerMemberName] string operation = "") =>
        mutations.Enter(() => colonies.Authorize(token, userId, colonyId, null, null, DateTimeOffset.UtcNow), operation);

    internal RecoveredSnapshotMutation EnterColony(string userId, string colonyId, string snapshotId,
        string? token, string? principalToken, [CallerMemberName] string operation = "") =>
        mutations.Enter(() =>
        {
            return AuthorizePrincipal(principalToken, new ProtocolIdentity(userId, colonyId, snapshotId))
                ?? colonies.Authorize(token, userId, colonyId, null, null, DateTimeOffset.UtcNow);
        }, operation);
}
