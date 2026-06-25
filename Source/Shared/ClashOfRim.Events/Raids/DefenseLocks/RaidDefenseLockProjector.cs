namespace AIRsLight.ClashOfRim.Events;

public static class RaidDefenseLockProjector
{
    public static RaidDefenseLockStatus BuildForDefender(
        string defenderUserId,
        string? defenderColonyId,
        IEnumerable<AuthoritativeEvent> events,
        DateTimeOffset checkedAtUtc,
        RaidDefenseLockPolicy? policy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defenderUserId);
        ArgumentNullException.ThrowIfNull(events);

        RaidDefenseLockPolicy activePolicy = policy ?? RaidDefenseLockPolicy.Default;
        var completedKeys = new HashSet<string>(StringComparer.Ordinal);
        var candidateRaids = new List<AuthoritativeEvent>();
        foreach (AuthoritativeEvent evt in events)
        {
            if (IsCompletedRaid(evt))
            {
                string completionKey = RaidCompletionKey(evt);
                if (!string.IsNullOrWhiteSpace(completionKey))
                {
                    completedKeys.Add(completionKey);
                }
            }

            if (IsActiveRaidForDefender(evt, defenderUserId, defenderColonyId))
            {
                candidateRaids.Add(evt);
            }
        }

        var locks = new List<RaidDefenseLock>(candidateRaids.Count);
        for (int index = 0; index < candidateRaids.Count; index++)
        {
            AuthoritativeEvent raid = candidateRaids[index];
            if (completedKeys.Contains(RaidCompletionKey(raid)))
            {
                continue;
            }

            RaidDefenseLock lockState = ToLock(raid, activePolicy);
            if (lockState.LockedUntilUtc > checkedAtUtc)
            {
                locks.Add(lockState);
            }
        }

        locks.Sort(static (left, right) => left.LockedUntilUtc.CompareTo(right.LockedUntilUtc));

        return new RaidDefenseLockStatus(defenderUserId, defenderColonyId, checkedAtUtc, locks);
    }

    private static bool IsActiveRaidForDefender(AuthoritativeEvent evt, string defenderUserId, string? defenderColonyId)
    {
        if (evt.Type != ServerEventType.Raid ||
            evt.Payload is not RaidEventPayload payload ||
            payload.OpponentKind != RaidOpponentKind.Player ||
            payload.StartedAtUtc == null ||
            payload.FinishedAtUtc != null ||
            payload.Settlement != null ||
            !string.Equals(evt.Target.UserId, defenderUserId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(defenderColonyId) &&
            !string.Equals(evt.Target.ColonyId, defenderColonyId, StringComparison.Ordinal))
        {
            return false;
        }

        return evt.Status is not ServerEventStatus.AppliedToSnapshot
            and not ServerEventStatus.Cancelled
            and not ServerEventStatus.Failed
            and not ServerEventStatus.RejectedByTarget
            and not ServerEventStatus.Conflict;
    }

    private static bool IsCompletedRaid(AuthoritativeEvent evt)
    {
        return evt.Type == ServerEventType.Raid &&
            evt.Payload is RaidEventPayload { OpponentKind: RaidOpponentKind.Player } payload &&
            (payload.FinishedAtUtc != null || payload.Settlement != null || payload.ReturnedSnapshotId != null);
    }

    private static RaidDefenseLock ToLock(AuthoritativeEvent evt, RaidDefenseLockPolicy policy)
    {
        var payload = (RaidEventPayload)evt.Payload;
        DateTimeOffset startedAt = payload.StartedAtUtc!.Value;
        return new RaidDefenseLock(
            evt.EventId,
            evt.Target.UserId,
            evt.Target.ColonyId,
            evt.TargetContext?.MapUniqueId,
            payload.DefenderSnapshotId,
            startedAt,
            startedAt + policy.MaxRaidDuration);
    }

    private static string RaidCompletionKey(AuthoritativeEvent evt)
    {
        if (evt.Payload is not RaidEventPayload payload)
        {
            return "";
        }

        return evt.Actor.UserId
            + "|"
            + evt.Target.UserId
            + "|"
            + (evt.Target.ColonyId ?? "")
            + "|"
            + (evt.TargetContext?.MapUniqueId ?? "")
            + "|"
            + payload.DefenderSnapshotId;
    }
}
