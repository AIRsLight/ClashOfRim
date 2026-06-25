namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidEventPayload(
    string DefenderSnapshotId,
    string? ReturnedSnapshotId,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    RaidSettlementRecord? Settlement,
    RaidAttackForceRecord? AttackForce = null,
    RaidAttackerLossRecord? AttackerLoss = null,
    RaidOpponentKind OpponentKind = RaidOpponentKind.Player) : LedgerEventPayload
{
    public bool RequiresSettlement => OpponentKind == RaidOpponentKind.Player;
}
