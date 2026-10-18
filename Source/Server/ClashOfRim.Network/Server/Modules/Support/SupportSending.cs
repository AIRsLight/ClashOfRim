using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Modules.Economy;
using static AIRsLight.ClashOfRim.Network.Modules.Transfers.ItemTransferMapper;
using static AIRsLight.ClashOfRim.Network.Modules.Events.EventProtocolMapper;
using static AIRsLight.ClashOfRim.Network.Modules.Snapshots.SnapshotTransactions;

namespace AIRsLight.ClashOfRim.Network.Modules.Support;

internal sealed class SupportSending(IAuthoritativeEventLedger ledger, IColonySnapshotIndexStore snapshots,
    SnapshotRequestAuthorization requests, SnapshotTransactions transactions, PendingConfirmationReconciler pending,
    OnlinePresenceRegistry presence, EventNotificationHub notifications, SupportSnapshotCommitter committer)
{
    internal EventCreationResponse CreateWithSnapshot(CreateSupportPawnWithSnapshotRequest transaction, byte[] bytes, string? principalToken)
    {
        var request = transaction.SupportPawn;
        if (request is null || transaction.ConfirmedSnapshot is null || string.IsNullOrWhiteSpace(transaction.ConfirmedSnapshot.SnapshotId))
            return SupportStartRejected(ProtocolErrorCode.ValidationFailed, "Support.TransactionMissingFields");
        if (ValidateSupportPawnRequest(request) is { } invalid)
            return new EventCreationResponse(invalid, null, ProtocolDeliverySemantics.OnlineImmediate);
        var metadata = transaction.ConfirmedSnapshot;
        if (metadata.OwnerId != request.Actor.UserId || metadata.ColonyId != request.Actor.ColonyId
            || request.Actor.SnapshotId != request.SourceSnapshotId || metadata.PreviousSnapshotId != request.SourceSnapshotId)
            return SupportStartRejected(ProtocolErrorCode.Conflict, "Support.TransactionConflict");
        if (!TryToPawnExchangePackage(request.PawnPackage, out var pawn, out string failure) || pawn is null)
            return new EventCreationResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                ServerText.Format("Support.PawnPackageInvalid", ("MESSAGE", failure))), null, ProtocolDeliverySemantics.OnlineImmediate);
        try { _ = SafePawnExchangeSerializer.Serialize(pawn); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new EventCreationResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                ServerText.Format("Support.PawnPackageInvalid", ("MESSAGE", ex.Message))), null, ProtocolDeliverySemantics.OnlineImmediate);
        }
        string binding = SupportSnapshotMutation.RequestHash(transaction, bytes);
        using (var recovery = requests.EnterPrincipal(principalToken, request.Actor))
        {
            if (recovery.Rejection is { } denied) return new(denied, null, ProtocolDeliverySemantics.OnlineImmediate);
            if (ledger.FindByIdempotencyKey(request.IdempotencyKey) is { } existing)
                return Replay(transaction, pawn, binding, existing);
            var rejection = ValidateStart(request, out _);
            if (rejection is not null) return new EventCreationResponse(rejection, null, ProtocolDeliverySemantics.OnlineImmediate);
        }
        var upload = transactions.PrepareTransactionSnapshot(request.Actor.UserId, request.Actor.ColonyId!, metadata.SnapshotId,
            metadata, bytes, DateTimeOffset.UtcNow);
        // Even a preparation rejection can race a successful identical commit; check its receipt under the gate first.
        using (var recovery = requests.EnterPrincipal(principalToken, request.Actor))
        {
            if (recovery.Rejection is { } denied) return new(denied, null, ProtocolDeliverySemantics.OnlineImmediate);
            if (ledger.FindByIdempotencyKey(request.IdempotencyKey) is { } existing)
                return Replay(transaction, pawn, binding, existing);
            if (!upload.Accepted || upload.AcceptedSnapshot is null)
                return new EventCreationResponse(ToProtocolResponse(upload), null, ProtocolDeliverySemantics.OnlineImmediate);
            var rejection = ValidateStart(request, out var targetContext);
            if (rejection is not null) return new EventCreationResponse(rejection, null, ProtocolDeliverySemantics.OnlineImmediate);
            var package = ProtocolDtoMapper.ToSaveSnapshotPackage(metadata, bytes);
            upload = transactions.CreateSnapshotReceiver().CommitPrepared(new SnapshotUploadContext(request.Actor.UserId,
                request.Actor.ColonyId!, metadata.SnapshotId, null, upload.SnapshotUploadKind), package, upload, storeAccepted: false);
            if (!upload.Accepted || upload.AcceptedSnapshot is null)
                return new EventCreationResponse(ToProtocolResponse(upload), null, ProtocolDeliverySemantics.OnlineImmediate);
            bool online = presence.IsUserOnline(request.Target.UserId);
            var support = CreateSupportSnapshotEvent(request, metadata.SnapshotId, pawn, binding, targetContext, online);
            committer.Commit(package, upload, support);
            return new EventCreationResponse(ProtocolResponse.Ok(ServerText.Format("Support.Created")), support.EventId,
                online ? ProtocolDeliverySemantics.OnlineImmediate : ProtocolDeliverySemantics.OfflinePending,
                metadata.SnapshotId, upload.AcceptedSnapshot.Envelope.NextLineageToken);
        }
    }

    private ProtocolResponse? ValidateStart(CreateSupportPawnRequest request,
        out EventTargetContext? context)
    {
        context = null;
        if (snapshots.GetLatest(request.Actor.UserId, request.Actor.ColonyId!)?.Identity.SnapshotId != request.SourceSnapshotId)
            return ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, ServerText.Format("Support.TransactionConflict"));
        var target = snapshots.GetLatest(request.Target.UserId, request.Target.ColonyId!);
        if (target is null) return ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, ServerText.Format("Support.TargetSnapshotNotFound"));
        context = request.TargetContext is null ? ResolveSupportPawnTargetContext(target) : ToEventTargetContext(request.TargetContext);
        if (string.IsNullOrWhiteSpace(context?.MapUniqueId))
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("Support.TargetMapMissing"));
        return pending.ReconcileSnapshot(request.Actor.UserId, request.Actor.ColonyId!, DateTimeOffset.UtcNow);
    }

    private static AuthoritativeEvent CreateSupportSnapshotEvent(CreateSupportPawnRequest request, string snapshotId,
        PawnExchangePackage pawn, string binding, EventTargetContext? context, bool online) =>
        AuthoritativeEventFactory.Create(ServerEventType.SupportPawn, ToEventParty(request.Actor), ToEventParty(request.Target),
            request.IdempotencyKey, online, new SupportPawnEventPayload(request.PawnGlobalKey, snapshotId, request.PawnName,
                request.TemporaryControl, request.ExpectedReturnAtUtc, ToCrossMapPawnReference(request.PawnReference), pawn,
                request.SourceTile, request.SourceCaravanLoadId, false, null, request.PermanentSupport,
                request.SupportDurationDays, request.ExpiresAtGameTicks, request.AutoReturnOnSettlement, null, null, binding),
            DateTimeOffset.UtcNow, context);

    private EventCreationResponse Replay(CreateSupportPawnWithSnapshotRequest transaction,
        PawnExchangePackage pawn, string binding, AuthoritativeEvent existing)
    {
        var request = transaction.SupportPawn;
        var context = request.TargetContext is null ? existing.TargetContext : ToEventTargetContext(request.TargetContext);
        var expected = CreateSupportSnapshotEvent(request, transaction.ConfirmedSnapshot.SnapshotId, pawn, binding, context, false);
        if (!SupportSnapshotMutation.MatchesContent(existing, expected))
            return SupportStartRejected(ProtocolErrorCode.Conflict, "Support.TransactionConflict");
        notifications.SignalUser(existing.Target.UserId);
        return new EventCreationResponse(new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest,
            ServerText.Format("Support.DuplicateTransaction")), existing.EventId,
            existing.Status == ServerEventStatus.ReadyForImmediateDelivery ? ProtocolDeliverySemantics.OnlineImmediate : ProtocolDeliverySemantics.OfflinePending,
            transaction.ConfirmedSnapshot.SnapshotId, transactions.FindSnapshotNextLineageToken(request.Actor.UserId,
                request.Actor.ColonyId!, transaction.ConfirmedSnapshot.SnapshotId));
    }

    private static EventCreationResponse SupportStartRejected(ProtocolErrorCode code, string key) =>
        new EventCreationResponse(ProtocolResponse.Reject(code, ServerText.Format(key)), null, ProtocolDeliverySemantics.OnlineImmediate);
    private static ProtocolResponse? ValidateSupportPawnRequest(CreateSupportPawnRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.Actor is null
            || request.Target is null
            || string.IsNullOrWhiteSpace(request.Actor.UserId)
            || string.IsNullOrWhiteSpace(request.Actor.ColonyId)
            || string.IsNullOrWhiteSpace(request.Actor.SnapshotId)
            || string.IsNullOrWhiteSpace(request.Target.UserId)
            || string.IsNullOrWhiteSpace(request.Target.ColonyId)
            || string.IsNullOrWhiteSpace(request.PawnGlobalKey)
            || string.IsNullOrWhiteSpace(request.SourceSnapshotId))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                ServerText.Format("Support.CreateMissingFields"));
        }

        if (request.PawnReference is not null
            && !string.Equals(request.PawnReference.GlobalId, request.PawnGlobalKey, StringComparison.Ordinal))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                ServerText.Format("Support.ReferenceMismatch"));
        }

        if (request.PawnPackage is not null
            && (request.PawnPackage.Reference is null
                || !string.Equals(request.PawnPackage.Reference.GlobalId, request.PawnGlobalKey, StringComparison.Ordinal)))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                ServerText.Format("Support.PackageReferenceMismatch"));
        }

        if (request.PermanentSupport)
        {
            return null;
        }

        if (request.SupportDurationDays is < 3 or > 30)
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                ServerText.Format("Support.TemporaryDurationInvalid"));
        }

        return null;
    }

    private static EventTargetContext? ResolveSupportPawnTargetContext(LatestSnapshotRecord targetSnapshot)
    {
        MapSummary? map = targetSnapshot.Index.Maps
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.UniqueId));
        if (map is null)
        {
            return null;
        }

        return new EventTargetContext(
            map.ParentWorldObjectId,
            map.UniqueId,
            Tile: null,
            EventLandingMode.MapEdge);
    }

}
