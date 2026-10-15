namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidEventPayload(
    string DefenderSnapshotId,
    string? ReturnedSnapshotId,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    RaidSettlementRecord? Settlement,
    RaidAttackForceRecord? AttackForce = null,
    RaidAttackerLossRecord? AttackerLoss = null,
    RaidOpponentKind OpponentKind = RaidOpponentKind.Player,
    string? GuardDeploymentId = null,
    RaidTimeoutWork? TimeoutWork = null,
    string? EvidenceRequestHash = null) : LedgerEventPayload
{
    public bool RequiresSettlement => OpponentKind == RaidOpponentKind.Player;
}

public sealed record RaidTimeoutWork(DateTimeOffset TimedOutAtUtc, bool AttackerWasOffline, bool Completed = false);
