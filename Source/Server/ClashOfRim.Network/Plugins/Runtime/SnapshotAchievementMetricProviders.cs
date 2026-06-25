using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Plugins;

public interface ISnapshotAchievementMetricProvider
{
    void CollectSnapshotAchievementMetrics(SnapshotAchievementMetricContext context);
}

public sealed class SnapshotAchievementMetricContext
{
    public SnapshotAchievementMetricContext(
        LatestSnapshotRecord snapshot,
        IDictionary<string, long> metrics)
    {
        Snapshot = snapshot;
        Metrics = metrics;
    }

    public LatestSnapshotRecord Snapshot { get; }

    public IDictionary<string, long> Metrics { get; }

    public void SetMetric(string metricId, long value)
    {
        if (string.IsNullOrWhiteSpace(metricId))
        {
            return;
        }

        Metrics[metricId.Trim()] = Math.Max(0, value);
    }
}
