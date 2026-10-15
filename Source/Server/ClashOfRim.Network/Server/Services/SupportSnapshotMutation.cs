using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public sealed record SupportSnapshotMutation(AuthoritativeEvent Support)
{
    internal void Validate(SnapshotIdentity identity)
    {
        if (Support.Type != ServerEventType.SupportPawn || Support.Actor.UserId != identity.OwnerId
            || Support.Actor.ColonyId != identity.ColonyId || string.IsNullOrWhiteSpace(Support.Target.UserId)
            || string.IsNullOrWhiteSpace(Support.Target.ColonyId) || string.IsNullOrWhiteSpace(Support.IdempotencyKey)
            || string.IsNullOrWhiteSpace(Support.TargetContext?.MapUniqueId)
            || Support.Payload is not SupportPawnEventPayload { ReturnToSender: false, SourceEventId: null, ReturnReason: null, RejectionReason: null } payload
            || payload.SourceSnapshotId != identity.SnapshotId || string.IsNullOrWhiteSpace(payload.PawnGlobalKey)
            || payload.PawnPackage is null || payload.PawnPackage.Reference.GlobalId != payload.PawnGlobalKey
            || payload.PawnReference is { } reference && reference.GlobalId != payload.PawnGlobalKey
            || payload.CreationRequestHash is not { Length: 67 } hash
            || !hash.StartsWith("v1:", StringComparison.Ordinal) || !hash[3..].All(Uri.IsHexDigit))
            throw new InvalidDataException("Support intent does not match its confirmed snapshot or request binding.");
        _ = SafePawnExchangeSerializer.Serialize(payload.PawnPackage);
        var initial = AuthoritativeEventFactory.Create(Support.Type, Support.Actor, Support.Target, Support.IdempotencyKey,
            Support.DeliveryMode == ServerEventDeliveryMode.OnlineImmediate, payload, Support.CreatedAtUtc, Support.TargetContext);
        if (Canonical(initial) != Canonical(Support))
            throw new InvalidDataException("Support journal must contain the original unpublished event.");
    }

    internal bool Preflight(IAuthoritativeEventLedger ledger)
    {
        var current = ledger.Find(Support.EventId);
        var keyed = ledger.FindByIdempotencyKey(Support.IdempotencyKey);
        if (current is null && keyed is null) return false;
        if (current is null || keyed?.EventId != Support.EventId || current.CreatedAtUtc != Support.CreatedAtUtc
            || current.DeliveryMode != Support.DeliveryMode || !MatchesContent(current, Support))
            throw new InvalidDataException("Support intent conflicts with an existing event.");
        return true;
    }

    internal void Apply(IAuthoritativeEventLedger ledger)
    {
        // Delivery/return may already have advanced. Never reset a published event on recovery.
        if (!Preflight(ledger)) ledger.ApplyBatch(new LedgerBatch([Support], []));
    }

    internal static bool MatchesContent(AuthoritativeEvent current, AuthoritativeEvent expected) =>
        current.Type == expected.Type && current.Actor == expected.Actor && current.Target == expected.Target
        && current.IdempotencyKey == expected.IdempotencyKey && current.TargetContext == expected.TargetContext
        && current.RejectionPolicy == expected.RejectionPolicy && Canonical(current.Payload) == Canonical(expected.Payload);

    internal static string RequestHash(CreateSupportPawnWithSnapshotRequest request, byte[] payload)
        => SnapshotRequestBinding.Hash(request, payload);

    private static string Canonical<T>(T value) => SnapshotRequestBinding.Canonical(value);
}
