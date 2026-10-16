using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static RecoveredSnapshotMutation EnterEventReadMutation(ClashOfRimNetworkState state,
        string? token, string userId, string colonyId, string snapshotId,
        [System.Runtime.CompilerServices.CallerMemberName] string operation = "") =>
        EnterRecoveredSnapshotMutation(state,
            () => ValidateEventRead(state, token, userId, colonyId, snapshotId), operation);

    // Called under the snapshot gate, including again after recovery or a long poll.
    private static ProtocolResponse? ValidateEventRead(ClashOfRimNetworkState state,
        string? token, string userId, string colonyId, string snapshotId)
    {
        if (!IsAuthorizedForColony(state, token, userId, colonyId, null, null, DateTimeOffset.UtcNow, out string failure))
            return ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, failure);
        if (string.IsNullOrWhiteSpace(snapshotId)
            || state.SnapshotStore.GetLatest(userId, colonyId)?.Identity.SnapshotId != snapshotId)
            return ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("Events.SnapshotNotLatest"));
        return null;
    }

    private static EventQueueSummaryDto EmptyEventQueue() => new([], [], [], [], []);

    private static EventQueueSummaryDto BuildColonyEventQueue(ClashOfRimNetworkState state, string userId, string colonyId) =>
        ProtocolDtoMapper.ToDto(EventQueueSummaryBuilder.BuildForTarget(userId,
            state.Ledger.ListQueueForTarget(userId).Where(evt => IsVisibleParty(evt.Target, userId, colonyId))));
}
