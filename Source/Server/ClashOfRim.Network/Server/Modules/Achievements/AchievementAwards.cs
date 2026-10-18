using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.Logging;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.Achievements;

internal sealed class AchievementAwards(AchievementRegistry registry, ServerPluginRegistry plugins,
    CompatibilityBaselineRegistry baseline, IAuthoritativeEventLedger ledger, OnlinePresenceRegistry presence,
    EventNotificationHub notifications, Func<ILogger> logger)
{
    internal void RecordRelocation(
        string userId,
        string colonyId,
        string sourceSnapshotId,
        DateTimeOffset nowUtc)
    {
        RecordCumulative(
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

    internal void RecordCumulative(
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

        IReadOnlyList<AchievementEventRecord> newlyRecorded = registry.RecordCumulativeMetricAchievements(
            userId!,
            colonyId!,
            sourceEventKey,
            sourceSnapshotId,
            metricDeltas,
            plugins.ActiveAchievementDefinitions(baseline.Current),
            nowUtc, includePreviouslyRecorded: true);
        if (newlyRecorded.Count > 0)
        {
            Notify(userId!, colonyId!, newlyRecorded, nowUtc);
        }
    }

    internal void Notify(
        string userId,
        string colonyId,
        IReadOnlyCollection<AchievementEventRecord> achievements,
        DateTimeOffset nowUtc)
    {
        bool hasNotifications = false;
        foreach (AchievementEventRecord achievement in achievements)
        {
            string notificationId = "achievement-unlocked:"
                + achievement.UserId
                + ":"
                + achievement.ColonyId
                + ":"
                + achievement.AchievementId
                + ":"
                + achievement.EventKey;
            string label = ResolveAchievementNotificationLabel(achievement);
            AuthoritativeEvent notification = AuthoritativeEventFactory.CreateForLedger(ledger,
                ServerEventType.ServerNotification,
                new EventParty("server"),
                new EventParty(userId, colonyId),
                notificationId,
                presence.IsUserOnline(userId),
                new ServerNotificationEventPayload(
                    notificationId,
                    Format("Achievements.UnlockedTitle"),
                    Format(
                        "Achievements.UnlockedMessage",
                        ("ACHIEVEMENT", label),
                        ("VALUE", achievement.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                    ServerNotificationSeverity.Info,
                    FromAdministrator: false,
                    RelatedUserId: achievement.UserId,
                    RelatedColonyId: achievement.ColonyId),
                nowUtc);
            LedgerAppendResult append = ledger.Append(notification);
            EventDiagnostics.LogAppend(logger(), append, "achievement-unlocked");
            hasNotifications = true; // Retry wake-up even when the notification was persisted before interruption.
        }

        if (hasNotifications)
        {
            notifications.SignalUser(userId);
        }
    }

    private static string ResolveAchievementNotificationLabel(AchievementEventRecord achievement)
    {
        if (!string.IsNullOrWhiteSpace(achievement.LabelKey))
        {
            try
            {
                return Format(achievement.LabelKey);
            }
            catch (KeyNotFoundException)
            {
            }
        }

        return string.IsNullOrWhiteSpace(achievement.AchievementId)
            ? Format("Achievements.UnknownLabel")
            : achievement.AchievementId;
    }

}
