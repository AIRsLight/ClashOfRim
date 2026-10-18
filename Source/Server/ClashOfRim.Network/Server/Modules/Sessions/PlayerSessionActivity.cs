using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Sessions;

internal sealed class PlayerSessionActivity(PlayerRegistry players, IColonySnapshotIndexStore snapshots)
{
    internal void Record(string userId, string colonyId, string? currentSnapshotId,
        DateTimeOffset seenAtUtc, string? displayName = null)
        => players.Record(userId, colonyId,
            LatestSnapshotLookup.Resolve(snapshots, userId, colonyId)?.Identity.SnapshotId
                ?? (!string.IsNullOrWhiteSpace(currentSnapshotId) ? currentSnapshotId : null),
            seenAtUtc, displayName);
}
