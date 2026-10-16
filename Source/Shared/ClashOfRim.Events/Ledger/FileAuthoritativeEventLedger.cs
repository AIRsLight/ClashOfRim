using System.Text.Json;

namespace AIRsLight.ClashOfRim.Events;

public sealed class FileAuthoritativeEventLedger : IAuthoritativeEventLedger
{
    private readonly string path;
    private readonly object gate = new();
    private Dictionary<string, AuthoritativeEvent> eventsById = new(StringComparer.Ordinal);
    private Dictionary<string, string> eventIdByIdempotencyKey = new(StringComparer.Ordinal);

    public FileAuthoritativeEventLedger(string path)
    {
        lock (gate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            this.path = path;
            Load();
        }
    }

    public LedgerAppendResult Append(AuthoritativeEvent ledgerEvent)
    {
        lock (gate)
        {
            ArgumentNullException.ThrowIfNull(ledgerEvent);

            if (eventIdByIdempotencyKey.TryGetValue(ledgerEvent.IdempotencyKey, out string? existingEventId))
            {
                LedgerEventIdentity.ValidateReplay(eventsById[existingEventId], ledgerEvent);
                return new LedgerAppendResult(eventsById[existingEventId], Created: false);
            }

            if (eventsById.ContainsKey(ledgerEvent.EventId))
            {
                throw new InvalidOperationException($"Event id already exists: {ledgerEvent.EventId}");
            }

            // Publish both indexes only after the complete candidate file was atomically persisted.
            Save(eventsById.Values.Append(ledgerEvent));
            eventsById.Add(ledgerEvent.EventId, ledgerEvent);
            eventIdByIdempotencyKey.Add(ledgerEvent.IdempotencyKey, ledgerEvent.EventId);
            return new LedgerAppendResult(ledgerEvent, Created: true);
        }
    }

    public void ApplyBatch(LedgerBatch batch)
    {
        lock (gate)
        {
            var (events, keys) = LedgerBatchPlan.Prepare(batch, Find, FindByIdempotencyKey).BuildCandidate(eventsById);
            // Publish both indexes only after the candidate file has been persisted.
            Save(events.Values);
            eventsById = events;
            eventIdByIdempotencyKey = keys;
        }
    }
    public AuthoritativeEvent? Find(string eventId)
    {
        lock (gate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
            return eventsById.TryGetValue(eventId, out AuthoritativeEvent? ledgerEvent) ? ledgerEvent : null;
        }
    }

    public AuthoritativeEvent? FindByIdempotencyKey(string idempotencyKey)
    {
        lock (gate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
            return eventIdByIdempotencyKey.TryGetValue(idempotencyKey, out string? eventId)
                && eventsById.TryGetValue(eventId, out AuthoritativeEvent? ledgerEvent)
                    ? ledgerEvent
                    : null;
        }
    }

    public IReadOnlyList<AuthoritativeEvent> ListAll()
    {
        lock (gate)
        {
            return eventsById.Values
                .OrderBy(ledgerEvent => ledgerEvent.CreatedAtUtc)
                .ToList();
        }
    }

    public IReadOnlyList<AuthoritativeEvent> ListByType(ServerEventType type)
    {
        lock (gate)
        {
            return eventsById.Values
                .Where(ledgerEvent => ledgerEvent.Type == type)
                .OrderBy(ledgerEvent => ledgerEvent.CreatedAtUtc)
                .ToList();
        }
    }

