using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Events;

public static class RaidTimeoutProcessor
{
    public static RaidTimeoutProcessingResult ProcessExpiredRaids(
        IAuthoritativeEventLedger ledger,
        IEnumerable<AuthoritativeEvent> events,
        DateTimeOffset nowUtc,
        RaidDefenseLockPolicy? policy = null,
        Func<EventParty, bool>? isAttackerOnline = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(events);

        RaidDefenseLockPolicy activePolicy = policy ?? RaidDefenseLockPolicy.Default;
        var failedRaids = new List<AuthoritativeEvent>();
        var notifications = new List<AuthoritativeEvent>();
        var attackerLossEvents = new List<AuthoritativeEvent>();
        var offlineFailedRaids = new List<AuthoritativeEvent>();

        foreach (AuthoritativeEvent raid in events.Where(evt => IsExpiredActiveRaid(evt, nowUtc, activePolicy, isAttackerOnline)))
        {
            bool attackerOffline = isAttackerOnline is not null && !isAttackerOnline(raid.Actor);
            var payload = (RaidEventPayload)raid.Payload;
            var timeoutPayload = payload with { TimeoutWork = new RaidTimeoutWork(nowUtc, attackerOffline) };
            var attackerNotification = CreateTimeoutNotification(raid, raid.Actor,
                ServerLocalization.Text("Raid.TimeoutAttackerTitle"),
                ServerLocalization.Text("Raid.TimeoutAttackerMessage", new Dictionary<string, string?> { ["EVENT"] = raid.EventId }), nowUtc);
            var defenderNotification = CreateTimeoutNotification(raid, raid.Target,
                ServerLocalization.Text("Raid.TimeoutDefenderTitle"),
                ServerLocalization.Text("Raid.TimeoutDefenderMessage", new Dictionary<string, string?> { ["EVENT"] = raid.EventId }), nowUtc);
            attackerNotification = AuthoritativeEventFactory.ResolvePersistedIdentity(ledger, attackerNotification);
            defenderNotification = AuthoritativeEventFactory.ResolvePersistedIdentity(ledger, defenderNotification);
            ledger.ApplyBatch(new LedgerBatch([attackerNotification, defenderNotification],
                [new LedgerStatusChange(raid.EventId, raid.Status, ServerEventStatus.Failed, payload, timeoutPayload)]));
            AuthoritativeEvent failed = ledger.Find(raid.EventId)!;
            failedRaids.Add(failed);
            if (attackerOffline)
            {
                offlineFailedRaids.Add(failed);
            }

            notifications.Add(attackerNotification);
            notifications.Add(defenderNotification);
        }

        return new RaidTimeoutProcessingResult(failedRaids, notifications, attackerLossEvents, offlineFailedRaids);
    }

    private static bool IsExpiredActiveRaid(
        AuthoritativeEvent evt,
        DateTimeOffset nowUtc,
        RaidDefenseLockPolicy policy,
        Func<EventParty, bool>? isAttackerOnline)
    {
        if (evt.Type != ServerEventType.Raid ||
            evt.Payload is not RaidEventPayload payload ||
            payload.OpponentKind != RaidOpponentKind.Player ||
            payload.StartedAtUtc == null ||
            payload.FinishedAtUtc != null ||
            payload.Settlement != null ||
            payload.ReturnedSnapshotId != null)
        {
            return false;
        }

        if (evt.Status is ServerEventStatus.AppliedToSnapshot
            or ServerEventStatus.Cancelled
            or ServerEventStatus.Failed
            or ServerEventStatus.RejectedByTarget
            or ServerEventStatus.Conflict)
        {
            return false;
        }

        DateTimeOffset battleDeadline = payload.StartedAtUtc.Value + policy.MaxRaidDuration;
        if (battleDeadline > nowUtc)
        {
            return false;
        }

        if (payload.StartedAtUtc.Value + policy.ServerTimeoutDuration <= nowUtc)
        {
            return true;
        }

        return isAttackerOnline is not null && !isAttackerOnline(evt.Actor);
    }

    public static IReadOnlyList<AuthoritativeEvent> PendingWork(IAuthoritativeEventLedger ledger) =>
        ledger.ListByType(ServerEventType.Raid).Where(item => item.Status is not (ServerEventStatus.Cancelled or ServerEventStatus.RejectedByTarget)
            && item.Payload is RaidEventPayload { TimeoutWork: { Completed: false } }).ToArray();

    public static void CompleteWork(IAuthoritativeEventLedger ledger, string raidEventId)
    {
        var raid = ledger.Find(raidEventId);
        if (raid?.Payload is not RaidEventPayload { TimeoutWork: { Completed: false } work } payload) return;
        ledger.ApplyBatch(new LedgerBatch([], [new LedgerStatusChange(raid.EventId, raid.Status, raid.Status,
            payload, payload with { TimeoutWork = work with { Completed = true } })]));
    }

    private static AuthoritativeEvent CreateTimeoutNotification(
        AuthoritativeEvent raid,
        EventParty target,
        string title,
        string message,
        DateTimeOffset nowUtc)
    {
        AuthoritativeEvent notification = AuthoritativeEventFactory.Create(
            ServerEventType.ServerNotification,
            new EventParty("server"),
            target,
            $"raid-timeout:{raid.EventId}:{target.UserId}",
            targetOnline: false,
            new ServerNotificationEventPayload(
                $"raid-timeout:{raid.EventId}:{target.UserId}",
                title,
                message,
                ServerNotificationSeverity.Warning,
                FromAdministrator: false,
                AdministratorUserId: null,
                RelatedEventId: raid.EventId,
                RelatedEventType: raid.Type,
                RelatedUserId: raid.Target.UserId,
                RelatedColonyId: raid.Target.ColonyId),
            nowUtc,
            raid.TargetContext);

        return notification;
    }
}
