namespace AIRsLight.ClashOfRim.Events;

public sealed record ItemDeliveryApplicationConfirmationResult(
    ItemDeliveryApplicationConfirmationResultKind Kind,
    AuthoritativeEvent? Event,
    string? AppliedSnapshotId,
    string? FailureReason)
{
    public bool Accepted => Kind is ItemDeliveryApplicationConfirmationResultKind.Accepted
        or ItemDeliveryApplicationConfirmationResultKind.AlreadyApplied;
}
