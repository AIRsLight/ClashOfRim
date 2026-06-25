using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Raids;

public sealed class RaidAttackerLossSnapshotConfirmationRequest
{
    public RaidAttackerLossSnapshotConfirmationRequest(
        string? sourceRaidEventId,
        string? attackerSnapshotId,
        RaidAttackerLossApplicationResultKind applicationKind,
        string? matchedCaravanLoadId,
        IReadOnlyList<string> lostPawnGlobalKeys,
        IReadOnlyList<RaidLostThingReference> lostThings)
    {
        SourceRaidEventId = sourceRaidEventId;
        AttackerSnapshotId = attackerSnapshotId;
        ApplicationKind = applicationKind;
        MatchedCaravanLoadId = matchedCaravanLoadId;
        LostPawnGlobalKeys = lostPawnGlobalKeys;
        LostThings = lostThings;
    }

    public string? SourceRaidEventId { get; }

    public string? AttackerSnapshotId { get; }

    public RaidAttackerLossApplicationResultKind ApplicationKind { get; }

    public string? MatchedCaravanLoadId { get; }

    public IReadOnlyList<string> LostPawnGlobalKeys { get; }

    public IReadOnlyList<RaidLostThingReference> LostThings { get; }
}
