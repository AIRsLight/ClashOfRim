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
            AuthoritativeEvent failed = ledger.ChangeStatus(raid.EventId, ServerEventStatus.Failed);
            failedRaids.Add(failed);
            if (attackerOffline)
            {
                offlineFailedRaids.Add(failed);
            }

            notifications.Add(AppendTimeoutNotification(
                ledger,
                failed,
                failed.Actor,
                ServerLocalization.Text("Raid.TimeoutAttackerTitle"),
                ServerLocalization.Text("Raid.TimeoutAttackerMessage", new Dictionary<string, string?> { ["EVENT"] = raid.EventId }),
                nowUtc).Event);
            notifications.Add(AppendTimeoutNotification(
                ledger,
                failed,
                failed.Target,
                ServerLocalization.Text("Raid.TimeoutDefenderTitle"),
                ServerLocalization.Text("Raid.TimeoutDefenderMessage", new Dictionary<string, string?> { ["EVENT"] = raid.EventId }),
                nowUtc).Event);
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

    private static LedgerAppendResult AppendTimeoutNotification(
        IAuthoritativeEventLedger ledger,
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
                RelatedEventType: raid.Type.ToString(),
                RelatedUserId: raid.Target.UserId,
                RelatedColonyId: raid.Target.ColonyId),
            nowUtc,
            raid.TargetContext);

        return ledger.Append(notification);
    }
}
