namespace AIRsLight.ClashOfRim.Events;

public sealed record LedgerDeliveryChange(AuthoritativeEvent Expected, string SnapshotId, DateTimeOffset DeliveredAtUtc)
{
    public AuthoritativeEvent Updated => Expected with
    {
        Status = ServerEventStatus.DeliveredToClient,
        DeliveredToSnapshotId = SnapshotId,
        DeliveredAtUtc = DeliveredAtUtc
    };
}

public sealed record LedgerAcceptanceChange(AuthoritativeEvent Expected, DateTimeOffset AcceptedAtUtc, string? Reason)
{
    public AuthoritativeEvent Updated => Expected with
    {
        Status = ServerEventStatus.AppliedToSnapshot,
        TargetDecision = TargetEventDecision.Accepted,
        DecisionAtUtc = AcceptedAtUtc,
        DecisionReason = Reason,
        LastApplicationResult = EventApplicationResultKind.Applied,
        AppliedAtUtc = AcceptedAtUtc
    };
}
