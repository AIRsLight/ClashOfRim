using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Authorization;

// Call under the mutation gate, including revalidation after recovery or a long poll.
internal sealed class ColonySnapshotAuthorization(ColonyAccessAuthorization authorization, IColonySnapshotIndexStore snapshots)
{
    internal ProtocolResponse? Authorize(string? token, string userId, string colonyId) =>
        authorization.TryAuthorize(token, userId, colonyId, null, null, DateTimeOffset.UtcNow, out string failure)
            ? null : ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, failure);

    internal ProtocolResponse? RequireCurrent(string userId, string colonyId, string? snapshotId) =>
        string.IsNullOrWhiteSpace(snapshotId) || snapshots.GetLatest(userId, colonyId)?.Identity.SnapshotId != snapshotId
            ? ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, ServerLocalization.Text("Events.SnapshotNotLatest")) : null;

    internal ProtocolResponse? Validate(string? token, string userId, string colonyId, string? snapshotId) =>
        Authorize(token, userId, colonyId) ?? RequireCurrent(userId, colonyId, snapshotId);
}
