namespace AIRsLight.ClashOfRim.Events;

public static class RaidCooldownProjector
{
    public static RaidCooldownStatus BuildForDefender(
        string defenderUserId,
        string? defenderColonyId,
        IEnumerable<AuthoritativeEvent> events,
        DateTimeOffset? defenderNextAvailableAtUtc = null,
        RaidCooldownPolicy? policy = null,
        Func<AuthoritativeEvent, DateTimeOffset?>? defenderProtectionStartResolver = null,
        bool requireDefenderProtectionActivation = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defenderUserId);
        ArgumentNullException.ThrowIfNull(events);

        RaidCooldownPolicy activePolicy = policy ?? RaidCooldownPolicy.Default;
        var settlementCompletedKeys = new HashSet<string>(StringComparer.Ordinal);
        var timeoutNotificationTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var candidateRaids = new List<AuthoritativeEvent>();
        foreach (AuthoritativeEvent evt in events)
        {
            if (HasSettlementCompletion(evt))
            {
                string completionKey = RaidCompletionKey(evt);
                if (!string.IsNullOrWhiteSpace(completionKey))
                {
                    settlementCompletedKeys.Add(completionKey);
                }
            }

            if (evt.Type == ServerEventType.ServerNotification
                && TryExtractRaidTimeoutEventId(evt.IdempotencyKey, out string? timeoutRaidEventId))
            {
                if (!timeoutNotificationTimes.TryGetValue(timeoutRaidEventId!, out DateTimeOffset existing)
                    || evt.CreatedAtUtc < existing)
                {
                    timeoutNotificationTimes[timeoutRaidEventId!] = evt.CreatedAtUtc;
                }
            }

            if (evt.Type == ServerEventType.Raid
                && evt.Payload is RaidEventPayload { OpponentKind: RaidOpponentKind.Player }
                && string.Equals(evt.Target.UserId, defenderUserId, StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(defenderColonyId) ||
                    string.Equals(evt.Target.ColonyId, defenderColonyId, StringComparison.Ordinal)))
            {
                candidateRaids.Add(evt);
            }
        }

        var records = new List<RaidCooldownRecord>(candidateRaids.Count);
        for (int index = 0; index < candidateRaids.Count; index++)
        {
            RaidCooldownRecord? record = ToCooldownRecord(
                candidateRaids[index],
                defenderNextAvailableAtUtc,
                activePolicy,
                settlementCompletedKeys,
                timeoutNotificationTimes,
                defenderProtectionStartResolver,
                requireDefenderProtectionActivation);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        records.Sort(static (left, right) => left.CooldownUntilUtc.CompareTo(right.CooldownUntilUtc));

        return new RaidCooldownStatus(defenderUserId, defenderColonyId, records);
    }

    private static RaidCooldownRecord? ToCooldownRecord(
        AuthoritativeEvent raid,
        DateTimeOffset? defenderNextAvailableAtUtc,
        RaidCooldownPolicy policy,
        IReadOnlySet<string> settlementCompletedKeys,
        IReadOnlyDictionary<string, DateTimeOffset> timeoutNotificationTimes,
        Func<AuthoritativeEvent, DateTimeOffset?>? defenderProtectionStartResolver,
        bool requireDefenderProtectionActivation)
    {
        if (raid.Payload is not RaidEventPayload { OpponentKind: RaidOpponentKind.Player } payload)
        {
            return null;
        }

        if (payload.FinishedAtUtc != null || payload.Settlement != null || payload.ReturnedSnapshotId != null)
        {
            DateTimeOffset completedAt = payload.FinishedAtUtc ?? raid.CreatedAtUtc;
            return BuildRecord(
                raid,
                RaidCooldownReason.SettlementCompleted,
                completedAt,
                defenderNextAvailableAtUtc,
                policy.SettlementCooldown,
                defenderProtectionStartResolver,
                requireDefenderProtectionActivation);
        }

        if (raid.Status == ServerEventStatus.Failed)
        {
            if (settlementCompletedKeys.Contains(RaidCompletionKey(raid)))
            {
                return null;
            }

            DateTimeOffset failedAt = timeoutNotificationTimes.TryGetValue(raid.EventId, out DateTimeOffset timeoutNotifiedAt)
                ? timeoutNotifiedAt
                : raid.CreatedAtUtc;
            return BuildRecord(
                raid,
                RaidCooldownReason.TimeoutFailed,
                failedAt,
                defenderNextAvailableAtUtc,
                policy.TimeoutCooldown,
                defenderProtectionStartResolver,
                requireDefenderProtectionActivation);
        }

        if (raid.Status == ServerEventStatus.Cancelled)
        {
            return BuildRecord(
                raid,
                RaidCooldownReason.Cancelled,
                raid.CreatedAtUtc,
                defenderNextAvailableAtUtc,
                policy.CancelledCooldown,
                defenderProtectionStartResolver,
                requireDefenderProtectionActivation);
        }

        return null;
    }

    private static RaidCooldownRecord? BuildRecord(
        AuthoritativeEvent raid,
        RaidCooldownReason reason,
        DateTimeOffset completedAtUtc,
        DateTimeOffset? defenderNextAvailableAtUtc,
        TimeSpan duration,
        Func<AuthoritativeEvent, DateTimeOffset?>? defenderProtectionStartResolver,
        bool requireDefenderProtectionActivation)
    {
        if (duration <= TimeSpan.Zero)
        {
            return null;
        }

        DateTimeOffset? activatedAtUtc = defenderProtectionStartResolver?.Invoke(raid);
        if (requireDefenderProtectionActivation && activatedAtUtc is null)
        {
            return new RaidCooldownRecord(
                raid.EventId,
                raid.Target.UserId,
                raid.Target.ColonyId,
                reason,
                completedAtUtc,
                DateTimeOffset.MaxValue);
        }

        DateTimeOffset? startsAtOverride = activatedAtUtc ?? defenderNextAvailableAtUtc;
        DateTimeOffset startsAt = completedAtUtc;
        if (startsAtOverride.HasValue && startsAtOverride.Value > completedAtUtc)
        {
            startsAt = startsAtOverride.Value;
        }

        return new RaidCooldownRecord(
            raid.EventId,
            raid.Target.UserId,
            raid.Target.ColonyId,
            reason,
            startsAt,
            startsAt + duration);
    }

    private static bool TryExtractRaidTimeoutEventId(string idempotencyKey, out string? raidEventId)
    {
        const string Prefix = "raid-timeout:";
        raidEventId = null;
        if (string.IsNullOrWhiteSpace(idempotencyKey)
            || !idempotencyKey.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        int idStart = Prefix.Length;
        int idEnd = idempotencyKey.IndexOf(':', idStart);
        if (idEnd <= idStart)
        {
            return false;
        }

        raidEventId = idempotencyKey[idStart..idEnd];
        return true;
    }

    private static bool HasSettlementCompletion(AuthoritativeEvent raid)
    {
        return raid.Payload is RaidEventPayload { OpponentKind: RaidOpponentKind.Player } payload
            && (payload.Settlement != null || payload.ReturnedSnapshotId != null);
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
