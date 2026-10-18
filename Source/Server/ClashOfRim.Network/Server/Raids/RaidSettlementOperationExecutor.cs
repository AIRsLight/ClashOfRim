using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public enum RaidSettlementOperationResultKind
{
    Completed,
    AlreadyCompleted,
    ManualReview
}

public sealed record RaidSettlementOperationResult(
    RaidSettlementOperationResultKind Kind,
    string? DefenderSnapshotId,
    string? SettlementEventId,
    string? Message);

// Public compatibility entry point; the module owns settlement and recovery.
public static class RaidSettlementOperationExecutor
{
    public static RaidSettlementOperationResult Execute(ClashOfRimNetworkState state,
        RaidSettlementDeferredPayload payload, SaveSnapshotPackage evidencePackage, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(evidencePackage);
        return state.RaidSettlement.Execute(payload, () => evidencePackage, nowUtc);
    }
}
