namespace AIRsLight.ClashOfRim.Events;

public static class AuthoritativeEventFactory
{
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
        string normalizedKey = string.Concat(idempotencyKey.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-'));
        return $"{type.ToString().ToLowerInvariant()}:{normalizedKey}";
    }
}
