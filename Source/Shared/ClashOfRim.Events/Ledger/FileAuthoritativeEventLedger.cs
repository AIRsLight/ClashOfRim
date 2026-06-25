using System.Text.Json;

namespace AIRsLight.ClashOfRim.Events;

public sealed class FileAuthoritativeEventLedger : IAuthoritativeEventLedger
{
    private readonly string path;
    private readonly Dictionary<string, AuthoritativeEvent> eventsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> eventIdByIdempotencyKey = new(StringComparer.Ordinal);

    public FileAuthoritativeEventLedger(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = path;
        Load();
    }

    public LedgerAppendResult Append(AuthoritativeEvent ledgerEvent)
    {
        ArgumentNullException.ThrowIfNull(ledgerEvent);

        if (eventIdByIdempotencyKey.TryGetValue(ledgerEvent.IdempotencyKey, out string? existingEventId))
        {
            return new LedgerAppendResult(eventsById[existingEventId], Created: false);
        }

        if (eventsById.ContainsKey(ledgerEvent.EventId))
        {
            throw new InvalidOperationException($"Event id already exists: {ledgerEvent.EventId}");
        }

        eventsById.Add(ledgerEvent.EventId, ledgerEvent);
        eventIdByIdempotencyKey.Add(ledgerEvent.IdempotencyKey, ledgerEvent.EventId);
        Save();
        return new LedgerAppendResult(ledgerEvent, Created: true);
    }

    public AuthoritativeEvent? Find(string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        return eventsById.TryGetValue(eventId, out AuthoritativeEvent? ledgerEvent) ? ledgerEvent : null;
    }

    public AuthoritativeEvent? FindByIdempotencyKey(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return eventIdByIdempotencyKey.TryGetValue(idempotencyKey, out string? eventId)
            && eventsById.TryGetValue(eventId, out AuthoritativeEvent? ledgerEvent)
                ? ledgerEvent
                : null;
    }

    public IReadOnlyList<AuthoritativeEvent> ListAll()
    {
        return eventsById.Values
            .OrderBy(ledgerEvent => ledgerEvent.CreatedAtUtc)
            .ToList();
    }

    public IReadOnlyList<AuthoritativeEvent> ListByType(ServerEventType type)
    {
        return eventsById.Values
            .Where(ledgerEvent => ledgerEvent.Type == type)
            .OrderBy(ledgerEvent => ledgerEvent.CreatedAtUtc)
            .ToList();
    }

    public IReadOnlyList<AuthoritativeEvent> ListByTypeForActor(ServerEventType type, string actorUserId, string? actorColonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);

