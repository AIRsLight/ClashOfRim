namespace AIRsLight.ClashOfRim.Events;

public sealed class InMemoryAuthoritativeEventLedger : IAuthoritativeEventLedger
{
    private readonly object gate = new();
    private readonly Dictionary<string, AuthoritativeEvent> eventsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> eventIdByIdempotencyKey = new(StringComparer.Ordinal);

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

            eventsById.Add(ledgerEvent.EventId, ledgerEvent);
            eventIdByIdempotencyKey.Add(ledgerEvent.IdempotencyKey, ledgerEvent.EventId);
            return new LedgerAppendResult(ledgerEvent, Created: true);
        }
    }

    public void ApplyBatch(LedgerBatch batch)
    {
        lock (gate)
        {
            LedgerBatchValidation.Validate(batch, Find, FindByIdempotencyKey);
            var prepared = new Dictionary<string, AuthoritativeEvent>(eventsById, StringComparer.Ordinal);
            foreach (AuthoritativeEvent item in batch.Events) prepared.TryAdd(item.EventId, item);
            foreach (LedgerStatusChange change in batch.StatusChanges)
                prepared[change.EventId] = prepared[change.EventId] with { Status = change.Status, Payload = change.Payload ?? prepared[change.EventId].Payload };
            foreach (LedgerApplicationChange change in batch.ApplicationChanges ?? [])
                prepared[change.Expected.EventId] = change.Updated;
            foreach (LedgerConfirmationChange change in batch.ConfirmationChanges ?? [])
                prepared[change.Expected.EventId] = change.Updated;
            foreach (LedgerRejectionChange change in batch.RejectionChanges ?? [])
                prepared[change.Expected.EventId] = change.Updated;
            // Persist before publishing caches; readers only see the complete batch.
            eventsById.Clear();
            eventIdByIdempotencyKey.Clear();
            foreach (AuthoritativeEvent item in prepared.Values)
            {
                eventsById.Add(item.EventId, item);
                eventIdByIdempotencyKey.Add(item.IdempotencyKey, item.EventId);
            }
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
            eventsById[eventId] = changed;
            return changed;
        }
    }

    public AuthoritativeEvent MarkDelivered(string eventId, string deliveredToSnapshotId, DateTimeOffset deliveredAtUtc)
    {
        lock (gate)
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
            return changed;
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
            eventsById[eventId] = changed;
            return changed;
        }
    }

    public AuthoritativeEvent MarkAccepted(string eventId, DateTimeOffset acceptedAtUtc, string? reason)
    {
        lock (gate)
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
            return changed;
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
            eventsById[eventId] = changed;
            return changed;
        }
    }

    public GiftReturnResult RejectGiftAndCreateReturn(
        string eventId,
        DateTimeOffset rejectedAtUtc,
        string? reason,
        bool originalActorOnline)
    {
        lock (gate)
        {
            var source = Find(eventId) ?? throw new KeyNotFoundException($"Event id not found: {eventId}");
            var plan = GiftRejectionPlan.Prepare(source, FindByIdempotencyKey, rejectedAtUtc, reason, originalActorOnline);
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
            eventsById[eventId] = changed;
            return changed;
        }
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
