using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Events;

public sealed record WorldMapRaidAvailabilitySource(
    SnapshotIdentity DefenderSnapshot,
    IReadOnlyList<MapSummary> DefenderMaps,
    bool IsHostile,
    bool DefenderOnline,
    int DefenderWealth,
    DateTimeOffset? DefenderRaidCooldownUntilUtc,
    RaidEligibilityPolicy? Policy = null);
