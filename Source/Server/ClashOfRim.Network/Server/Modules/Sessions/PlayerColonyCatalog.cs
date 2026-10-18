using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Sessions;

internal sealed record VisiblePlayerColony(PlayerSessionRecord Player, string ColonyId, string SnapshotId);
internal sealed record PlayerColonyCatalogSnapshot(LatestSnapshotLookup Snapshots, IReadOnlyList<VisiblePlayerColony> Colonies);

internal sealed class PlayerColonyCatalog(PlayerRegistry players, IColonySnapshotIndexStore snapshots)
{
    // Capture under the shared mutation boundary so both directory and ranking use the same visibility rules.
    internal PlayerColonyCatalogSnapshot Capture()
    {
        var lookup = LatestSnapshotLookup.Build(snapshots.ListLatest().Where(snapshot =>
            !string.IsNullOrWhiteSpace(snapshot.Identity.OwnerId)
            && (string.IsNullOrWhiteSpace(snapshot.Identity.ColonyId)
                || !players.IsDeleted(snapshot.Identity.OwnerId, snapshot.Identity.ColonyId))).ToArray());
        var visible = new List<VisiblePlayerColony>();
        foreach (var player in players.List())
        {
            var snapshot = lookup.Resolve(player.UserId, player.ColonyId);
            string colonyId = string.IsNullOrWhiteSpace(snapshot?.Identity.ColonyId) ? player.ColonyId : snapshot.Identity.ColonyId;
            string? snapshotId = snapshot is null ? player.CurrentSnapshotId : snapshot.Identity.SnapshotId;
            if (!string.IsNullOrWhiteSpace(player.UserId) && !string.IsNullOrWhiteSpace(colonyId)
                && !string.IsNullOrWhiteSpace(snapshotId) && !players.IsDeleted(player.UserId, colonyId))
                visible.Add(new(player, colonyId, snapshotId));
        }
        return new(lookup, visible);
    }
}
