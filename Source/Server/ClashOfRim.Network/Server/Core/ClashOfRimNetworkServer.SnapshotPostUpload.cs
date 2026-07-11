using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static void RunSnapshotPostUploadProcessors(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        string? sessionId,
        SnapshotUploadResult upload,
        DateTimeOffset nowUtc,
        SnapshotPostUploadExtraData? extraData = null,
        bool registerPlayerColonySite = true,
        IReadOnlyCollection<SnapshotAchievementCandidateDto>? achievementCandidates = null)
    {
        if (!upload.Accepted || upload.AcceptedSnapshot is null)
        {
            return;
        }

        extraData ??= new SnapshotPostUploadExtraData(upload.SnapshotUploadKind, null, null);
        if (achievementCandidates is not null)
        {
            extraData = extraData with { AchievementCandidates = achievementCandidates };
        }
        else if (string.IsNullOrWhiteSpace(extraData.SnapshotUploadKind)
            && !string.IsNullOrWhiteSpace(upload.SnapshotUploadKind))
        {
            extraData = extraData with { SnapshotUploadKind = upload.SnapshotUploadKind };
        }

        RunSnapshotPostUploadProcessors(
            state,
            new SnapshotPostUploadContext(
                ResolveSnapshotPostUploadKind(extraData.SnapshotUploadKind),
                upload.AcceptedSnapshot,
                userId,
                colonyId,
                sessionId,
                nowUtc,
                extraData,
                registerPlayerColonySite));
    }

    private static void RunSnapshotPostUploadProcessors(
        ClashOfRimNetworkState state,
        SnapshotPostUploadContext context)
    {
        SnapshotUploadResult acceptedUpload = SnapshotUploadResult.Accept(
            context.Snapshot,
            context.ExtraData.SnapshotUploadKind);

        if (ProcessesAuthoritativeColonyState(context.Kind))
        {
            RecordAcceptedSnapshotReference(
                state,
                context.UserId,
                context.ColonyId,
                acceptedUpload,
                context.OccurredAtUtc);
            ProcessWorldTileFloatLayersFromAcceptedSnapshot(
                state,
                context.UserId,
                context.ColonyId,
                acceptedUpload,
                context.OccurredAtUtc);

            if (context.RegisterPlayerColonySite)
            {
                TryRegisterPlayerColonySiteFromAcceptedSnapshot(
                    state,
                    context.UserId,
                    context.ColonyId,
                    acceptedUpload);
            }

            RecordSnapshotMetricsAndAchievements(
                state,
                context.UserId,
                context.ColonyId,
                context.Snapshot,
                context.OccurredAtUtc,
                context.ExtraData.AchievementCandidates);
        }

        ProcessSupportPawnDeathsFromSnapshot(state, acceptedUpload);
        RunSnapshotUploadedLifecycleHooks(
            state,
            context.UserId,
            context.ColonyId,
            context.SessionId,
            acceptedUpload,
            context.OccurredAtUtc);
    }

    private static bool ProcessesAuthoritativeColonyState(SnapshotPostUploadKind kind)
    {
        return kind == SnapshotPostUploadKind.AuthoritativeColonySnapshot;
    }

    private static SnapshotPostUploadKind ResolveSnapshotPostUploadKind(string? snapshotUploadKind)
    {
        return string.Equals(snapshotUploadKind, SnapshotUploadKinds.RaidSettlementEvidence, StringComparison.Ordinal)
            ? SnapshotPostUploadKind.RaidSettlementEvidence
            : SnapshotPostUploadKind.AuthoritativeColonySnapshot;
    }

    private static void RecordSnapshotMetricsAndAchievements(
        ClashOfRimNetworkState state,
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
        int? wealth = CalculateSnapshotWealth(snapshot);
        if (wealth.HasValue)
        {
            metrics[AchievementRegistry.MetricColonyWealth] = wealth.Value;
            state.Players.RecordLatestSnapshotWealth(
                ownerId,
                colonyId,
                snapshotId,
                wealth.Value,
                nowUtc);
        }

        int colonistCount = CalculateSnapshotPlayerColonists(state, snapshot);
        metrics[AchievementRegistry.MetricPlayerColonists] = colonistCount;
        AddMetricIfPresent(metrics, AchievementRegistry.MetricWealthItems, snapshot.Index.HistoryWealthItems);
        AddMetricIfPresent(metrics, AchievementRegistry.MetricWealthBuildings, snapshot.Index.HistoryWealthBuildings);
        AddMetricIfPresent(metrics, AchievementRegistry.MetricWealthPawns, snapshot.Index.HistoryWealthPawns);
        AddMetricIfPresent(metrics, AchievementRegistry.MetricPrisoners, snapshot.Index.HistoryPrisonerCount);
        AddMetricIfPresent(metrics, AchievementRegistry.MetricColonistMood, snapshot.Index.HistoryColonistMood);
        AddMetricIfPresent(metrics, AchievementRegistry.MetricColonistsLaunched, snapshot.Index.StoryColonistsLaunched);

        CollectPluginSnapshotAchievementMetrics(state, snapshot, metrics);

        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return;
        }

        var newlyRecordedAchievements = new List<AchievementEventRecord>();
        newlyRecordedAchievements.AddRange(state.Achievements.RecordSnapshotMetricAchievements(
            ownerId,
            colonyId,
            snapshotId!,
            metrics,
            state.Plugins.ActiveAchievementDefinitions(state.CompatibilityBaseline.Current),
            nowUtc));

        foreach (SnapshotAchievementCandidateDto candidate in achievementCandidates ?? Array.Empty<SnapshotAchievementCandidateDto>())
        {
            if (state.Achievements.TryRecord(ownerId, colonyId, snapshotId!, candidate, nowUtc, out AchievementEventRecord? record))
            {
                newlyRecordedAchievements.Add(record!);
            }
        }

        if (newlyRecordedAchievements.Count > 0)
        {
            NotifyUnlockedAchievements(state, ownerId, colonyId, newlyRecordedAchievements, nowUtc);
        }
    }

    private static void NotifyUnlockedAchievements(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        IReadOnlyCollection<AchievementEventRecord> achievements,
        DateTimeOffset nowUtc)
    {
        bool createdAny = false;
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
            AuthoritativeEvent notification = AuthoritativeEventFactory.Create(
                ServerEventType.ServerNotification,
                new EventParty("server"),
                new EventParty(userId, colonyId),
                notificationId,
                state.OnlinePresence.IsUserOnline(userId),
                new ServerNotificationEventPayload(
                    notificationId,
                    T("Achievements.UnlockedTitle"),
                    T(
                        "Achievements.UnlockedMessage",
                        ("ACHIEVEMENT", label),
                        ("VALUE", achievement.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                    ServerNotificationSeverity.Info,
                    FromAdministrator: false,
                    RelatedUserId: achievement.UserId,
                    RelatedColonyId: achievement.ColonyId),
                nowUtc);
            LedgerAppendResult append = state.Ledger.Append(notification);
            LogEventAppend(state, append, "achievement-unlocked");
            createdAny |= append.Created;
        }

        if (createdAny)
        {
            state.EventNotifications.SignalUser(userId);
        }
    }

    private static string ResolveAchievementNotificationLabel(AchievementEventRecord achievement)
    {
        if (!string.IsNullOrWhiteSpace(achievement.LabelKey))
        {
            try
            {
                return T(achievement.LabelKey);
            }
            catch (KeyNotFoundException)
            {
            }
        }

        return string.IsNullOrWhiteSpace(achievement.AchievementId)
            ? T("Achievements.UnknownLabel")
            : achievement.AchievementId;
    }

    private static void AddMetricIfPresent(IDictionary<string, long> metrics, string metricId, int? value)
    {
        if (value.HasValue)
        {
            metrics[metricId] = Math.Max(0, value.Value);
        }
    }

    private static int CalculateSnapshotPlayerColonists(ClashOfRimNetworkState state, LatestSnapshotRecord snapshot)
    {
        if (snapshot.Index.HistoryPlayerColonistCount is int historyPlayerColonistCount)
        {
            return Math.Max(0, historyPlayerColonistCount);
        }

        IReadOnlyList<SnapshotColonyAnchor> anchors = ExtractSnapshotColonyAnchors(state, snapshot.Index);
        if (anchors.Count == 0)
        {
            return 0;
        }

        var colonyMapIds = new HashSet<string>(StringComparer.Ordinal);
        var colonyWorldObjectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (SnapshotColonyAnchor anchor in anchors)
        {
            if (!string.IsNullOrWhiteSpace(anchor.MapUniqueId))
            {
                colonyMapIds.Add(anchor.MapUniqueId!);
            }

            if (!string.IsNullOrWhiteSpace(anchor.WorldObjectId))
            {
                colonyWorldObjectIds.Add(anchor.WorldObjectId!);
            }
        }

        int total = 0;
        foreach (MapSummary map in snapshot.Index.Maps)
        {
            string? mapUniqueId = NormalizeSnapshotMapUniqueId(map.UniqueId);
            if ((string.IsNullOrWhiteSpace(mapUniqueId) || !colonyMapIds.Contains(mapUniqueId!))
                && (string.IsNullOrWhiteSpace(map.ParentWorldObjectId) || !colonyWorldObjectIds.Contains(map.ParentWorldObjectId!)))
            {
                continue;
            }

            total += Math.Max(0, map.PlayerColonistCount);
        }

        return total;
    }

    private static void CollectPluginSnapshotAchievementMetrics(
        ClashOfRimNetworkState state,
        LatestSnapshotRecord snapshot,
        IDictionary<string, long> metrics)
    {
        foreach (ISnapshotAchievementMetricProvider provider in
            state.Plugins.ActiveSnapshotAchievementMetricProviders(state.CompatibilityBaseline.Current))
        {
            try
            {
                provider.CollectSnapshotAchievementMetrics(new SnapshotAchievementMetricContext(snapshot, metrics));
            }
            catch (Exception ex)
            {
                state.RuntimeLogger.LogWarning(
                    ex,
                    "Snapshot achievement metric provider failed: provider={Provider} snapshot={SnapshotId}",
                    provider.GetType().FullName,
                    snapshot.Identity.SnapshotId);
            }
        }
    }

    private enum SnapshotPostUploadKind
    {
        AuthoritativeColonySnapshot,
        RaidSettlementEvidence
    }

    private sealed record SnapshotPostUploadContext(
        SnapshotPostUploadKind Kind,
        LatestSnapshotRecord Snapshot,
        string UserId,
        string ColonyId,
        string? SessionId,
        DateTimeOffset OccurredAtUtc,
        SnapshotPostUploadExtraData ExtraData,
        bool RegisterPlayerColonySite);

    private sealed record SnapshotPostUploadExtraData(
        string? SnapshotUploadKind,
        string? ConfirmationOperation,
        IReadOnlyCollection<SnapshotAchievementCandidateDto>? AchievementCandidates)
    {
        public static SnapshotPostUploadExtraData Empty { get; } = new(null, null, null);
    }
}
