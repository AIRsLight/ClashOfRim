using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal sealed class LatestSnapshotLookup
{
    private readonly Dictionary<(string User, string Colony), LatestSnapshotRecord> snapshotsByColony;
    private readonly Dictionary<string, LatestSnapshotRecord> latestSnapshotsByUser;

    private LatestSnapshotLookup(
        Dictionary<(string User, string Colony), LatestSnapshotRecord> snapshotsByColony,
        Dictionary<string, LatestSnapshotRecord> latestSnapshotsByUser)
    {
        this.snapshotsByColony = snapshotsByColony;
        this.latestSnapshotsByUser = latestSnapshotsByUser;
    }

    internal static LatestSnapshotRecord? Resolve(IColonySnapshotIndexStore snapshots, string userId, string colonyId)
        => snapshots.GetLatest(userId, colonyId) ?? Build(snapshots.ListLatest()).Resolve(userId, colonyId);

    public static LatestSnapshotLookup Build(IReadOnlyList<LatestSnapshotRecord> snapshots)
    {
        var byColony = new Dictionary<(string User, string Colony), LatestSnapshotRecord>();
        var byUser = new Dictionary<string, LatestSnapshotRecord>(StringComparer.Ordinal);
        foreach (LatestSnapshotRecord snapshot in snapshots)
        {
            string? ownerId = snapshot.Identity.OwnerId;
            string? colonyId = snapshot.Identity.ColonyId;
            if (string.IsNullOrWhiteSpace(ownerId))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(colonyId))
            {
                byColony[(ownerId!, colonyId!)] = snapshot;
            }

            if (!byUser.TryGetValue(ownerId!, out LatestSnapshotRecord? existing)
                || snapshot.AcceptedAtUtc > existing.AcceptedAtUtc)
            {
                byUser[ownerId!] = snapshot;
            }
        }

        return new LatestSnapshotLookup(byColony, byUser);
    }

    public LatestSnapshotRecord? Resolve(string userId, string colonyId)
    {
        if (!string.IsNullOrWhiteSpace(userId)
            && !string.IsNullOrWhiteSpace(colonyId)
            && snapshotsByColony.TryGetValue((userId, colonyId), out LatestSnapshotRecord? exact))
        {
            return exact;
        }

        return ForUser(userId);
    }

    internal LatestSnapshotRecord? ForUser(string userId)
        => !string.IsNullOrWhiteSpace(userId)
            && latestSnapshotsByUser.TryGetValue(userId, out LatestSnapshotRecord? latest)
                ? latest
                : null;

}
