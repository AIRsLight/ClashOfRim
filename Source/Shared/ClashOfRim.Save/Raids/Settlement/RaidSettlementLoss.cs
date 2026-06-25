namespace AIRsLight.ClashOfRim.Save;

public sealed record RaidSettlementLoss(
    ThingSummary Thing,
    int OriginalStackCount,
    int? ReturnedStackCount,
    int StolenStackCount,
    int BaseLossCapCount,
    double FractionalCapChance,
    double FractionalRoll,
    int MaxLossCount,
    int LossCount,
    int? RemainingHitPointsAfterDamage = null)
{
    public bool UsedFractionalCap => MaxLossCount > BaseLossCapCount;
}
