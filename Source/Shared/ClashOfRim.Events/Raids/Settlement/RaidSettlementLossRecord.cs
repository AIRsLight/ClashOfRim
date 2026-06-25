namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidSettlementLossRecord(
    string GlobalKey,
    string? Def,
    string? Position,
    string? MapUniqueId,
    bool WholeThingMissing,
    int OriginalStackCount,
    int? ReturnedStackCount,
    int StolenStackCount,
    int BaseLossCapCount,
    double FractionalCapChance,
    double FractionalRoll,
    int MaxLossCount,
    int LossCount,
    int? RemainingHitPointsAfterDamage = null);
