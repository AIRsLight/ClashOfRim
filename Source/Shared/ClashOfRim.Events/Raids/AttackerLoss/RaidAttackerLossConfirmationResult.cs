namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidAttackerLossConfirmationResult(
    RaidAttackerLossConfirmationResultKind Kind,
    AuthoritativeEvent? Event,
    string? AppliedSnapshotId,
    string? FailureReason)
{
    public bool Accepted => Kind is RaidAttackerLossConfirmationResultKind.Accepted
        or RaidAttackerLossConfirmationResultKind.AlreadyApplied;
}
