using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static void RecordColonyRelocationAchievements(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        string sourceSnapshotId,
        DateTimeOffset nowUtc)
    {
        RecordEventMetricAchievementsForParty(
            state,
            userId,
            colonyId,
            "colony-relocation:" + sourceSnapshotId,
            sourceSnapshotId,
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [AchievementRegistry.MetricColonyRelocations] = 1
            },
            nowUtc);
    }

    private static void RecordAuthoritativeEventAchievements(
        ClashOfRimNetworkState state,
        AuthoritativeEvent ledgerEvent,
        DateTimeOffset nowUtc)
    {
        if (TryBuildCompletedTradeMetrics(ledgerEvent, out IReadOnlyDictionary<string, long>? tradeMetrics))
        {
            RecordEventMetricAchievementsForParty(
                state,
                ledgerEvent.Actor.UserId,
                ledgerEvent.Actor.ColonyId,
                ledgerEvent.EventId + ":actor",
                ResolveEventAchievementSourceSnapshotId(ledgerEvent),
                tradeMetrics!,
                nowUtc);
            RecordEventMetricAchievementsForParty(
                state,
                ledgerEvent.Target.UserId,
                ledgerEvent.Target.ColonyId,
                ledgerEvent.EventId + ":target",
                ResolveEventAchievementSourceSnapshotId(ledgerEvent),
                tradeMetrics!,
                nowUtc);
        }

        IReadOnlyList<IAuthoritativeEventAchievementMetricProvider> providers =
            state.Plugins.ActiveAuthoritativeEventAchievementMetricProviders(state.CompatibilityBaseline.Current);
        if (providers.Count == 0)
        {
            return;
        }

        var pluginMetrics = new Dictionary<string, long>(StringComparer.Ordinal);
        var context = new AuthoritativeEventAchievementMetricContext(ledgerEvent, pluginMetrics);
        foreach (IAuthoritativeEventAchievementMetricProvider provider in providers)
        {
            try
            {
                provider.CollectAuthoritativeEventAchievementMetrics(context);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    "[ClashOfRim][AchievementEventMetricProvider] "
                    + provider.GetType().FullName
                    + " failed: "
                    + ex.Message);
            }
        }

        if (pluginMetrics.Count == 0)
        {
            return;
        }

        RecordEventMetricAchievementsForParty(
            state,
            ledgerEvent.Actor.UserId,
            ledgerEvent.Actor.ColonyId,
            ledgerEvent.EventId + ":plugin:" + ledgerEvent.Actor.UserId + ":" + ledgerEvent.Actor.ColonyId,
            ResolveEventAchievementSourceSnapshotId(ledgerEvent),
            pluginMetrics,
            nowUtc);
    }

    private static void RecordEventMetricAchievementsForParty(
        ClashOfRimNetworkState state,
        string? userId,
        string? colonyId,
        string sourceEventKey,
        string? sourceSnapshotId,
        IReadOnlyDictionary<string, long> metricDeltas,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(colonyId)
            || string.IsNullOrWhiteSpace(sourceEventKey)
            || metricDeltas.Count == 0)
        {
            return;
        }

        IReadOnlyList<AchievementEventRecord> newlyRecorded = state.Achievements.RecordCumulativeMetricAchievements(
            userId!,
            colonyId!,
            sourceEventKey,
            sourceSnapshotId,
            metricDeltas,
            state.Plugins.ActiveAchievementDefinitions(state.CompatibilityBaseline.Current),
            nowUtc);
        if (newlyRecorded.Count > 0)
        {
            NotifyUnlockedAchievements(state, userId!, colonyId!, newlyRecorded, nowUtc);
        }
    }

    private static bool TryBuildCompletedTradeMetrics(
        AuthoritativeEvent ledgerEvent,
        out IReadOnlyDictionary<string, long>? metrics)
    {
        metrics = null;
        if (ledgerEvent.Type != ServerEventType.Trade
            || ledgerEvent.Status != ServerEventStatus.AppliedToSnapshot
            || ledgerEvent.Payload is not TradeEventPayload
            {
                Stage: TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange
            } payload)
        {
            return false;
        }

        long tradeValue = Math.Max(
            CalculateTradeSideValue(payload.OfferedItems),
            CalculateTradeSideValue(payload.RequestedItems));
        var result = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [AchievementRegistry.MetricCompletedTradeCount] = 1
        };
        if (tradeValue > 0)
        {
            result[AchievementRegistry.MetricCompletedTradeValue] = tradeValue;
        }

        metrics = result;
        return true;
    }

    private static long CalculateTradeSideValue(IReadOnlyList<EventThingReference> things)
    {
        long total = 0;
        foreach (EventThingReference thing in things)
        {
            int stackCount = Math.Max(1, thing.StackCount);
            if (thing.MarketValue.HasValue)
            {
                total += Math.Max(0, (long)Math.Round(thing.MarketValue.Value * stackCount));
                continue;
            }

            if (string.Equals(thing.Def, "Silver", StringComparison.Ordinal))
            {
                total += stackCount;
            }
        }

        return total;
    }

    private static string ResolveEventAchievementSourceSnapshotId(AuthoritativeEvent ledgerEvent)
    {
        return FirstNonEmpty(
            ledgerEvent.AppliedSnapshotId,
            ledgerEvent.DeliveredToSnapshotId,
            ledgerEvent.EventId);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value!.Trim();
            }
        }

        return string.Empty;
    }
}
