using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

internal static class GiftSnapshotReplayPolicy
{
    public static bool Matches(AuthoritativeEvent gift, string userId, string? colonyId, string? snapshotId)
        => !string.IsNullOrWhiteSpace(snapshotId)
            && gift.Type == ServerEventType.ItemDelivery
            && gift.Actor.UserId == userId && gift.Actor.ColonyId == colonyId
            && gift.Payload is ItemDeliveryEventPayload { Items.Count: > 0, Purpose: ItemDeliveryPurpose.Gift } payload
            && payload.Items.All(item => item.SourceSnapshotId == snapshotId);
}
