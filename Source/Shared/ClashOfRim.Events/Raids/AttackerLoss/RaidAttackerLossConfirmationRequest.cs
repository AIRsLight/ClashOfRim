using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidAttackerLossConfirmationRequest(
    string AttackerLossEventId,
    string SourceRaidEventId,
    string OwnerId,
    string ColonyId,
    string AttackerSnapshotId,
    LatestSnapshotRecord ConfirmedSnapshot);
