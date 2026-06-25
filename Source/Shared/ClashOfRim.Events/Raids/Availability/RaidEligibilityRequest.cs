using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidEligibilityRequest(
    EventParty? Attacker,
    EventParty? Defender,
    bool IsHostile,
    bool DefenderOnline,
    DateTimeOffset CheckedAtUtc,
    DateTimeOffset? DefenderRaidCooldownUntilUtc,
    int DefenderWealth,
    SnapshotIdentity? DefenderSnapshot,
    IReadOnlyList<MapSummary>? DefenderMaps,
    string? TargetMapUniqueId);
