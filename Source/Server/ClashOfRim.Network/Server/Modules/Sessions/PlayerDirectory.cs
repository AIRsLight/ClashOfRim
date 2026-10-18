using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Sessions;

internal sealed class PlayerDirectory(PlayerColonyCatalog catalog, PlayerSnapshotWealth wealth,
    DiplomacyRelationRegistry relations, OnlinePresenceRegistry online, SnapshotMutationCoordinator mutations)
{
    internal ListPlayersResponse List(ListPlayersRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
            return new(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Players.MissingIdentity")), []);
        // This public read protocol has no credential. Caller fields select a view, never publish player activity.
        using var mutation = mutations.EnterUnrecovered();
        var view = catalog.Capture();
        var result = view.Colonies.Select(colony =>
        {
            var player = colony.Player;
            bool cached = player.ColonyId == colony.ColonyId && player.LatestSnapshotWealthSnapshotId == colony.SnapshotId;
            int? value = cached ? player.LatestSnapshotWealth
                : wealth.CacheLatest(player.UserId, colony.ColonyId, colony.SnapshotId, player.LastSeenAtUtc, view.Snapshots).LatestSnapshotWealth;
            return new PlayerSummaryDto(player.UserId, colony.ColonyId, colony.SnapshotId, online.IsUserOnline(player.UserId),
                player.LastSeenAtUtc, player.DisplayName,
                relations.GetRelationKind(request.UserId, request.ColonyId, player.UserId, colony.ColonyId), value);
        }).ToArray();
        return new(ProtocolResponse.Ok(ServerLocalization.Text("Players.Returned")), result);
    }
}
