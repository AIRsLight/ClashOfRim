using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public sealed record RaidCleanupSnapshotMutation(AuthoritativeEvent Raid, SaveSnapshotEnvelope Previous,
    LedgerBatch Batch, IReadOnlyList<AuthoritativeEvent> SupportSources)
{
    internal void Validate(SnapshotIdentity identity)
    {
        if (Raid.Type != ServerEventType.Raid || Raid.Payload is not RaidEventPayload
            || Raid.Actor.UserId != identity.OwnerId || Raid.Actor.ColonyId != identity.ColonyId
            || Previous.Identity.OwnerId != identity.OwnerId || Previous.Identity.ColonyId != identity.ColonyId
            || Batch.ApplicationChanges?.Count > 0 || Batch.StatusChanges.Count != SupportSources.Count
            || SupportSources.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count() != SupportSources.Count)
            throw new InvalidDataException("Raid cleanup intent has inconsistent ownership or support sources.");
        foreach (var source in SupportSources)
        {
            if (source.Payload is not SupportPawnEventPayload || source.Target.UserId != identity.OwnerId
                || source.Target.ColonyId != identity.ColonyId
                || !Batch.StatusChanges.Any(change => change.EventId == source.EventId
                    && change.ExpectedStatus == source.Status && change.Status == ServerEventStatus.Failed)
                || Batch.Events.Count(item => item.Type == ServerEventType.SupportPawn
                    && item.Payload is SupportPawnEventPayload payload && payload.SourceEventId == source.EventId
                    && item.Actor == source.Target && item.Target == source.Actor) != 1)
                throw new InvalidDataException("Raid cleanup support mutation is inconsistent.");
        }
        foreach (var item in Batch.Events)
        {
            bool attackLoss = item.Type == ServerEventType.Raid && item.Actor == new EventParty("server") && item.Target == Raid.Actor
                && item.Payload is RaidEventPayload { AttackerLoss: not null } raidPayload
                && raidPayload.AttackerLoss.SourceRaidEventId == Raid.EventId
                && raidPayload.AttackerLoss.AttackerSnapshotId == identity.SnapshotId;
            bool supportLoss = item.Type == ServerEventType.SupportPawn && item.Payload is SupportPawnEventPayload support
                && SupportSources.Any(source => source.EventId == support.SourceEventId);
            if (!attackLoss && !supportLoss) throw new InvalidDataException("Raid cleanup contains an unrelated event.");
        }
    }

    internal void Preflight(IAuthoritativeEventLedger ledger)
    {
        var currentRaid = ledger.Find(Raid.EventId);
        if (currentRaid is null || currentRaid.Status != Raid.Status || !RaidSettlementSnapshotMutation.SameSource(currentRaid, Raid))
            throw new InvalidDataException("Raid cleanup source changed before recovery.");
        foreach (var source in SupportSources)
        {
            var current = ledger.Find(source.EventId);
            if (current is null || !RaidSettlementSnapshotMutation.SameSource(current, source))
                throw new InvalidDataException("Raid cleanup support source changed before recovery.");
        }
        LedgerBatchValidation.Validate(Batch, ledger.Find, ledger.FindByIdempotencyKey);
    }
}
