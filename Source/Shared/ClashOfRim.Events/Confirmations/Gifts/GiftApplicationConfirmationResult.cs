namespace AIRsLight.ClashOfRim.Events;

public sealed record GiftApplicationConfirmationResult(
    GiftApplicationConfirmationResultKind Kind,
    AuthoritativeEvent? Event,
    string? AppliedSnapshotId,
    string? FailureReason)
{
    public bool Accepted => Kind is GiftApplicationConfirmationResultKind.Accepted
        or GiftApplicationConfirmationResultKind.AlreadyApplied;
}
