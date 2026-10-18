using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network.Modules.Achievements;

internal sealed class SnapshotAchievements(PlayerRegistry players, AchievementRegistry registry,
    AchievementAwards awards, ServerPluginRegistry plugins, CompatibilityBaselineRegistry baseline, Func<ILogger> logger)
{
    internal void Record(
        string fallbackUserId,
        string fallbackColonyId,
        LatestSnapshotRecord snapshot,
        DateTimeOffset nowUtc,
        IReadOnlyCollection<SnapshotAchievementCandidateDto>? achievementCandidates)
    {
        string ownerId = string.IsNullOrWhiteSpace(snapshot.Identity.OwnerId)
            ? fallbackUserId
            : snapshot.Identity.OwnerId!;
        string colonyId = string.IsNullOrWhiteSpace(snapshot.Identity.ColonyId)
            ? fallbackColonyId
            : snapshot.Identity.ColonyId!;
        string? snapshotId = snapshot.Identity.SnapshotId;
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(colonyId))
        {
            return;
        }

        var metrics = new Dictionary<string, long>(StringComparer.Ordinal);
        int? wealth = SnapshotWealth.Total(snapshot.Index);
        if (wealth.HasValue)
        {
            metrics[AchievementRegistry.MetricColonyWealth] = wealth.Value;
            players.RecordLatestSnapshotWealth(
                ownerId,
                colonyId,
                snapshotId,
                wealth.Value,
                nowUtc);
        }

        int colonistCount = CalculateSnapshotPlayerColonists(snapshot);
        metrics[AchievementRegistry.MetricPlayerColonists] = colonistCount;
        AddMetricIfPresent(metrics, AchievementRegistry.MetricWealthItems, snapshot.Index.HistoryWealthItems);
        AddMetricIfPresent(metrics, AchievementRegistry.MetricWealthBuildings, snapshot.Index.HistoryWealthBuildings);
        AddMetricIfPresent(metrics, AchievementRegistry.MetricWealthPawns, snapshot.Index.HistoryWealthPawns);
        AddMetricIfPresent(metrics, AchievementRegistry.MetricPrisoners, snapshot.Index.HistoryPrisonerCount);
        AddMetricIfPresent(metrics, AchievementRegistry.MetricColonistMood, snapshot.Index.HistoryColonistMood);
        AddMetricIfPresent(metrics, AchievementRegistry.MetricColonistsLaunched, snapshot.Index.StoryColonistsLaunched);

        CollectPluginSnapshotAchievementMetrics(snapshot, metrics);

        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return;
        }

        registry.RecordSnapshotMetricAchievements(
            ownerId,
            colonyId,
            snapshotId!,
            metrics,
            plugins.ActiveAchievementDefinitions(baseline.Current),
            nowUtc);

        foreach (SnapshotAchievementCandidateDto candidate in achievementCandidates ?? Array.Empty<SnapshotAchievementCandidateDto>())
        {
            registry.TryRecord(ownerId, colonyId, snapshotId!, candidate, nowUtc, out _);
        }

        // A previous attempt may have persisted an unlock and then failed to append
        // its notification. Replay all durable unlocks from this exact snapshot.
        var snapshotAchievements = registry.ListSnapshotEvents(ownerId, colonyId, snapshotId!);
        if (snapshotAchievements.Count > 0)
        {
            awards.Notify(ownerId, colonyId, snapshotAchievements, nowUtc);
        }
    }

    private static void AddMetricIfPresent(IDictionary<string, long> metrics, string metricId, int? value)
    {
        if (value.HasValue)
        {
            metrics[metricId] = Math.Max(0, value.Value);
        }
    }

    private static int CalculateSnapshotPlayerColonists(LatestSnapshotRecord snapshot)
    {
        return Math.Max(0, snapshot.Index.HistoryPlayerColonistCount ?? 0);
    }

    private void CollectPluginSnapshotAchievementMetrics(
        LatestSnapshotRecord snapshot,
        IDictionary<string, long> metrics)
    {
        foreach (ISnapshotAchievementMetricProvider provider in
            plugins.ActiveSnapshotAchievementMetricProviders(baseline.Current))
        {
            try
            {
                provider.CollectSnapshotAchievementMetrics(new SnapshotAchievementMetricContext(snapshot, metrics));
            }
            catch (Exception ex)
            {
                logger().LogWarning(
                    ex,
                    "Snapshot achievement metric provider failed: provider={Provider} snapshot={SnapshotId}",
                    provider.GetType().FullName,
                    snapshot.Identity.SnapshotId);
            }
        }
    }
}
