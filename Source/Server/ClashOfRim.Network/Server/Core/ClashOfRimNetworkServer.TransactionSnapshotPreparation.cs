using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static RecoveredSnapshotMutation EnterColonySnapshotMutation(ClashOfRimNetworkState state,
        string? token, string userId, string colonyId, [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
        => EnterRecoveredSnapshotMutation(state, () => IsAuthorizedForColony(state, token, userId, colonyId,
            null, null, DateTimeOffset.UtcNow, out string failure)
                ? null : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, failure), operation);

    private static RecoveredSnapshotMutation EnterPrincipalSnapshotMutation(HttpRequest http, ClashOfRimNetworkState state,
        ProtocolIdentity principal, [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
        => EnterRecoveredSnapshotMutation(state, () => ReauthorizeSnapshotPrincipal(http, state, principal)
            ? null : ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")), operation);

    private static RecoveredSnapshotMutation EnterColonySnapshotMutation(HttpRequest http, ClashOfRimNetworkState state,
        string userId, string colonyId, string snapshotId, string? token,
        [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
        => state.SnapshotRequests.EnterColony(userId, colonyId, snapshotId, token,
            http.Headers[MultipartSnapshotAuthentication.HeaderName].FirstOrDefault(), operation);

    private static SnapshotUploadResult PrepareTransactionSnapshot(ClashOfRimNetworkState state, string userId, string colonyId,
        string snapshotId, SnapshotPackageMetadataDto metadata, byte[] payload, DateTimeOffset at)
        => state.SnapshotTransactions.PrepareTransactionSnapshot(userId, colonyId, snapshotId, metadata, payload, at);
}
