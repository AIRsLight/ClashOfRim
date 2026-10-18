using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Achievements;

internal sealed class AchievementQueries(AchievementRegistry achievements, PlayerColonyCatalog catalog, SnapshotMutationCoordinator mutations)
{
    internal ListAchievementsResponse List(ListAchievementsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
            return new(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Achievements.MissingIdentity")), [], []);
        using var mutation = mutations.EnterUnrecovered();
        string userId = string.IsNullOrWhiteSpace(request.TargetUserId) ? request.UserId : request.TargetUserId.Trim();
        string colonyId = string.IsNullOrWhiteSpace(request.TargetColonyId) ? request.ColonyId : request.TargetColonyId.Trim();
        var visible = catalog.Capture().Colonies.ToDictionary(colony => (colony.Player.UserId, colony.ColonyId));
        var aggregates = achievements.ListAggregates().Where(record => visible.ContainsKey((record.UserId, record.ColonyId))).ToArray();
        var own = aggregates.Where(record => record.UserId == userId && record.ColonyId == colonyId)
            .OrderBy(record => record.Category, StringComparer.Ordinal).ThenBy(record => record.AchievementId, StringComparer.Ordinal)
            .Select(record => new AchievementSummaryDto(record.AchievementId, record.Category, record.LabelKey,
                DescriptionKey(record.LabelKey, record.AchievementId), record.IconId, record.Color, record.Value, record.SourceSnapshotId)).ToArray();
        var boards = aggregates.GroupBy(record => record.AchievementId, StringComparer.Ordinal).Select(group =>
        {
            var first = group.First();
            var entries = group.OrderByDescending(record => record.Value).ThenBy(record => record.UserId, StringComparer.Ordinal)
                .ThenBy(record => record.ColonyId, StringComparer.Ordinal).Take(10).Select(record =>
                    new AchievementLeaderboardEntryDto(record.UserId, record.ColonyId, visible[(record.UserId, record.ColonyId)].Player.DisplayName,
                        record.Value, record.SourceSnapshotId)).ToArray();
            return new AchievementLeaderboardDto(first.AchievementId, first.Category, first.LabelKey,
                DescriptionKey(first.LabelKey, first.AchievementId), first.IconId, first.Color, entries);
        }).OrderBy(board => board.Category, StringComparer.Ordinal).ThenBy(board => board.AchievementId, StringComparer.Ordinal).ToArray();
        return new(ProtocolResponse.Ok(ServerLocalization.Text("Achievements.Returned")), boards, own);
    }

    private static string DescriptionKey(string? labelKey, string? achievementId)
        => !string.IsNullOrWhiteSpace(labelKey) ? labelKey.Trim() + ".Description"
            : string.IsNullOrWhiteSpace(achievementId) ? "ClashOfRim.Achievement.Unknown.Description"
            : "ClashOfRim.Achievement." + achievementId.Trim() + ".Description";
}
