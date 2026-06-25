using AIRsLight.ClashOfRim.Events;

namespace AIRsLight.ClashOfRim.Network.Plugins;

public interface IAuthoritativeEventAchievementMetricProvider
{
    void CollectAuthoritativeEventAchievementMetrics(AuthoritativeEventAchievementMetricContext context);
}

public sealed class AuthoritativeEventAchievementMetricContext
{
    public AuthoritativeEventAchievementMetricContext(
        AuthoritativeEvent ledgerEvent,
        IDictionary<string, long> metricDeltas)
    {
        LedgerEvent = ledgerEvent;
        MetricDeltas = metricDeltas;
    }

    public AuthoritativeEvent LedgerEvent { get; }

    public IDictionary<string, long> MetricDeltas { get; }

    public void AddMetric(string metricId, long delta)
    {
        if (string.IsNullOrWhiteSpace(metricId) || delta <= 0)
        {
            return;
        }

        string normalized = metricId.Trim();
        MetricDeltas.TryGetValue(normalized, out long current);
        MetricDeltas[normalized] = current + delta;
    }
}
