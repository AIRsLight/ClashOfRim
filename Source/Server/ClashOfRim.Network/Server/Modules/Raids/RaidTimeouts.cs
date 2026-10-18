using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using static AIRsLight.ClashOfRim.Network.Modules.Raids.RaidTimeoutRules;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal sealed class RaidTimeouts(IAuthoritativeEventLedger ledger, IColonySnapshotIndexStore snapshots,
    SnapshotPostUploadJobRegistry jobs, SnapshotMutationCoordinator mutations, OnlinePresenceRegistry presence,
    EventNotificationHub notifications, RaidOfflineEvidence offline, RaidBattleCleanup cleanup,
    Func<ClashOfRimServerConfiguration> configuration)
{
    internal RaidTimeoutProcessingResult Reconcile(
        DateTimeOffset nowUtc)
    {
        RaidTimeoutProcessingResult result;
        using (mutations.Enter())
        {
            result = ExpireUnderMutation(nowUtc);
        }
        return CompletePending(result, nowUtc);
    }

    internal RaidTimeoutProcessingResult ExpireUnderMutation(DateTimeOffset nowUtc)
        => RaidTimeoutProcessor.ProcessExpiredRaids(ledger, ledger.ListByType(ServerEventType.Raid), nowUtc,
            RaidPolicies.BuildDefenseLock(configuration()), party => presence.IsUserOnline(party.UserId));

    internal void RecoverPending(DateTimeOffset nowUtc)
        => CompletePending(new RaidTimeoutProcessingResult([], [], [], []), nowUtc);

    internal RaidTimeoutProcessingResult CompletePending(RaidTimeoutProcessingResult result, DateTimeOffset nowUtc)
    {
        IReadOnlyList<AuthoritativeEvent> pendingTimeouts;
        using (mutations.Enter())
        {
            pendingTimeouts = RaidTimeoutProcessor.PendingWork(ledger);
        }
        if (pendingTimeouts.Count == 0) return result;
        var offlinePending = pendingTimeouts.Where(raid => raid.Payload is RaidEventPayload { TimeoutWork: { AttackerWasOffline: true } }).ToArray();
        // Retain offline evidence and its durable job before permitting attacker cleanup; preparation owns no outer lock.
        var queuedOfflineSettlements = offline.ScheduleAll(
            offlinePending.Where(raid => raid.Status != ServerEventStatus.AppliedToSnapshot).ToArray(), nowUtc);
        var offlineRaidIds = offlinePending.Select(raid => raid.EventId).ToHashSet(StringComparer.Ordinal);
        var cleanupEligibleRaids = pendingTimeouts.Where(raid => !offlineRaidIds.Contains(raid.EventId)
            || queuedOfflineSettlements.Contains(raid.EventId) || raid.Status == ServerEventStatus.AppliedToSnapshot).ToArray();

        var cleanupEvents = cleanup.CleanupTimeouts(cleanupEligibleRaids, nowUtc);
        var usersToSignal = result.NotificationEvents.Select(evt => evt.Target.UserId)
            .Concat(result.AttackerLossEvents.Select(evt => evt.Target.UserId))
            .Concat(cleanupEvents.AttackerLossEvents.Select(evt => evt.Target.UserId))
            .Concat(cleanupEvents.SupportLossNotifications.Select(evt => evt.Target.UserId))
            .Where(userId => !string.IsNullOrWhiteSpace(userId)).Distinct(StringComparer.Ordinal).ToArray();
        notifications.SignalUsers(usersToSignal);
        // A stale completion cannot acknowledge changed timeout work or a changed source.
        using (mutations.Enter())
        {
            lock (jobs.SnapshotCommitGate)
            {
                foreach (var completed in cleanupEvents.CompletedRaids)
                {
                    var current = ledger.Find(completed.Raid.EventId);
                    if (!SameSource(current, completed.Raid) || !CanFinish(jobs, current!)
                        || !SnapshotCommitService.SameVersion(snapshots.GetLatest(current!.Actor.UserId, current.Actor.ColonyId!)?.Envelope,
                            completed.Snapshot)) continue;
                    notifications.SignalUsers(new[] { current!.Actor.UserId, current.Target.UserId });
                    RaidTimeoutProcessor.CompleteWork(ledger, current.EventId);
                }
            }
        }
        return result;
    }

}
