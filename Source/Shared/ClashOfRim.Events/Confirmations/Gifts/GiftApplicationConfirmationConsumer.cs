using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Events;

public sealed class GiftApplicationConfirmationConsumer
{
    private readonly IAuthoritativeEventLedger ledger;

    public GiftApplicationConfirmationConsumer(IAuthoritativeEventLedger ledger)
    {
        this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    }

    public GiftApplicationConfirmationResult Consume(
        GiftApplicationConfirmationRequest request,
        DateTimeOffset confirmedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ConfirmedSnapshot);

        AuthoritativeEvent? ledgerEvent = ledger.Find(request.GiftEventId);
        if (ledgerEvent is null)
        {
            return new GiftApplicationConfirmationResult(
                GiftApplicationConfirmationResultKind.EventNotFound,
                Event: null,
                AppliedSnapshotId: null,
                FailureReason: ServerLocalization.Text("Gift.ConfirmationEventNotFound"));
        }

        if (ledgerEvent.Type is not ServerEventType.Gift and not ServerEventType.GiftReturn
            || ledgerEvent.Payload is not GiftEventPayload)
        {
            return Rejected(
                GiftApplicationConfirmationResultKind.NotGiftEvent,
                ledgerEvent,
                ServerLocalization.Text("Gift.ConfirmationNotGiftEvent"));
        }

        if (!IsTarget(ledgerEvent, request.OwnerId, request.ColonyId))
        {
            return Rejected(
                GiftApplicationConfirmationResultKind.NotTarget,
                ledgerEvent,
                ServerLocalization.Text("Gift.ConfirmationNotTarget"));
        }

        if (ledgerEvent.Status == ServerEventStatus.AppliedToSnapshot)
        {
            return new GiftApplicationConfirmationResult(
                GiftApplicationConfirmationResultKind.AlreadyApplied,
                ledgerEvent,
                ledgerEvent.AppliedSnapshotId,
                FailureReason: null);
        }

        if (ledgerEvent.Status == ServerEventStatus.RejectedByTarget)
        {
            return Rejected(
                GiftApplicationConfirmationResultKind.RejectedByTarget,
                ledgerEvent,
                ServerLocalization.Text("Gift.ConfirmationRejectedByTarget"));
        }

        if (string.IsNullOrWhiteSpace(ledgerEvent.DeliveredToSnapshotId))
        {
            AuthoritativeEvent changed = ledger.ReportApplicationResult(
                ledgerEvent.EventId,
                EventApplicationResultKind.SnapshotBaseMismatch,
                ServerLocalization.Text("Gift.ConfirmationNotDelivered"),
                nextRetryAtUtc: null);
            return Rejected(
                GiftApplicationConfirmationResultKind.NotDelivered,
                changed,
                ServerLocalization.Text("Gift.ConfirmationNotDelivered"));
        }

        SnapshotIdentity confirmedIdentity = request.ConfirmedSnapshot.Identity;
        if (!IdentityMatches(request, confirmedIdentity))
        {
            AuthoritativeEvent changed = ledger.ReportApplicationResult(
                ledgerEvent.EventId,
                EventApplicationResultKind.SnapshotBaseMismatch,
                ServerLocalization.Text("Gift.ConfirmationSnapshotIdentityMismatch"),
                nextRetryAtUtc: null);
            return Rejected(
                GiftApplicationConfirmationResultKind.SnapshotIdentityMismatch,
                changed,
                ServerLocalization.Text("Gift.ConfirmationSnapshotIdentityMismatch"));
        }

        if (!string.Equals(request.BaseSnapshotId, ledgerEvent.DeliveredToSnapshotId, StringComparison.Ordinal))
        {
            AuthoritativeEvent changed = ledger.ReportApplicationResult(
                ledgerEvent.EventId,
                EventApplicationResultKind.SnapshotBaseMismatch,
                ServerLocalization.Text("Gift.ConfirmationSnapshotBaseMismatch"),
                nextRetryAtUtc: null);
            return Rejected(
                GiftApplicationConfirmationResultKind.SnapshotBaseMismatch,
                changed,
                ServerLocalization.Text("Gift.ConfirmationSnapshotBaseMismatch"));
        }

        if (!IsAnchoredClientResult(request.ClientApplicationResult))
        {
            AuthoritativeEvent changed = ledger.ReportApplicationResult(
                ledgerEvent.EventId,
                EventApplicationResultKind.NeedsManualReview,
                ServerLocalization.Text("Gift.ConfirmationNotAnchored"),
                nextRetryAtUtc: null);
            return Rejected(
                GiftApplicationConfirmationResultKind.NotAnchored,
                changed,
                ServerLocalization.Text("Gift.ConfirmationNotAnchored"));
        }

        AuthoritativeEvent applied = ledger.MarkApplied(
            ledgerEvent.EventId,
            confirmedIdentity.SnapshotId!,
            confirmedAtUtc);
        AuthoritativeEvent reported = ledger.ReportApplicationResult(
            ledgerEvent.EventId,
            EventApplicationResultKind.Applied,
            failureReason: null,
            nextRetryAtUtc: null);

        return new GiftApplicationConfirmationResult(
            GiftApplicationConfirmationResultKind.Accepted,
            reported,
            applied.AppliedSnapshotId,
            FailureReason: null);
    }

    private static bool IsTarget(AuthoritativeEvent ledgerEvent, string ownerId, string colonyId)
    {
        return string.Equals(ledgerEvent.Target.UserId, ownerId, StringComparison.Ordinal)
            && string.Equals(ledgerEvent.Target.ColonyId, colonyId, StringComparison.Ordinal);
    }

    private static bool IdentityMatches(GiftApplicationConfirmationRequest request, SnapshotIdentity confirmedIdentity)
    {
        return string.Equals(request.OwnerId, confirmedIdentity.OwnerId, StringComparison.Ordinal)
            && string.Equals(request.ColonyId, confirmedIdentity.ColonyId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(confirmedIdentity.SnapshotId);
    }

    private static bool IsAnchoredClientResult(string clientApplicationResult)
    {
        return string.Equals(clientApplicationResult, "GiftAnchored", StringComparison.Ordinal)
            || string.Equals(clientApplicationResult, "GiftReturnAnchored", StringComparison.Ordinal);
    }

    private static GiftApplicationConfirmationResult Rejected(
        GiftApplicationConfirmationResultKind kind,
        AuthoritativeEvent ledgerEvent,
        string failureReason)
    {
        return new GiftApplicationConfirmationResult(
            kind,
            ledgerEvent,
            AppliedSnapshotId: null,
            failureReason);
    }
}
