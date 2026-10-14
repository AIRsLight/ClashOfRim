using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public sealed record RaidSnapshotMutation(AuthoritativeEvent Raid, MercenaryGuardContractRecord? Guard,
    AuthoritativeEvent? GuardNotification)
{
    internal void Validate(SnapshotIdentity identity)
    {
        if (Raid.Type != ServerEventType.Raid || Raid.Actor.UserId != identity.OwnerId || Raid.Actor.ColonyId != identity.ColonyId
            || Raid.Payload is not RaidEventPayload { OpponentKind: RaidOpponentKind.Player, AttackForce: not null } payload
            || payload.AttackForce.AttackerSnapshotId != identity.SnapshotId || payload.StartedAtUtc != Raid.CreatedAtUtc
            || payload.ReturnedSnapshotId is not null || payload.Settlement is not null || payload.FinishedAtUtc is not null
            || payload.AttackerLoss is not null || payload.TimeoutWork is not null || string.IsNullOrWhiteSpace(Raid.Target.ColonyId)
            || (Guard is null) != (GuardNotification is null) || payload.GuardDeploymentId != Guard?.ContractId)
            throw new InvalidDataException("Raid intent does not match its confirmed snapshot.");
        if (Guard is { } guard && (guard.Status != MercenaryGuardContractRegistry.StatusActive
            || guard.ConsumedRaidEventId is not null || guard.ConsumedAtUtc is not null
            || guard.UserId != Raid.Target.UserId || guard.ColonyId != Raid.Target.ColonyId
            || GuardNotification!.Type != ServerEventType.ServerNotification || GuardNotification.Actor != new EventParty("server")
            || GuardNotification.Target != Raid.Target || GuardNotification.IdempotencyKey != "mercenary-guard-activated:" + Raid.EventId
            || GuardNotification.Payload is not ServerNotificationEventPayload notice
            || notice.NotificationId != GuardNotification.IdempotencyKey || notice.RelatedEventId != Raid.EventId))
            throw new InvalidDataException("Raid guard intent has inconsistent ownership or notification.");
    }

    private IReadOnlyList<AuthoritativeEvent> Events => GuardNotification is null ? [Raid] : [Raid, GuardNotification];

    internal bool Preflight(IAuthoritativeEventLedger ledger, MercenaryGuardContractRegistry guards)
    {
        if (Guard is { } guard) guards.ValidateRaidConsumption(guard, Raid.EventId, Raid.CreatedAtUtc);
        int present = 0;
        foreach (var expected in Events)
        {
            var current = ledger.Find(expected.EventId);
            var keyed = ledger.FindByIdempotencyKey(expected.IdempotencyKey);
            if (current is null && keyed is null) continue;
            if (current is null || keyed?.EventId != expected.EventId || !Matches(current, expected))
                throw new InvalidDataException("Raid intent conflicts with an existing ledger event.");
            present++;
        }
        if (present != 0 && present != Events.Count)
            throw new InvalidDataException("Raid intent conflicts with a partially populated event batch.");
        return present != 0;
    }

    internal void Apply(IAuthoritativeEventLedger ledger, MercenaryGuardContractRegistry guards)
    {
        bool published = Preflight(ledger, guards);
        // Finish guard consumption before exposing the raid to event delivery and settlement.
        if (Guard is { } guard) guards.ApplyRaidConsumption(guard, Raid.EventId, Raid.CreatedAtUtc);
        if (!published) ledger.ApplyBatch(new LedgerBatch(Events, []));
    }

    private static bool Matches(AuthoritativeEvent current, AuthoritativeEvent expected)
    {
        if (current.Type != expected.Type || current.Actor != expected.Actor || current.Target != expected.Target
            || current.IdempotencyKey != expected.IdempotencyKey || current.CreatedAtUtc != expected.CreatedAtUtc
            || current.TargetContext != expected.TargetContext) return false;
        LedgerEventPayload payload = current.Payload;
        // Settlement and delivery can advance after publication. Recovery must preserve them.
        if (payload is RaidEventPayload raid && expected.Payload is RaidEventPayload original)
            payload = raid with { ReturnedSnapshotId = original.ReturnedSnapshotId, FinishedAtUtc = original.FinishedAtUtc,
                Settlement = original.Settlement, AttackerLoss = original.AttackerLoss, TimeoutWork = original.TimeoutWork };
        return JsonSerializer.Serialize(payload) == JsonSerializer.Serialize(expected.Payload);
    }
}
