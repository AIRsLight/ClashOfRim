using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult CommitSupportWithSnapshot(HttpRequest http, ClashOfRimNetworkState state,
        CreateSupportPawnWithSnapshotRequest transaction, byte[] bytes)
    {
        var request = transaction.SupportPawn;
        var metadata = transaction.ConfirmedSnapshot;
        if (metadata.OwnerId != request.Actor.UserId || metadata.ColonyId != request.Actor.ColonyId
            || request.Actor.SnapshotId != request.SourceSnapshotId || metadata.PreviousSnapshotId != request.SourceSnapshotId)
            return SupportStartRejected(ProtocolErrorCode.Conflict, "Support.TransactionConflict");
        if (!TryToPawnExchangePackage(request.PawnPackage, out var pawn, out string failure) || pawn is null)
            return Results.Ok(new EventCreationResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                T("Support.PawnPackageInvalid", ("MESSAGE", failure))), null, ProtocolDeliverySemantics.OnlineImmediate));
        try { _ = SafePawnExchangeSerializer.Serialize(pawn); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Results.Ok(new EventCreationResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                T("Support.PawnPackageInvalid", ("MESSAGE", ex.Message))), null, ProtocolDeliverySemantics.OnlineImmediate));
        }
        string binding = SupportSnapshotMutation.RequestHash(transaction, bytes);
        using (state.EnterSnapshotMutation())
        {
            if (!AuthorizeSupportCommit(http, state, request)) return SupportStartRejected(ProtocolErrorCode.Unauthorized, "Auth.Failed");
            RecoverSnapshotCommits(state);
            if (state.Ledger.FindByIdempotencyKey(request.IdempotencyKey) is { } existing)
                return SupportSnapshotReplayResponse(state, transaction, pawn, binding, existing);
            var rejection = ValidateSupportSnapshotStart(state, request, out _);
            if (rejection is not null) return Results.Ok(new EventCreationResponse(rejection, null, ProtocolDeliverySemantics.OnlineImmediate));
        }
        var upload = ReceiveSnapshot(state, request.Actor.UserId, request.Actor.ColonyId!, metadata.SnapshotId,
            metadata, bytes, DateTimeOffset.UtcNow, storeAccepted: false);
        // Even a preparation rejection can race a successful identical commit; check its receipt under the gate first.
        using (state.EnterSnapshotMutation())
        {
            if (!AuthorizeSupportCommit(http, state, request)) return SupportStartRejected(ProtocolErrorCode.Unauthorized, "Auth.Failed");
            RecoverSnapshotCommits(state);
            if (state.Ledger.FindByIdempotencyKey(request.IdempotencyKey) is { } existing)
                return SupportSnapshotReplayResponse(state, transaction, pawn, binding, existing);
            if (!upload.Accepted || upload.AcceptedSnapshot is null)
                return Results.Ok(new EventCreationResponse(ToProtocolResponse(upload), null, ProtocolDeliverySemantics.OnlineImmediate));
            var rejection = ValidateSupportSnapshotStart(state, request, out var targetContext);
            if (rejection is not null) return Results.Ok(new EventCreationResponse(rejection, null, ProtocolDeliverySemantics.OnlineImmediate));
            var package = ProtocolDtoMapper.ToSaveSnapshotPackage(metadata, bytes);
            upload = CreateSnapshotReceiver(state).CommitPrepared(new SnapshotUploadContext(request.Actor.UserId,
                request.Actor.ColonyId!, metadata.SnapshotId, null, upload.SnapshotUploadKind), package, upload, storeAccepted: false);
            if (!upload.Accepted || upload.AcceptedSnapshot is null)
                return Results.Ok(new EventCreationResponse(ToProtocolResponse(upload), null, ProtocolDeliverySemantics.OnlineImmediate));
            bool online = state.OnlinePresence.IsUserOnline(request.Target.UserId);
            var support = CreateSupportSnapshotEvent(request, metadata.SnapshotId, pawn, binding, targetContext, online);
            state.SnapshotCommits.CommitSupport(state.SnapshotStore, state.BankLoans, state.Ledger, package,
                upload.AcceptedSnapshot, new SupportSnapshotMutation(support),
                (snapshot, kind) => RunCommittedSnapshotPostUpload(state, snapshot, kind),
                user => state.EventNotifications.SignalUser(user), upload.SnapshotUploadKind, state.MercenaryContracts);
            return Results.Ok(new EventCreationResponse(ProtocolResponse.Ok(T("Support.Created")), support.EventId,
                online ? ProtocolDeliverySemantics.OnlineImmediate : ProtocolDeliverySemantics.OfflinePending,
                metadata.SnapshotId, upload.AcceptedSnapshot.Envelope.NextLineageToken));
        }
    }

    private static bool AuthorizeSupportCommit(HttpRequest http, ClashOfRimNetworkState state, CreateSupportPawnRequest request) =>
        MultipartSnapshotAuthentication.TryAuthorize(http, state, DateTimeOffset.UtcNow)
        && MultipartSnapshotAuthentication.MatchesPrincipal(http, request.Actor.UserId, request.Actor.ColonyId);

    private static ProtocolResponse? ValidateSupportSnapshotStart(ClashOfRimNetworkState state, CreateSupportPawnRequest request,
        out EventTargetContext? context)
    {
        context = null;
        if (state.SnapshotStore.GetLatest(request.Actor.UserId, request.Actor.ColonyId!)?.Identity.SnapshotId != request.SourceSnapshotId)
            return ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("Support.TransactionConflict"));
        var target = state.SnapshotStore.GetLatest(request.Target.UserId, request.Target.ColonyId!);
        if (target is null) return ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("Support.TargetSnapshotNotFound"));
        context = request.TargetContext is null ? ResolveSupportPawnTargetContext(target) : ToEventTargetContext(request.TargetContext);
        if (string.IsNullOrWhiteSpace(context?.MapUniqueId))
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Support.TargetMapMissing"));
        if (TryRejectExpiredPendingConfirmationSnapshot(state, request.Actor.UserId, request.Actor.ColonyId!,
            DateTimeOffset.UtcNow, out var rejection)) return rejection;
        return null;
    }

    private static AuthoritativeEvent CreateSupportSnapshotEvent(CreateSupportPawnRequest request, string snapshotId,
        PawnExchangePackage pawn, string binding, EventTargetContext? context, bool online) =>
        AuthoritativeEventFactory.Create(ServerEventType.SupportPawn, ToEventParty(request.Actor), ToEventParty(request.Target),
            request.IdempotencyKey, online, new SupportPawnEventPayload(request.PawnGlobalKey, snapshotId, request.PawnName,
                request.TemporaryControl, request.ExpectedReturnAtUtc, ToCrossMapPawnReference(request.PawnReference), pawn,
                request.SourceTile, request.SourceCaravanLoadId, false, null, request.PermanentSupport,
                request.SupportDurationDays, request.ExpiresAtGameTicks, request.AutoReturnOnSettlement, null, null, binding),
            DateTimeOffset.UtcNow, context);

    private static IResult SupportSnapshotReplayResponse(ClashOfRimNetworkState state, CreateSupportPawnWithSnapshotRequest transaction,
        PawnExchangePackage pawn, string binding, AuthoritativeEvent existing)
    {
        var request = transaction.SupportPawn;
        var context = request.TargetContext is null ? existing.TargetContext : ToEventTargetContext(request.TargetContext);
        var expected = CreateSupportSnapshotEvent(request, transaction.ConfirmedSnapshot.SnapshotId, pawn, binding, context, false);
        if (!SupportSnapshotMutation.MatchesContent(existing, expected))
            return SupportStartRejected(ProtocolErrorCode.Conflict, "Support.TransactionConflict");
        state.EventNotifications.SignalUser(existing.Target.UserId);
        return Results.Ok(new EventCreationResponse(new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest,
            T("Support.DuplicateTransaction")), existing.EventId,
            existing.Status == ServerEventStatus.ReadyForImmediateDelivery ? ProtocolDeliverySemantics.OnlineImmediate : ProtocolDeliverySemantics.OfflinePending,
            transaction.ConfirmedSnapshot.SnapshotId, FindSnapshotNextLineageToken(state, request.Actor.UserId,
                request.Actor.ColonyId!, transaction.ConfirmedSnapshot.SnapshotId)));
    }

    private static IResult SupportStartRejected(ProtocolErrorCode code, string key) =>
        Results.Ok(new EventCreationResponse(ProtocolResponse.Reject(code, T(key)), null, ProtocolDeliverySemantics.OnlineImmediate));
}
