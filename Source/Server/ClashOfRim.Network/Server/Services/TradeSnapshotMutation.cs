using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public sealed record TradeSnapshotMutation(LedgerBatch Batch, IReadOnlyList<string> NotifyUsers)
{
    public void Validate(SnapshotIdentity identity)
    {
        if (Batch.Events.Count == 0 || !TradeSnapshotReplayPolicy.Matches(Batch.Events[0],
                identity.OwnerId!, identity.ColonyId, identity.SnapshotId,
                Batch.Events[0].Payload is TradeEventPayload { Stage: TradeStage.MarketOrder }))
            throw new InvalidDataException("Trade journal does not belong to its confirmed snapshot.");
    }
}

internal static class TradeSnapshotReplayPolicy
{
    public static bool Matches(AuthoritativeEvent item, string userId, string? colonyId, string? snapshotId, bool order)
    {
        if (string.IsNullOrWhiteSpace(snapshotId) || item.Type != ServerEventType.Trade
            || item.Actor.UserId != userId || item.Actor.ColonyId != colonyId || item.Payload is not TradeEventPayload payload)
            return false;
        if (!order) return payload.Stage is TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange
            && item.AppliedSnapshotId == snapshotId && item.Status == ServerEventStatus.AppliedToSnapshot;
        return payload.Stage == TradeStage.MarketOrder
            && payload.OfferedItems.Concat(payload.RequestedItems).Any()
            && payload.OfferedItems.Concat(payload.RequestedItems).All(thing => thing.SourceSnapshotId == snapshotId);
    }
}
