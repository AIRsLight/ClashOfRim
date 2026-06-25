namespace AIRsLight.ClashOfRim.Save;

public sealed record RaidSettlementDiffResult(
    IReadOnlyList<ThingSummary> MissingThings,
    IReadOnlyList<RaidSettlementLoss> Losses,
    int IgnoredExtraThingCount,
    double LossRatio)
{
    public IReadOnlyList<RaidSettlementBattlefieldResidue> BattlefieldResidues { get; init; } =
        Array.Empty<RaidSettlementBattlefieldResidue>();

    public int StolenThingCount => MissingThings.Count;

    public int ReducedStackThingCount => Losses.Count(loss => loss.ReturnedStackCount.HasValue && loss.StolenStackCount > 0);

    public int TotalStolenStackCount => Losses.Sum(loss => loss.StolenStackCount);

    public int TotalLossCount => Losses.Sum(loss => loss.LossCount);
}
