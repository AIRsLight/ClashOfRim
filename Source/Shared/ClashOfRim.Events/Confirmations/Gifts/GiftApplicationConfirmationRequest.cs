using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Events;

public sealed record GiftApplicationConfirmationRequest(
    string GiftEventId,
    string OwnerId,
    string ColonyId,
    string BaseSnapshotId,
    LatestSnapshotRecord ConfirmedSnapshot,
    string ClientApplicationResult);
