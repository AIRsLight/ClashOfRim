namespace AIRsLight.ClashOfRim.Save;

public sealed record RaidSettlementReturnResult(
    RaidSettlementReturnResultKind Kind,
    string? EventId,
    SnapshotIdentity? OriginalSnapshot,
    SnapshotIdentity? ReturnedSnapshot,
    string? TargetMapUniqueId,
    RaidSettlementDiffResult? Settlement)
{
    public bool Accepted => Kind == RaidSettlementReturnResultKind.Accepted;

    public static RaidSettlementReturnResult Rejected(
        RaidSettlementReturnResultKind kind,
        RaidSettlementReturnRequest? request = null)
    {
        return new RaidSettlementReturnResult(
            kind,
            request?.EventId,
            request?.OriginalDefenseSnapshot?.Envelope.Identity,
            request?.ReturnedRaidSnapshot?.Envelope.Identity,
            request?.TargetMapUniqueId,
            Settlement: null);
    }
}
