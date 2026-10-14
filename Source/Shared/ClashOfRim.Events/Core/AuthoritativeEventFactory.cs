using System.Globalization;
using System.Text;

namespace AIRsLight.ClashOfRim.Events;

public static class AuthoritativeEventFactory
{
    /// <summary>Resolve persisted identity before callers embed the ID in dependent events or journals.</summary>
    public static AuthoritativeEvent CreateForLedger(IAuthoritativeEventLedger ledger,
        ServerEventType type, EventParty actor, EventParty target, string idempotencyKey, bool targetOnline,
        LedgerEventPayload payload, DateTimeOffset createdAtUtc, EventTargetContext? targetContext = null)
    {
        var candidate = Create(type, actor, target, idempotencyKey, targetOnline, payload, createdAtUtc, targetContext);
        return ResolvePersistedIdentity(ledger, candidate);
    }

    public static AuthoritativeEvent ResolvePersistedIdentity(IAuthoritativeEventLedger ledger, AuthoritativeEvent candidate)
    {
        var existing = ledger.FindByIdempotencyKey(candidate.IdempotencyKey);
        if (existing is null) return candidate;
        if (candidate.EventId != existing.EventId && !IsKnownEventId(candidate.Type, candidate.IdempotencyKey, candidate.EventId))
            throw new InvalidOperationException("Candidate has an unrecognized event identity.");
        candidate = candidate with { EventId = existing.EventId };
        LedgerEventIdentity.ValidateReplay(existing, candidate);
        return candidate;
    }

    public static AuthoritativeEvent Create(
        ServerEventType type,
        EventParty actor,
        EventParty target,
        string idempotencyKey,
        bool targetOnline,
        LedgerEventPayload payload,
        DateTimeOffset createdAtUtc,
        EventTargetContext? targetContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        ServerEventDeliveryMode deliveryMode = targetOnline
            ? ServerEventDeliveryMode.OnlineImmediate
            : ServerEventDeliveryMode.OfflinePending;

        ServerEventStatus status = targetOnline
            ? ServerEventStatus.ReadyForImmediateDelivery
            : ServerEventStatus.PendingOfflineDelivery;

        string eventId = BuildEventId(type, idempotencyKey);

        return new AuthoritativeEvent(
            eventId,
            type,
            actor,
            target,
            createdAtUtc,
            status,
            deliveryMode,
            idempotencyKey,
            payload,
            targetContext,
            EventRejectionPolicyResolver.Resolve(type, payload),
            TargetEventDecision.None,
            DecisionAtUtc: null,
            DecisionReason: null,
            targetOnline ? EventApplicationResultKind.RequiresClientConfirmation : EventApplicationResultKind.ReadyToApply);
    }

    public static string BuildEventId(ServerEventType type, string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        // '~' cannot occur in a normalized legacy ID. Encode safe-looking keys too: a legacy
        // key such as 'a:b' may already occupy the old ID for the different key 'a-b'.
        var encoded = new StringBuilder();
        foreach (char codeUnit in idempotencyKey) encoded.Append(((int)codeUnit).ToString("X4", CultureInfo.InvariantCulture));
        return $"{type.ToString().ToLowerInvariant()}:v2~{encoded}";
    }

    public static bool IsKnownEventId(ServerEventType type, string key, string id) =>
        id == BuildEventId(type, key) || id == BuildLegacyEventId(type, key);

    /// <summary>Only for validating or replaying persisted pre-v2 identities; never use for new events.</summary>
    public static string BuildLegacyEventId(ServerEventType type, string idempotencyKey)
    {
        string normalizedKey = string.Concat(idempotencyKey.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-'));
        return $"{type.ToString().ToLowerInvariant()}:{normalizedKey}";
    }
}
