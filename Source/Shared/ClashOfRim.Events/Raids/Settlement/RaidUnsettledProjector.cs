namespace AIRsLight.ClashOfRim.Events;

public static class RaidUnsettledProjector
{
    public static IReadOnlyList<AuthoritativeEvent> BuildForAttacker(
        string attackerUserId,
        string? attackerColonyId,
        IEnumerable<AuthoritativeEvent> events,
        string? excludedIdempotencyKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attackerUserId);
        ArgumentNullException.ThrowIfNull(events);

        return events
            .Where(evt => IsUnsettledForAttacker(evt, attackerUserId, attackerColonyId))
            .Where(evt => string.IsNullOrWhiteSpace(attackerColonyId) ||
                string.Equals(evt.Actor.ColonyId, attackerColonyId, StringComparison.Ordinal) ||
                string.Equals(evt.Target.ColonyId, attackerColonyId, StringComparison.Ordinal))
            .Where(evt => string.IsNullOrWhiteSpace(excludedIdempotencyKey) ||
                !string.Equals(evt.IdempotencyKey, excludedIdempotencyKey, StringComparison.Ordinal))
            .OrderBy(evt => evt.CreatedAtUtc)
            .ToList();
    }

    public static IReadOnlyList<AuthoritativeEvent> BuildForDefender(
        string defenderUserId,
        string? defenderColonyId,
        IEnumerable<AuthoritativeEvent> events)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defenderUserId);
        ArgumentNullException.ThrowIfNull(events);

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

            if (IsUnsettledSourceRaid(evt)
                && string.Equals(evt.Target.UserId, defenderUserId, StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(defenderColonyId) ||
                    string.Equals(evt.Target.ColonyId, defenderColonyId, StringComparison.Ordinal)))
            {
                candidateRaids.Add(evt);
            }
        }

        var unsettledRaids = new List<AuthoritativeEvent>(candidateRaids.Count);
        for (int index = 0; index < candidateRaids.Count; index++)
        {
            AuthoritativeEvent raid = candidateRaids[index];
            if (!completedKeys.Contains(RaidCompletionKey(raid)))
            {
                unsettledRaids.Add(raid);
            }
        }

        unsettledRaids.Sort(static (left, right) => left.CreatedAtUtc.CompareTo(right.CreatedAtUtc));
        return unsettledRaids;
    }

    public static IReadOnlyList<AuthoritativeEvent> BuildActiveSourceForAttacker(
        string attackerUserId,
        string? attackerColonyId,
        IEnumerable<AuthoritativeEvent> events)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attackerUserId);
        ArgumentNullException.ThrowIfNull(events);

        return events
            .Where(IsUnsettledSourceRaid)
            .Where(evt => string.Equals(evt.Actor.UserId, attackerUserId, StringComparison.Ordinal))
            .Where(evt => string.IsNullOrWhiteSpace(attackerColonyId) ||
                string.Equals(evt.Actor.ColonyId, attackerColonyId, StringComparison.Ordinal))
            .OrderBy(evt => evt.CreatedAtUtc)
            .ToList();
    }

    private static bool IsUnsettledSourceRaid(AuthoritativeEvent evt)
    {
        if (evt.Type != ServerEventType.Raid ||
            evt.Payload is not RaidEventPayload payload ||
            payload.OpponentKind != RaidOpponentKind.Player ||
            payload.AttackForce == null ||
            payload.AttackerLoss != null ||
            payload.StartedAtUtc == null ||
            payload.FinishedAtUtc != null ||
            payload.Settlement != null ||
            payload.ReturnedSnapshotId != null)
        {
            return false;
        }

        if (evt.Status == ServerEventStatus.AppliedToSnapshot)
        {
            return false;
        }

        return evt.Status is not ServerEventStatus.Cancelled
            and not ServerEventStatus.Failed
            and not ServerEventStatus.RejectedByTarget;
    }

    private static bool IsUnsettledForAttacker(
        AuthoritativeEvent evt,
        string attackerUserId,
        string? attackerColonyId)
    {
        if (evt.Payload is not RaidEventPayload payload)
        {
            return false;
        }

        if (IsUnsettledSourceRaid(evt) &&
            string.Equals(evt.Actor.UserId, attackerUserId, StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(attackerColonyId) ||
                string.Equals(evt.Actor.ColonyId, attackerColonyId, StringComparison.Ordinal);
        }

        if (payload.AttackerLoss == null ||
            !string.Equals(evt.Target.UserId, attackerUserId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(attackerColonyId) &&
            !string.Equals(evt.Target.ColonyId, attackerColonyId, StringComparison.Ordinal))
        {
            return false;
        }

        return evt.Status is not ServerEventStatus.AppliedToSnapshot
            and not ServerEventStatus.Cancelled
            and not ServerEventStatus.Failed
            and not ServerEventStatus.RejectedByTarget;
    }

    private static bool IsCompletedRaid(AuthoritativeEvent evt)
    {
        return evt.Type == ServerEventType.Raid &&
            evt.Payload is RaidEventPayload { OpponentKind: RaidOpponentKind.Player } payload &&
            (payload.FinishedAtUtc != null || payload.Settlement != null || payload.ReturnedSnapshotId != null);
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
