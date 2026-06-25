namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidAttackerLossClientContext(
    string CurrentSnapshotId,
    bool MatchingCaravanFound,
    string? CaravanId = null);
