using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidSettlementRecord(
    string OriginalSnapshotId,
    string ReturnedSnapshotId,
    double LossRatio,
    IReadOnlyList<string> MissingThingGlobalKeys,
    IReadOnlyList<RaidSettlementLossRecord> Losses,
    int IgnoredExtraThingCount)
{
    public static RaidSettlementRecord FromDiff(
        string originalSnapshotId,
        string returnedSnapshotId,
        RaidSettlementDiffResult diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        return new RaidSettlementRecord(
            originalSnapshotId,
            returnedSnapshotId,
            diff.LossRatio,
            diff.MissingThings.Select(thing => thing.GlobalKey).ToList(),
            diff.Losses.Select(loss => new RaidSettlementLossRecord(
                loss.Thing.GlobalKey,
                loss.Thing.Def,
                loss.Thing.Position,
                loss.Thing.MapUniqueId,
                !loss.ReturnedStackCount.HasValue,
                loss.OriginalStackCount,
                loss.ReturnedStackCount,
                loss.StolenStackCount,
                loss.BaseLossCapCount,
                loss.FractionalCapChance,
                loss.FractionalRoll,
                loss.MaxLossCount,
                loss.LossCount,
                loss.RemainingHitPointsAfterDamage)).ToList(),
            diff.IgnoredExtraThingCount);
    }
}
