using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Sessions;

internal readonly record struct PlayerSnapshotWealthSummary(
    string ColonyId,
    string? CurrentSnapshotId,
    int? LatestSnapshotWealth);

internal sealed class PlayerSnapshotWealth(PlayerRegistry players, IColonySnapshotIndexStore snapshots)
{
    internal PlayerSnapshotWealthSummary CacheLatest(
        string userId,
        string colonyId,
        string? currentSnapshotId,
        DateTimeOffset nowUtc,
        LatestSnapshotLookup? snapshotLookup = null)
    {
        LatestSnapshotRecord? snapshot = snapshotLookup is null
            ? LatestSnapshotLookup.Resolve(snapshots, userId, colonyId)
            : snapshotLookup.Resolve(userId, colonyId);
        if (snapshot is null)
        {
            return new PlayerSnapshotWealthSummary(colonyId, currentSnapshotId, null);
        }

        string snapshotOwnerId = string.IsNullOrWhiteSpace(snapshot.Identity.OwnerId)
            ? userId
            : snapshot.Identity.OwnerId!;
        string snapshotColonyId = string.IsNullOrWhiteSpace(snapshot.Identity.ColonyId)
            ? colonyId
            : snapshot.Identity.ColonyId!;
        int? wealth = SnapshotWealth.Total(snapshot.Index)
            ?? FromPackage(snapshotOwnerId, snapshotColonyId, snapshot.Identity);
        players.RecordLatestSnapshotWealth(
            snapshotOwnerId,
            snapshotColonyId,
            snapshot.Identity.SnapshotId,
            wealth,
            nowUtc);
        return new PlayerSnapshotWealthSummary(snapshotColonyId, snapshot.Identity.SnapshotId, wealth);
    }

    private int? FromPackage(
        string userId,
        string colonyId,
        SnapshotIdentity identity)
    {
        if (snapshots is not IColonySnapshotPackageStore packageStore)
        {
            return null;
        }

        SaveSnapshotPackage? package = packageStore.GetLatestPackage(userId, colonyId);
        if (package is null
            || !string.Equals(package.Envelope.Identity.SnapshotId, identity.SnapshotId, StringComparison.Ordinal))
        {
            return null;
        }

        return SnapshotWealth.Total(package.Index);
    }

    internal int ForRaid(
        string ownerUserId,
        string colonyId,
        LatestSnapshotRecord snapshot,
        DateTimeOffset nowUtc)
    {
        string snapshotOwnerId = string.IsNullOrWhiteSpace(snapshot.Identity.OwnerId)
            ? ownerUserId
            : snapshot.Identity.OwnerId!;
        string snapshotColonyId = string.IsNullOrWhiteSpace(snapshot.Identity.ColonyId)
            ? colonyId
            : snapshot.Identity.ColonyId!;
        string? snapshotId = snapshot.Identity.SnapshotId;
        PlayerSessionRecord? player = players.FindByUserId(snapshotOwnerId);
        if (player is not null
            && string.Equals(player.ColonyId, snapshotColonyId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(snapshotId)
            && string.Equals(player.LatestSnapshotWealthSnapshotId, snapshotId, StringComparison.Ordinal)
            && player.LatestSnapshotWealth.HasValue)
        {
            return Math.Max(0, player.LatestSnapshotWealth.Value);
        }

        int? wealth = SnapshotWealth.Total(snapshot.Index)
            ?? FromPackage(snapshotOwnerId, snapshotColonyId, snapshot.Identity);
        if (wealth.HasValue)
        {
            players.RecordLatestSnapshotWealth(
                snapshotOwnerId,
                snapshotColonyId,
                snapshotId,
                wealth,
                nowUtc);
            return Math.Max(0, wealth.Value);
        }

        return int.MaxValue;
    }
}
