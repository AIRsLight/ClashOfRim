namespace AIRsLight.ClashOfRim.Save;

public sealed record RaidSettlementReturnRequest(
    string? EventId,
    SnapshotIdentity OriginalSnapshotIdentity,
    SaveSnapshotPackage OriginalDefenseSnapshot,
    SaveSnapshotPackage ReturnedRaidSnapshot,
    string? TargetMapUniqueId,
    double LossRatio,
    IReadOnlyCollection<string>? PackableBuildingDefNames = null,
    IReadOnlyDictionary<string, int>? BuildingMaxHitPointsByDefName = null,
    IReadOnlyDictionary<string, float>? StuffHitPointFactorByDefName = null,
    IReadOnlyDictionary<string, float>? StuffHitPointOffsetByDefName = null,
    double MinimumRemainingHitPointsRatio = 0,
    IReadOnlyCollection<string>? IgnoredThingDefNames = null,
    string? ReturnedMapUniqueId = null,
    double BuildingHitPointsLossRatio = RaidSettlementPolicy.DefaultBuildingHitPointsLossRatio,
    IReadOnlyCollection<string>? TrapDefNames = null);