    public IReadOnlyList<AuthoritativeEvent> ListByTypeForActor(ServerEventType type, string actorUserId, string? actorColonyId)
    {
        lock (gate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);

            return eventsById.Values
                .Where(ledgerEvent => ledgerEvent.Type == type)
                .Where(ledgerEvent => ledgerEvent.Actor.UserId == actorUserId)
                .Where(ledgerEvent => string.Equals(ledgerEvent.Actor.ColonyId, actorColonyId, StringComparison.Ordinal))
                .OrderBy(ledgerEvent => ledgerEvent.CreatedAtUtc)
                .ToList();
        }
    }

    public IReadOnlyList<AuthoritativeEvent> ListByTypeForTarget(ServerEventType type, string targetUserId, string? targetColonyId)
    {
        lock (gate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);

            return eventsById.Values
                .Where(ledgerEvent => ledgerEvent.Type == type)
                .Where(ledgerEvent => ledgerEvent.Target.UserId == targetUserId)
                .Where(ledgerEvent => string.Equals(ledgerEvent.Target.ColonyId, targetColonyId, StringComparison.Ordinal))
                .OrderBy(ledgerEvent => ledgerEvent.CreatedAtUtc)
                .ToList();
        }
    }

    public IReadOnlyList<AuthoritativeEvent> ListForUser(string userId)
    {
        lock (gate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userId);

            return eventsById.Values
                .Where(ledgerEvent => ledgerEvent.Actor.UserId == userId || ledgerEvent.Target.UserId == userId)
                .OrderBy(ledgerEvent => ledgerEvent.CreatedAtUtc)
                .ToList();
        }
    }

    public IReadOnlyList<AuthoritativeEvent> ListQueueForTarget(string targetUserId)
    {
        lock (gate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);

            return eventsById.Values
                .Where(ledgerEvent => ledgerEvent.Target.UserId == targetUserId)
                .Where(IsQueueVisible)
                .OrderBy(ledgerEvent => ledgerEvent.CreatedAtUtc)
                .ThenBy(ledgerEvent => ledgerEvent.EventId, StringComparer.Ordinal)
                .ToList();
        }
    }

    public int DeleteForColony(string userId, string colonyId)
    {
        lock (gate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userId);
            ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

            List<AuthoritativeEvent> matching = eventsById.Values
                .Where(ledgerEvent =>
                    IsColonyParty(ledgerEvent.Actor, userId, colonyId)
                    || IsColonyParty(ledgerEvent.Target, userId, colonyId))
                .ToList();
            if (matching.Count == 0) return 0;
            var removedIds = matching.Select(item => item.EventId).ToHashSet(StringComparer.Ordinal);
            Save(eventsById.Values.Where(item => !removedIds.Contains(item.EventId)));
            foreach (AuthoritativeEvent ledgerEvent in matching)
            {
                eventsById.Remove(ledgerEvent.EventId);
                eventIdByIdempotencyKey.Remove(ledgerEvent.IdempotencyKey);
            }

            return matching.Count;
        }
    }

    public IReadOnlyList<AuthoritativeEvent> ListDeliverableForTarget(string targetUserId)
    {
        lock (gate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);

            return eventsById.Values
                .Where(ledgerEvent => ledgerEvent.Target.UserId == targetUserId)
                .Where(IsDeliverable)
                .OrderBy(ledgerEvent => ledgerEvent.CreatedAtUtc)
                .ToList();
        }
    }

    public AuthoritativeEvent ChangeStatus(string eventId, ServerEventStatus status)
    {
        lock (gate)
        {
            AuthoritativeEvent ledgerEvent = Find(eventId)
                ?? throw new KeyNotFoundException($"Event id not found: {eventId}");

            AuthoritativeEvent changed = ledgerEvent with { Status = status };
            return PersistReplacement(changed);
        }
    }

    public AuthoritativeEvent MarkDelivered(string eventId, string deliveredToSnapshotId, DateTimeOffset deliveredAtUtc)
    {
        lock (gate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(deliveredToSnapshotId);

            AuthoritativeEvent ledgerEvent = Find(eventId)
                ?? throw new KeyNotFoundException($"Event id not found: {eventId}");

            AuthoritativeEvent changed = new LedgerDeliveryChange(ledgerEvent, deliveredToSnapshotId, deliveredAtUtc).Updated;
            return PersistReplacement(changed);
        }
    }

    public AuthoritativeEvent MarkApplied(string eventId, string appliedSnapshotId, DateTimeOffset appliedAtUtc)
    {
        lock (gate)
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
            return PersistReplacement(changed);
        }
    }

    public AuthoritativeEvent MarkAccepted(string eventId, DateTimeOffset acceptedAtUtc, string? reason)
    {
        lock (gate)
        {
            AuthoritativeEvent ledgerEvent = Find(eventId)
                ?? throw new KeyNotFoundException($"Event id not found: {eventId}");

            AuthoritativeEvent changed = new LedgerAcceptanceChange(ledgerEvent, acceptedAtUtc, reason).Updated;
            return PersistReplacement(changed);
        }
    }

    public AuthoritativeEvent MarkRejected(string eventId, DateTimeOffset rejectedAtUtc, string? reason)
    {
        lock (gate)
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
            return PersistReplacement(changed);
        }
    }

    public GiftReturnResult RejectGiftAndCreateReturn(
        string eventId,
        DateTimeOffset rejectedAtUtc,
        string? reason,
        bool originalActorOnline,
        EventTargetContext? returnTargetContext = null)
    {
        lock (gate)
        {
            var source = Find(eventId) ?? throw new KeyNotFoundException($"Event id not found: {eventId}");
            var plan = GiftRejectionPlan.Prepare(source, FindByIdempotencyKey, rejectedAtUtc, reason, originalActorOnline, returnTargetContext);
            ApplyBatch(plan.Batch);
            return plan.Result;
        }
    }

    public AuthoritativeEvent ReportApplicationResult(
        string eventId,
        EventApplicationResultKind result,
        string? failureReason,
        DateTimeOffset? nextRetryAtUtc)
    {
        lock (gate)
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
            return PersistReplacement(changed);
        }
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

    // Call only under gate. A failed write must leave both reads and subsequent retries on the old state.
    private AuthoritativeEvent PersistReplacement(AuthoritativeEvent changed)
    {
        Save(eventsById.Values.Select(item => item.EventId == changed.EventId ? changed : item));
        eventsById[changed.EventId] = changed;
        return changed;
    }

    private void Save(IEnumerable<AuthoritativeEvent> records)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var events = records
            .OrderBy(ledgerEvent => ledgerEvent.CreatedAtUtc)
            .ToList();

        string tempPath = path + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, events, LedgerJson.Options);
            stream.Flush(flushToDisk: true);
        }
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
