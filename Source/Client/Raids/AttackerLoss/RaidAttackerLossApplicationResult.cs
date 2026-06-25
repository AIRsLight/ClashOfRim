using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Raids;

public sealed class RaidAttackerLossApplicationResult
{
    public RaidAttackerLossApplicationResult(
        RaidAttackerLossApplicationResultKind kind,
        IReadOnlyList<string> removedPawnGlobalKeys,
        IReadOnlyList<RaidLostThingReference> removedThings,
        bool triggeredVanillaCaravanLostEvent,
        bool requiresSnapshotUploadConfirmation,
        string? matchedCaravanLoadId,
        string? failureReason)
    {
        Kind = kind;
        RemovedPawnGlobalKeys = removedPawnGlobalKeys;
        RemovedThings = removedThings;
        TriggeredVanillaCaravanLostEvent = triggeredVanillaCaravanLostEvent;
        RequiresSnapshotUploadConfirmation = requiresSnapshotUploadConfirmation;
        MatchedCaravanLoadId = matchedCaravanLoadId;
        FailureReason = failureReason;
    }

    public RaidAttackerLossApplicationResultKind Kind { get; }

    public IReadOnlyList<string> RemovedPawnGlobalKeys { get; }

    public IReadOnlyList<RaidLostThingReference> RemovedThings { get; }

    public bool TriggeredVanillaCaravanLostEvent { get; }

    public bool RequiresSnapshotUploadConfirmation { get; }

    public string? MatchedCaravanLoadId { get; }

    public string? FailureReason { get; }

    public bool Applied =>
        Kind == RaidAttackerLossApplicationResultKind.AppliedWithVanillaCaravanLostEvent ||
        Kind == RaidAttackerLossApplicationResultKind.AppliedWithSnapshotFallback;
}
