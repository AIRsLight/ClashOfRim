using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static RecoveredSnapshotMutation EnterPrincipalSnapshotMutation(HttpRequest http, ClashOfRimNetworkState state,
        ProtocolIdentity principal, [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
        => EnterRecoveredSnapshotMutation(state, () => ReauthorizeSnapshotPrincipal(http, state, principal)
            ? null : ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")), operation);

    private static RecoveredSnapshotMutation EnterColonySnapshotMutation(HttpRequest http, ClashOfRimNetworkState state,
        string userId, string colonyId, string snapshotId, string? token,
        [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
        => EnterRecoveredSnapshotMutation(state, () =>
        {
            if (!ReauthorizeSnapshotPrincipal(http, state, new ProtocolIdentity(userId, colonyId, snapshotId)))
                return ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed"));
            return IsAuthorizedForColony(state, token, userId, colonyId, null, null, DateTimeOffset.UtcNow, out string failure)
                ? null : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, failure);
        }, operation);

    private static SnapshotUploadResult PrepareTransactionSnapshot(ClashOfRimNetworkState state, string userId, string colonyId,
        string snapshotId, SnapshotPackageMetadataDto metadata, byte[] payload, DateTimeOffset at)
    {
        string? kind = NormalizeSnapshotUploadKind(metadata.SnapshotUploadKind);
        if (kind == SnapshotUploadKinds.RaidSettlementEvidence)
            return SnapshotUploadResult.Reject(SnapshotUploadResultKind.InvalidPayload,
                "Raid settlement evidence snapshots are only accepted by the raid settlement confirmation endpoint.");
        return SnapshotPreparationService.Prepare(CreateSnapshotReceiver(state),
            new SnapshotUploadContext(userId, colonyId, snapshotId, SnapshotUploadKind: kind),
            ProtocolDtoMapper.ToSaveSnapshotPackage(metadata, payload), at);
    }
}
