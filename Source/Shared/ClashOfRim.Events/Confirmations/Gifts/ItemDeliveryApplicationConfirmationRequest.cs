using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Events;

public sealed record ItemDeliveryApplicationConfirmationRequest(
    string ItemDeliveryEventId,
    string OwnerId,
    string ColonyId,
    string BaseSnapshotId,
    LatestSnapshotRecord ConfirmedSnapshot,
    string ClientApplicationResult);