        return eventsById.Values
            .Where(ledgerEvent => ledgerEvent.Type == type)
            .Where(ledgerEvent => ledgerEvent.Actor.UserId == actorUserId)
            .Where(ledgerEvent => string.Equals(ledgerEvent.Actor.ColonyId, actorColonyId, StringComparison.Ordinal))
            .OrderBy(ledgerEvent => ledgerEvent.CreatedAtUtc)
            .ToList();
    }

    public IReadOnlyList<AuthoritativeEvent> ListByTypeForTarget(ServerEventType type, string targetUserId, string? targetColonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);

        return eventsById.Values
            .Where(ledgerEvent => ledgerEvent.Type == type)
            .Where(ledgerEvent => ledgerEvent.Target.UserId == targetUserId)
            .Where(ledgerEvent => string.Equals(ledgerEvent.Target.ColonyId, targetColonyId, StringComparison.Ordinal))
            .OrderBy(ledgerEvent => ledgerEvent.CreatedAtUtc)
            .ToList();
    }

    public IReadOnlyList<AuthoritativeEvent> ListForUser(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return eventsById.Values
            .Where(ledgerEvent => ledgerEvent.Actor.UserId == userId || ledgerEvent.Target.UserId == userId)
            .OrderBy(ledgerEvent => ledgerEvent.CreatedAtUtc)
            .ToList();
    }

    public IReadOnlyList<AuthoritativeEvent> ListQueueForTarget(string targetUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);

        return eventsById.Values
            .Where(ledgerEvent => ledgerEvent.Target.UserId == targetUserId)
            .Where(IsQueueVisible)
            .OrderBy(ledgerEvent => ledgerEvent.CreatedAtUtc)
            .ThenBy(ledgerEvent => ledgerEvent.EventId, StringComparer.Ordinal)
            .ToList();
    }

    public int DeleteForColony(string userId, string colonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

        List<AuthoritativeEvent> matching = eventsById.Values
            .Where(ledgerEvent =>
                IsColonyParty(ledgerEvent.Actor, userId, colonyId)
                || IsColonyParty(ledgerEvent.Target, userId, colonyId))
            .ToList();
        foreach (AuthoritativeEvent ledgerEvent in matching)
        {
            eventsById.Remove(ledgerEvent.EventId);
            eventIdByIdempotencyKey.Remove(ledgerEvent.IdempotencyKey);
        }

        if (matching.Count > 0)
        {
            Save();
        }

        return matching.Count;
    }

    public IReadOnlyList<AuthoritativeEvent> ListDeliverableForTarget(string targetUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);

        return eventsById.Values
            .Where(ledgerEvent => ledgerEvent.Target.UserId == targetUserId)
            .Where(IsDeliverable)
            .OrderBy(ledgerEvent => ledgerEvent.CreatedAtUtc)
            .ToList();
    }

    public AuthoritativeEvent ChangeStatus(string eventId, ServerEventStatus status)
    {
        AuthoritativeEvent ledgerEvent = Find(eventId)
            ?? throw new KeyNotFoundException($"Event id not found: {eventId}");

        AuthoritativeEvent changed = ledgerEvent with { Status = status };
        eventsById[eventId] = changed;
        Save();
        return changed;
    }

    public AuthoritativeEvent MarkDelivered(string eventId, string deliveredToSnapshotId, DateTimeOffset deliveredAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveredToSnapshotId);

        AuthoritativeEvent ledgerEvent = Find(eventId)
            ?? throw new KeyNotFoundException($"Event id not found: {eventId}");

        AuthoritativeEvent changed = ledgerEvent with
        {
            Status = ServerEventStatus.DeliveredToClient,
            DeliveredToSnapshotId = deliveredToSnapshotId,
            DeliveredAtUtc = deliveredAtUtc
        };
        eventsById[eventId] = changed;
        Save();
        return changed;
    }

    public AuthoritativeEvent MarkApplied(string eventId, string appliedSnapshotId, DateTimeOffset appliedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appliedSnapshotId);

        AuthoritativeEvent ledgerEvent = Find(eventId)
            ?? throw new KeyNotFoundException($"Event id not found: {eventId}");
        if (string.IsNullOrWhiteSpace(ledgerEvent.DeliveredToSnapshotId))
        {
            throw new InvalidOperationException("Event must be delivered to a snapshot before it can be applied.");
        }

        AuthoritativeEvent changed = ledgerEvent with
        {
            Status = ServerEventStatus.AppliedToSnapshot,
            AppliedSnapshotId = appliedSnapshotId,
            AppliedAtUtc = appliedAtUtc
        };
        eventsById[eventId] = changed;
        Save();
        return changed;
    }

    public AuthoritativeEvent MarkAccepted(string eventId, DateTimeOffset acceptedAtUtc, string? reason)
    {
        AuthoritativeEvent ledgerEvent = Find(eventId)
            ?? throw new KeyNotFoundException($"Event id not found: {eventId}");

        AuthoritativeEvent changed = ledgerEvent with
        {
            Status = ServerEventStatus.AppliedToSnapshot,
            TargetDecision = TargetEventDecision.Accepted,
            DecisionAtUtc = acceptedAtUtc,
            DecisionReason = reason,
            LastApplicationResult = EventApplicationResultKind.Applied,
            AppliedAtUtc = acceptedAtUtc
        };
        eventsById[eventId] = changed;
        Save();
        return changed;
    }

    public AuthoritativeEvent MarkRejected(string eventId, DateTimeOffset rejectedAtUtc, string? reason)
    {
        AuthoritativeEvent ledgerEvent = Find(eventId)
            ?? throw new KeyNotFoundException($"Event id not found: {eventId}");
        if (ledgerEvent.RejectionPolicy != EventRejectionPolicy.RejectableByTarget)
        {
            throw new InvalidOperationException("Event is not rejectable by target.");
        }

        AuthoritativeEvent changed = ledgerEvent with
        {
            Status = ServerEventStatus.RejectedByTarget,
            TargetDecision = TargetEventDecision.Rejected,
            DecisionAtUtc = rejectedAtUtc,
            DecisionReason = reason,
            LastApplicationResult = EventApplicationResultKind.Rejected
        };
        eventsById[eventId] = changed;
        Save();
        return changed;
    }

    public GiftReturnResult RejectGiftAndCreateReturn(
        string eventId,
        DateTimeOffset rejectedAtUtc,
        string? reason,
        bool originalActorOnline)
    {
        AuthoritativeEvent rejectedGift = MarkRejected(eventId, rejectedAtUtc, reason);
        AuthoritativeEvent returnEvent = GiftReturnEventFactory.CreateReturnEvent(
            rejectedGift,
            rejectedAtUtc,
            originalActorOnline);
        LedgerAppendResult appendResult = Append(returnEvent);
        return new GiftReturnResult(rejectedGift, appendResult.Event, appendResult.Created);
    }

    public AuthoritativeEvent ReportApplicationResult(
        string eventId,
        EventApplicationResultKind result,
        string? failureReason,
        DateTimeOffset? nextRetryAtUtc)
    {
        AuthoritativeEvent ledgerEvent = Find(eventId)
            ?? throw new KeyNotFoundException($"Event id not found: {eventId}");

        AuthoritativeEvent changed = ledgerEvent with
        {
            Status = result is EventApplicationResultKind.SnapshotBaseMismatch
                or EventApplicationResultKind.TargetObjectMissing
                or EventApplicationResultKind.LossNotReflected
                or EventApplicationResultKind.NeedsManualReview
                ? ServerEventStatus.Conflict
                : ledgerEvent.Status,
            LastApplicationResult = result,
            LastFailureReason = failureReason,
            NextRetryAtUtc = nextRetryAtUtc
        };
        eventsById[eventId] = changed;
        Save();
        return changed;
    }

    private void Load()
    {
        if (!File.Exists(path))
        {
            return;
        }

        string json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        List<AuthoritativeEvent>? events = JsonSerializer.Deserialize<List<AuthoritativeEvent>>(json, LedgerJson.Options);
        foreach (AuthoritativeEvent ledgerEvent in events ?? Enumerable.Empty<AuthoritativeEvent>())
        {
            if (eventsById.ContainsKey(ledgerEvent.EventId))
            {
                throw new InvalidOperationException($"Duplicate event id in ledger file: {ledgerEvent.EventId}");
            }

            if (eventIdByIdempotencyKey.ContainsKey(ledgerEvent.IdempotencyKey))
            {
                throw new InvalidOperationException($"Duplicate idempotency key in ledger file: {ledgerEvent.IdempotencyKey}");
            }

            eventsById.Add(ledgerEvent.EventId, ledgerEvent);
            eventIdByIdempotencyKey.Add(ledgerEvent.IdempotencyKey, ledgerEvent.EventId);
        }
    }

    private void Save()
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var events = eventsById.Values
            .OrderBy(ledgerEvent => ledgerEvent.CreatedAtUtc)
            .ToList();

        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(events, LedgerJson.Options));
        File.Move(tempPath, path, overwrite: true);
    }

    private static bool IsDeliverable(AuthoritativeEvent ledgerEvent)
    {
        return ledgerEvent.Status is ServerEventStatus.PendingOfflineDelivery or ServerEventStatus.DeliveredToClient;
    }

    private static bool IsQueueVisible(AuthoritativeEvent ledgerEvent)
    {
        return ledgerEvent.Status is ServerEventStatus.PendingOfflineDelivery
            or ServerEventStatus.ReadyForImmediateDelivery
            or ServerEventStatus.DeliveredToClient
            or ServerEventStatus.Conflict
            or ServerEventStatus.Failed
            or ServerEventStatus.RejectedByTarget;
    }

    private static bool IsColonyParty(EventParty party, string userId, string colonyId)
    {
        return string.Equals(party.UserId, userId, StringComparison.Ordinal)
            && string.Equals(party.ColonyId, colonyId, StringComparison.Ordinal);
    }
}
