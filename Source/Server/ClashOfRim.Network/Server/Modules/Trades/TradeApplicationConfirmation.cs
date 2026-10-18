using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Trades;

// Uses the selected confirmation ledger; durable publication remains with the commit coordinator.
internal static class TradeApplicationConfirmation
{
    internal static ConfirmEventApplicationResultDto Confirm(
        ConfirmEventApplicationEntry application,
        string userId,
        string colonyId,
        string baseSnapshotId,
        LatestSnapshotRecord acceptedSnapshot,
        AuthoritativeEvent exchangeEvent,
        TradeEventPayload exchangePayload,
        IAuthoritativeEventLedger ledger,
        DateTimeOffset nowUtc, Action<AuthoritativeEvent>? recordTrade = null)
    {
        if (!EventVisibility.MatchesParty(exchangeEvent.Actor, userId, colonyId))
        {
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, ServerText.Format("Trade.ExchangeActorMismatch")),
                appliedSnapshotId: null,
                "AcceptorIdentityMismatch");
        }

        if (string.IsNullOrWhiteSpace(application.SourceEventId)
            || !string.Equals(application.SourceEventId, exchangePayload.TradeId, StringComparison.Ordinal))
        {
            if (exchangeEvent.Status != ServerEventStatus.AppliedToSnapshot)
                ledger.ReportApplicationResult(
                    exchangeEvent.EventId,
                    EventApplicationResultKind.SnapshotBaseMismatch,
                    ServerText.Format("Trade.ExchangeSourceMismatch"),
                    nextRetryAtUtc: null);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, ServerText.Format("Trade.ExchangeSourceMismatch")),
                appliedSnapshotId: null,
                "SourceTradeMismatch");
        }

        if (exchangeEvent.Status == ServerEventStatus.AppliedToSnapshot)
        {
            AuthoritativeEvent repaired = SnapshotApplicationCommit.Apply(ledger, exchangeEvent,
                baseSnapshotId, acceptedSnapshot.Identity.SnapshotId!, nowUtc);
            if (!string.IsNullOrWhiteSpace(repaired.AppliedSnapshotId) && repaired.AppliedAtUtc is { } appliedAtUtc)
                recordTrade?.Invoke(repaired);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerText.Format("Trade.ExchangeAlreadyConfirmed")),
                repaired.AppliedSnapshotId,
                "AlreadyApplied");
        }

        SnapshotIdentity identity = acceptedSnapshot.Identity;
        if (!string.Equals(identity.OwnerId, userId, StringComparison.Ordinal)
            || !string.Equals(identity.ColonyId, colonyId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(identity.SnapshotId))
        {
            ledger.ReportApplicationResult(
                exchangeEvent.EventId,
                EventApplicationResultKind.SnapshotBaseMismatch,
                ServerText.Format("Trade.ExchangeSnapshotIdentityMismatch"),
                nextRetryAtUtc: null);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.IdentityMismatch, ServerText.Format("Trade.ExchangeSnapshotIdentityMismatch")),
                appliedSnapshotId: null,
                "SnapshotIdentityMismatch");
        }

        if (!TradeExchangeBaseSnapshotMatches(exchangePayload, baseSnapshotId)
            || !string.Equals(acceptedSnapshot.Envelope.PreviousSnapshotId, baseSnapshotId, StringComparison.Ordinal))
        {
            ledger.ReportApplicationResult(
                exchangeEvent.EventId,
                EventApplicationResultKind.SnapshotBaseMismatch,
                ServerText.Format("Trade.ExchangeBaseSnapshotMismatch"),
                nextRetryAtUtc: null);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, ServerText.Format("Trade.ExchangeBaseSnapshotMismatch")),
                appliedSnapshotId: null,
                "SnapshotBaseMismatch");
        }

        AuthoritativeEvent applied = SnapshotApplicationCommit.Apply(ledger, exchangeEvent,
            baseSnapshotId, identity.SnapshotId!, nowUtc);
        recordTrade?.Invoke(applied);

        return new ConfirmEventApplicationResultDto(
            application.EventId,
            ProtocolResponse.Ok(ServerText.Format("Trade.ExchangeSnapshotConfirmed")),
            applied.AppliedSnapshotId,
            exchangePayload.Stage.ToString());
    }

    private static bool TradeExchangeBaseSnapshotMatches(TradeEventPayload payload, string baseSnapshotId)
    {
        if (string.IsNullOrWhiteSpace(baseSnapshotId))
        {
            return false;
        }

        IReadOnlyList<EventThingReference> deliveredByAcceptor = payload.RequestedItems;
        return deliveredByAcceptor.Count > 0
            && deliveredByAcceptor.All(thing =>
                string.Equals(thing.SourceSnapshotId, baseSnapshotId, StringComparison.Ordinal));
    }
}
