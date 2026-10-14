using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public sealed record RaidSettlementSnapshotMutation(AuthoritativeEvent Source, AuthoritativeEvent Settlement,
    SaveSnapshotEnvelope Previous, string AppliedSnapshotId, DateTimeOffset AppliedAtUtc)
{
    internal void Validate(SnapshotIdentity identity)
    {
        if (Source.Type != ServerEventType.Raid || Source.Payload is not RaidEventPayload { OpponentKind: RaidOpponentKind.Player } source
            || Source.Target.UserId != identity.OwnerId || Source.Target.ColonyId != identity.ColonyId
            || identity.SnapshotId != AppliedSnapshotId || Previous.Identity.OwnerId != identity.OwnerId
            || Previous.Identity.ColonyId != identity.ColonyId || Previous.Identity.SnapshotId != source.DefenderSnapshotId
            || Source.AppliedSnapshotId is not null || Settlement.Type != ServerEventType.Raid
            || Settlement.IdempotencyKey != "raid-settlement:" + Source.EventId
            || Settlement.Actor != Source.Actor || Settlement.Target != Source.Target || Settlement.TargetContext != Source.TargetContext
            || Settlement.Payload is not RaidEventPayload { Settlement: not null } result
            || result.DefenderSnapshotId != source.DefenderSnapshotId || result.FinishedAtUtc != AppliedAtUtc
            || Settlement.CreatedAtUtc != AppliedAtUtc || string.IsNullOrWhiteSpace(result.ReturnedSnapshotId))
            throw new InvalidDataException("Raid settlement intent has inconsistent event or snapshot identity.");
    }

    internal LedgerBatch Preflight(IAuthoritativeEventLedger ledger)
    {
        var current = ledger.Find(Source.EventId) ?? throw new InvalidDataException("Raid settlement source disappeared.");
        if (!SameSource(current, Source)
            || current.AppliedSnapshotId is not null && current.AppliedSnapshotId != AppliedSnapshotId
            || current.Status != Source.Status && current.Status is not (ServerEventStatus.DeliveredToClient or ServerEventStatus.AppliedToSnapshot))
            throw new InvalidDataException("Raid settlement source changed before recovery.");
        bool applied = current.Status == ServerEventStatus.AppliedToSnapshot && current.AppliedSnapshotId == AppliedSnapshotId;
        var existing = ledger.FindByIdempotencyKey(Settlement.IdempotencyKey);
        if (applied != (existing is not null))
            throw new InvalidDataException("Raid settlement ledger contains a partial or unrelated legacy result.");
        if (existing is not null && (existing.CreatedAtUtc != Settlement.CreatedAtUtc || existing.TargetContext != Settlement.TargetContext))
            throw new InvalidDataException("Raid settlement event metadata conflicts with its intent.");
        var batch = new LedgerBatch([Settlement], [], applied ? [] :
            [new LedgerApplicationChange(current, Previous.Identity.SnapshotId!, AppliedSnapshotId, AppliedAtUtc)]);
        LedgerBatchValidation.Validate(batch, ledger.Find, ledger.FindByIdempotencyKey);
        return batch;
    }

    internal static bool SameSource(AuthoritativeEvent current, AuthoritativeEvent expected)
    {
        // Delivery may advance independently, but ownership, payload and administrative decisions must not change.
        var comparable = current with
        {
            Status = expected.Status, DeliveredToSnapshotId = expected.DeliveredToSnapshotId, DeliveredAtUtc = expected.DeliveredAtUtc,
            AppliedSnapshotId = expected.AppliedSnapshotId, AppliedAtUtc = expected.AppliedAtUtc,
            LastApplicationResult = expected.LastApplicationResult, LastFailureReason = expected.LastFailureReason,
            NextRetryAtUtc = expected.NextRetryAtUtc
        };
        return JsonSerializer.Serialize(comparable) == JsonSerializer.Serialize(expected);
    }
}
