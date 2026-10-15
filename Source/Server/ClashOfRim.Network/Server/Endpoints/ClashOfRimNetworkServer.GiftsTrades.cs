using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using System.Globalization;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    // Keep the route as an explicit upgrade response, without binding state or recovering pending writes.
    private static IResult CreateGift() => Results.Ok(new EventCreationResponse(
        ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Gift.SnapshotEndpointRequired")),
        eventId: null, ProtocolDeliverySemantics.ServerNotification));

    private static bool ReauthorizeSnapshotPrincipal(HttpRequest http, ClashOfRimNetworkState state, ProtocolIdentity identity) =>
        MultipartSnapshotAuthentication.TryAuthorize(http, state, DateTimeOffset.UtcNow)
        && MultipartSnapshotAuthentication.MatchesPrincipal(http, identity.UserId, identity.ColonyId);

    private static async Task<IResult> CreateGiftWithSnapshot(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        MultipartSnapshotRequest<CreateGiftWithSnapshotRequest>? multipart =
            await ReadMultipartSnapshotRequest<CreateGiftWithSnapshotRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Gift.TransactionMissingPayload")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        CreateGiftWithSnapshotRequest transaction = multipart.Request;
        CreateGiftRequest? request = transaction.Gift;
        if (request is null)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Gift.TransactionMissingGift")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        ProtocolResponse? validation = ValidateGiftCreationRequest(request);
        if (validation is not null)
        {
            return Results.Ok(new EventCreationResponse(
                validation,
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        if (!state.ServerConfiguration.GiftsEnabled)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Gift.Disabled")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        if (transaction.ConfirmedSnapshot is null
            || string.IsNullOrWhiteSpace(transaction.ConfirmedSnapshot.SnapshotId))
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Gift.TransactionMissingSnapshot")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        if (!MultipartSnapshotAuthentication.MatchesPrincipal(
                httpRequest,
                request.Actor.UserId,
                request.Actor.ColonyId))
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        using (state.EnterSnapshotMutation())
        {
            if (!ReauthorizeSnapshotPrincipal(httpRequest, state, request.Actor))
                return Results.Ok(new EventCreationResponse(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")), null, ProtocolDeliverySemantics.OnlineImmediate));
            RecoverSnapshotCommits(state);
            AuthoritativeEvent? existingEvent = FindEventByIdempotencyKey(state.Ledger, request.IdempotencyKey);
            if (existingEvent is not null)
                return GiftSnapshotReplayResponse(state, request, transaction.ConfirmedSnapshot.SnapshotId!, existingEvent);
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        EventTargetContext? targetContext = ResolveGiftTargetContext(request, state);
        if (IsForcedGiftDelivery(request))
        {
            ProtocolResponse? forcedValidation = ValidateForcedGiftDelivery(request, state, nowUtc, targetContext);
            if (forcedValidation is not null)
            {
                return Results.Ok(new EventCreationResponse(
                    forcedValidation,
                    eventId: null,
                    ProtocolDeliverySemantics.ServerNotification));
            }
        }

        if (!TryValidateInlinePawnPackages(request.Things, out ProtocolResponse? packageFailure)
            || !TryValidateInlineThingPackages(request.Things, out packageFailure))
        {
            return Results.Ok(new EventCreationResponse(
                packageFailure ?? ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.ParseFailed")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        if (TryRejectExpiredPendingConfirmationSnapshot(
                state,
                request.Actor.UserId,
                request.Actor.ColonyId ?? string.Empty,
                nowUtc,
                out ProtocolResponse? pendingRejection))
        {
            return Results.Ok(new EventCreationResponse(
                pendingRejection!,
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        SnapshotUploadResult upload = ReceiveSnapshot(
            state,
            request.Actor.UserId,
            request.Actor.ColonyId ?? string.Empty,
            transaction.ConfirmedSnapshot.SnapshotId!,
            transaction.ConfirmedSnapshot,
            multipart.Payload,
            nowUtc, storeAccepted: false);
        using (state.EnterSnapshotMutation())
        {
            if (!ReauthorizeSnapshotPrincipal(httpRequest, state, request.Actor))
                return Results.Ok(new EventCreationResponse(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")), null, ProtocolDeliverySemantics.OnlineImmediate));
            RecoverSnapshotCommits(state);
            AuthoritativeEvent? existingEvent = FindEventByIdempotencyKey(state.Ledger, request.IdempotencyKey);
            if (existingEvent is not null)
                return GiftSnapshotReplayResponse(state, request, transaction.ConfirmedSnapshot.SnapshotId!, existingEvent);
            if (!upload.Accepted || upload.AcceptedSnapshot is null)
            {
                return Results.Ok(new EventCreationResponse(
                    ToProtocolResponse(upload),
                    eventId: null,
                    ProtocolDeliverySemantics.OnlineImmediate));
            }
            upload = CreateSnapshotReceiver(state).CommitPrepared(
                new SnapshotUploadContext(request.Actor.UserId, request.Actor.ColonyId ?? string.Empty,
                    transaction.ConfirmedSnapshot.SnapshotId!, null, upload.SnapshotUploadKind),
                ProtocolDtoMapper.ToSaveSnapshotPackage(transaction.ConfirmedSnapshot, multipart.Payload),
                upload, storeAccepted: false);
            if (!upload.Accepted || upload.AcceptedSnapshot is null)
                return Results.Ok(new EventCreationResponse(ToProtocolResponse(upload), null,
                    ProtocolDeliverySemantics.OnlineImmediate));
            string acceptedSnapshotId = upload.AcceptedSnapshot.Identity.SnapshotId
                ?? transaction.ConfirmedSnapshot.SnapshotId!;
            bool targetOnline = state.OnlinePresence.IsUserOnline(request.Target.UserId);
            var acceptedActor = new ProtocolIdentity(request.Actor.UserId, request.Actor.ColonyId, acceptedSnapshotId);
            string giftCommitId = Guid.NewGuid().ToString("N");
            if (!TryStoreInlinePawnPackages(
                    state,
                    acceptedActor,
                    "gift-commit:" + giftCommitId,
                    request.Things,
                    out IReadOnlyList<ThingReferenceDto> giftThings,
                    out packageFailure))
            {
                return Results.Ok(new EventCreationResponse(
                    packageFailure ?? ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.ParseFailed")),
                    eventId: null,
                    ProtocolDeliverySemantics.OnlineImmediate));
            }

            AuthoritativeEvent giftEvent = AuthoritativeEventFactory.CreateForLedger(state.Ledger,
                ServerEventType.ItemDelivery,
                ToEventParty(acceptedActor),
                ToEventParty(request.Target),
                request.IdempotencyKey,
                targetOnline,
                new ItemDeliveryEventPayload(
                    giftThings.Select(thing => ToEventThingReference(thing, acceptedSnapshotId)).ToList(),
                    request.Message,
                    NormalizeGiftDeliveryKind(request.DeliveryKind)),
                nowUtc,
                targetContext) with
            { EventId = "gift:" + giftCommitId };

            state.SnapshotCommits.CommitGift(state.SnapshotStore, state.BankLoans, state.Ledger,
                ProtocolDtoMapper.ToSaveSnapshotPackage(transaction.ConfirmedSnapshot, multipart.Payload),
                upload.AcceptedSnapshot, giftEvent,
                (snapshot, kind) => RunCommittedSnapshotPostUpload(state, snapshot, kind),
                userId => state.EventNotifications.SignalUsers(new[] { userId }), upload.SnapshotUploadKind, state.MercenaryContracts, preparedStoragePayload: upload.PreparedStoragePayload);
            var append = new LedgerAppendResult(giftEvent, Created: true);
            EventCreationResponse created = ToEventCreationResponse(
                append,
                targetOnline ? ProtocolDeliverySemantics.OnlineImmediate : ProtocolDeliverySemantics.OfflinePending,
                T("Gift.Created"));
            return Results.Ok(new EventCreationResponse(
                created.Result,
                created.EventId,
                created.DeliverySemantics,
                acceptedSnapshotId,
                upload.AcceptedSnapshot.Envelope.NextLineageToken));
        }
    }

    private static IResult GiftSnapshotReplayResponse(ClashOfRimNetworkState state, CreateGiftRequest request,
        string snapshotId, AuthoritativeEvent existing)
    {
        if (!GiftSnapshotReplayPolicy.Matches(existing, request.Actor.UserId, request.Actor.ColonyId, snapshotId)
            || existing.Target.UserId != request.Target.UserId || existing.Target.ColonyId != request.Target.ColonyId)
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, "Gift idempotency key belongs to another operation."),
                null, ProtocolDeliverySemantics.OnlineImmediate));
        return Results.Ok(new EventCreationResponse(
            new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Gift.TransactionDuplicate")),
            existing.EventId,
            existing.Status == ServerEventStatus.ReadyForImmediateDelivery
                ? ProtocolDeliverySemantics.OnlineImmediate : ProtocolDeliverySemantics.OfflinePending,
            snapshotId, FindSnapshotNextLineageToken(state, request.Actor.UserId, request.Actor.ColonyId ?? string.Empty, snapshotId)));
    }

    private static ProtocolResponse? ValidateGiftCreationRequest(CreateGiftRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.Actor is null
            || request.Target is null
            || string.IsNullOrWhiteSpace(request.Actor.UserId)
            || string.IsNullOrWhiteSpace(request.Target.UserId))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T("Gift.CreateMissingFields"));
        }

        if (request.Things is null || request.Things.Count == 0)
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T("Gift.Empty"));
        }

        foreach (ThingReferenceDto thing in request.Things)
        {
            if (string.IsNullOrWhiteSpace(thing.GlobalKey)
                || string.IsNullOrWhiteSpace(thing.DefName)
                || thing.StackCount <= 0)
            {
                return ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    T("Gift.InvalidThing"));
            }

            if (thing.PawnPackage is not null)
            {
                if (!TryToPawnExchangePackage(thing.PawnPackage, out PawnExchangePackage? package, out string packageFailure)
                    || package is null)
                {
                    return ProtocolResponse.Reject(
                        ProtocolErrorCode.ValidationFailed,
                        T("Gift.PawnPackageInvalid", ("MESSAGE", packageFailure)));
                }

                try
                {
                    _ = SafePawnExchangeSerializer.Serialize(package);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    return ProtocolResponse.Reject(
                        ProtocolErrorCode.ValidationFailed,
                        T("Gift.PawnPackageInvalid", ("MESSAGE", ex.Message)));
                }
            }

            if (thing.ThingPackage is not null
                && !TryToThingStatePackage(thing.ThingPackage, out _, out string thingPackageFailure))
            {
                return ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    "invalid thing package: " + thingPackageFailure);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.DeliveryKind)
            && !string.Equals(request.DeliveryKind, ItemDeliveryEventPayload.ForcedDeliveryKind, StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T("Gift.UnknownDeliveryKind"));
        }

        return null;
    }

    private static IResult StorePawnPackage(StorePawnPackageRequest request, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.Owner is null
            || string.IsNullOrWhiteSpace(request.Owner.UserId)
            || request.PawnPackage is null)
        {
            return Results.Ok(new StorePawnPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.UploadMissingFields")),
                pawnPackageId: null,
                pawnGlobalId: null));
        }

        if (!TryToPawnExchangePackage(request.PawnPackage, out PawnExchangePackage? package, out string packageFailure)
            || package is null)
        {
            return Results.Ok(new StorePawnPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.Invalid", ("MESSAGE", packageFailure))),
                pawnPackageId: null,
                pawnGlobalId: null));
        }

        try
        {
            StoredPawnPackageRecord record = state.PawnPackages.Store(
                request.IdempotencyKey,
                request.Owner.UserId,
                request.Owner.ColonyId,
                request.Owner.SnapshotId,
                package,
                DateTimeOffset.UtcNow);
            return Results.Ok(new StorePawnPackageResponse(
                ProtocolResponse.Ok(T("PawnPackage.Stored")),
                record.PackageId,
                record.PawnGlobalId));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Results.Ok(new StorePawnPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.Invalid", ("MESSAGE", ex.Message))),
                pawnPackageId: null,
                pawnGlobalId: null));
        }
    }

    private static IResult GetPawnPackage(GetPawnPackageRequest request, ClashOfRimNetworkState state)
    {
        if (request.Requester is null
            || string.IsNullOrWhiteSpace(request.Requester.UserId)
            || string.IsNullOrWhiteSpace(request.PawnPackageId))
        {
            return Results.Ok(new GetPawnPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.DownloadMissingFields")),
                request.PawnPackageId,
                pawnPackage: null));
        }

        if (!state.PawnPackages.TryGetPackage(request.PawnPackageId, out PawnExchangePackage? package, out string message)
            || package is null)
        {
            return Results.Ok(new GetPawnPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, message),
                request.PawnPackageId,
                pawnPackage: null));
        }

        return Results.Ok(new GetPawnPackageResponse(
            ProtocolResponse.Ok(T("PawnPackage.Returned")),
            request.PawnPackageId,
            ToPawnExchangePackageDto(package)));
    }

    private static async Task<IResult> StorePawnPackagePreview(
        HttpRequest httpRequest,
        ClashOfRimNetworkState state)
    {
        MultipartSnapshotRequest<StorePawnPackagePreviewRequest>? multipart =
            await ReadMultipartSnapshotRequest<StorePawnPackagePreviewRequest>(httpRequest);
        StorePawnPackagePreviewRequest? request = multipart?.Request;
        if (request?.Owner is null
            || string.IsNullOrWhiteSpace(request.Owner.UserId)
            || string.IsNullOrWhiteSpace(request.PawnPackageId)
            || multipart is null)
        {
            return Results.Ok(new StorePawnPackagePreviewResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.PreviewMissingFields")),
                request?.PawnPackageId,
                contentSha256: null));
        }

        if (!MultipartSnapshotAuthentication.MatchesPrincipal(
                httpRequest,
                request.Owner.UserId,
                request.Owner.ColonyId))
        {
            return Results.Ok(new StorePawnPackagePreviewResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")),
                request.PawnPackageId,
                contentSha256: null));
        }

        if (!state.PawnPackages.TryGetRecord(request.PawnPackageId, out StoredPawnPackageRecord? package)
            || package is null)
        {
            return Results.Ok(new StorePawnPackagePreviewResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("PawnPackage.NotFound")),
                request.PawnPackageId,
                contentSha256: null));
        }

        if (!string.Equals(package.OwnerUserId, request.Owner.UserId, StringComparison.Ordinal)
            || !string.Equals(package.OwnerColonyId, request.Owner.ColonyId, StringComparison.Ordinal))
        {
            return Results.Ok(new StorePawnPackagePreviewResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")),
                request.PawnPackageId,
                contentSha256: null));
        }

        if (!PawnPackagePreviewValidator.TryValidate(
                multipart.Payload,
                out int width,
                out int height,
                out string contentSha256,
                out string validationError))
        {
            return Results.Ok(new StorePawnPackagePreviewResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    T("PawnPackage.PreviewInvalid", ("MESSAGE", validationError))),
                request.PawnPackageId,
                contentSha256: null));
        }

        try
        {
            state.PawnPackages.StorePreview(new StoredPawnPackagePreviewRecord(
                request.PawnPackageId,
                contentSha256,
                multipart.Payload,
                width,
                height,
                DateTimeOffset.UtcNow));
            return Results.Ok(new StorePawnPackagePreviewResponse(
                ProtocolResponse.Ok(T("PawnPackage.PreviewStored")),
                request.PawnPackageId,
                contentSha256));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Ok(new StorePawnPackagePreviewResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ServerRejected,
                    T("PawnPackage.PreviewStoreFailed", ("MESSAGE", ex.Message))),
                request.PawnPackageId,
                contentSha256: null));
        }
    }

    private static IResult GetPawnPackagePreview(
        HttpRequest httpRequest,
        GetPawnPackagePreviewRequest request,
        ClashOfRimNetworkState state)
    {
        if (request.Requester is null
            || string.IsNullOrWhiteSpace(request.Requester.UserId)
            || string.IsNullOrWhiteSpace(request.PawnPackageId))
        {
            return Results.BadRequest(T("PawnPackage.PreviewMissingFields"));
        }

        if (!MultipartSnapshotAuthentication.MatchesPrincipal(
                httpRequest,
                request.Requester.UserId,
                request.Requester.ColonyId))
        {
            return Results.Unauthorized();
        }

        if (!state.PawnPackages.TryGetPreview(
                request.PawnPackageId,
                out StoredPawnPackagePreviewRecord? preview)
            || preview is null)
        {
            return Results.NotFound(T("PawnPackage.PreviewNotFound"));
        }

        return Results.File(
            preview.PngBytes,
            "image/png",
            enableRangeProcessing: false);
    }

    private static IResult StoreThingPackage(StoreThingPackageRequest request, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.Owner is null
            || string.IsNullOrWhiteSpace(request.Owner.UserId)
            || request.ThingPackage is null)
        {
            return Results.Ok(new StoreThingPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, "thing package upload missing fields"),
                thingPackageId: null,
                fingerprint: null));
        }

        if (!TryToThingStatePackage(request.ThingPackage, out ThingStatePackage? package, out string packageFailure)
            || package is null)
        {
            return Results.Ok(new StoreThingPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, "invalid thing package: " + packageFailure),
                thingPackageId: null,
                fingerprint: null));
        }

        try
        {
            StoredThingPackageRecord record = state.ThingPackages.Store(
                request.IdempotencyKey,
                request.Owner.UserId,
                request.Owner.ColonyId,
                request.Owner.SnapshotId,
                package,
                DateTimeOffset.UtcNow);
            return Results.Ok(new StoreThingPackageResponse(
                ProtocolResponse.Ok("thing package stored"),
                record.PackageId,
                record.Fingerprint));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or IOException)
        {
            return Results.Ok(new StoreThingPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, "invalid thing package: " + ex.Message),
                thingPackageId: null,
                fingerprint: null));
        }
    }

    private static IResult GetThingPackage(GetThingPackageRequest request, ClashOfRimNetworkState state)
    {
        if (request.Requester is null
            || string.IsNullOrWhiteSpace(request.Requester.UserId)
            || string.IsNullOrWhiteSpace(request.ThingPackageId))
        {
            return Results.Ok(new GetThingPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, "thing package download missing fields"),
                request.ThingPackageId,
                thingPackage: null));
        }

        if (!state.ThingPackages.TryGetPackage(request.ThingPackageId, out ThingStatePackage? package, out string message)
            || package is null)
        {
            return Results.Ok(new GetThingPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, message),
                request.ThingPackageId,
                thingPackage: null));
        }

        return Results.Ok(new GetThingPackageResponse(
            ProtocolResponse.Ok("thing package returned"),
            request.ThingPackageId,
            ToThingStatePackageDto(package)));
    }

    private static ProtocolResponse? ValidateForcedGiftDelivery(
        CreateGiftRequest request,
        ClashOfRimNetworkState state,
        DateTimeOffset nowUtc,
        EventTargetContext? targetContext)
    {
        if (!state.ServerConfiguration.PvpEnabled)
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ServerRejected,
                T("Pvp.Disabled"));
        }

        ReconcileExpiredRaidEvents(state, nowUtc);
        AuthoritativeEvent? defenderBlockingRaid = FindDefenderLoginBlockingRaid(
            state,
            request.Target.UserId,
            request.Target.ColonyId);
        if (defenderBlockingRaid is not null)
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ServerRejected,
                T("Gift.ForcedBlockedByRaid"));
        }

        LatestSnapshotRecord? defenderSnapshot = state.SnapshotStore.GetLatest(
            request.Target.UserId,
            request.Target.ColonyId ?? string.Empty);
        if (defenderSnapshot is null)
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.SnapshotMismatch,
                T("Gift.ForcedMissingTargetSnapshot"));
        }

        if (targetContext is null || string.IsNullOrWhiteSpace(targetContext.MapUniqueId))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T("Gift.ForcedMissingTargetMap"));
        }

        string relationKind = state.DiplomacyRelations.GetRelationKind(
            request.Actor.UserId,
            request.Actor.ColonyId,
            request.Target.UserId,
            request.Target.ColonyId);
        DateTimeOffset? cooldownUntilUtc = CurrentRaidCooldownUntil(
            state,
            request.Target.UserId,
            request.Target.ColonyId,
            nowUtc);
        RaidEligibilityResult eligibility = RaidEligibilityChecker.Check(new RaidEligibilityRequest(
            ToEventParty(request.Actor),
            ToEventParty(request.Target),
            string.Equals(relationKind, DiplomacyRelationRegistry.RelationHostile, StringComparison.Ordinal),
            state.OnlinePresence.IsUserOnline(request.Target.UserId),
            nowUtc,
            cooldownUntilUtc,
            DefenderWealth: int.MaxValue,
            defenderSnapshot.Identity,
            defenderSnapshot.Index.Maps,
            targetContext.MapUniqueId),
            BuildRaidEligibilityPolicy(state.ServerConfiguration));
        if (eligibility.Eligible)
        {
            return ValidateForcedGiftDeliveryCooldown(state, request, nowUtc);
        }

        return ProtocolResponse.Reject(
            ProtocolErrorCode.ServerRejected,
            T("Gift.ForcedRaidProtection", ("REASONS", string.Join(", ", eligibility.FailureReasons))));
    }

    private static ProtocolResponse? ValidateForcedGiftDeliveryCooldown(
        ClashOfRimNetworkState state,
        CreateGiftRequest request,
        DateTimeOffset nowUtc)
    {
        TimeSpan cooldown = state.ServerConfiguration.ForcedGiftDeliveryCooldown;
        if (cooldown <= TimeSpan.Zero)
        {
            return null;
        }

        AuthoritativeEvent? latest = state.Ledger.ListByType(ServerEventType.ItemDelivery)
            .Where(evt => !string.Equals(evt.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal))
            .Where(evt => evt.Payload is ItemDeliveryEventPayload payload && payload.IsForcedDelivery)
            .Where(evt => string.Equals(evt.Actor.UserId, request.Actor.UserId, StringComparison.Ordinal)
                && string.Equals(evt.Actor.ColonyId ?? string.Empty, request.Actor.ColonyId ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(evt.Target.UserId, request.Target.UserId, StringComparison.Ordinal)
                && string.Equals(evt.Target.ColonyId ?? string.Empty, request.Target.ColonyId ?? string.Empty, StringComparison.Ordinal))
            .OrderByDescending(evt => evt.CreatedAtUtc)
            .FirstOrDefault();
        if (latest is null)
        {
            return null;
        }

        DateTimeOffset availableAtUtc = latest.CreatedAtUtc + cooldown;
        if (availableAtUtc <= nowUtc)
        {
            return null;
        }

        TimeSpan remaining = availableAtUtc - nowUtc;
        string remainingText = remaining.TotalMinutes >= 1
            ? T("Diplomacy.TimeMinutes", ("VALUE", Math.Ceiling(remaining.TotalMinutes).ToString("0", CultureInfo.InvariantCulture)))
            : T("Diplomacy.TimeSeconds", ("VALUE", Math.Ceiling(remaining.TotalSeconds).ToString("0", CultureInfo.InvariantCulture)));
        return ProtocolResponse.Reject(
            ProtocolErrorCode.ServerRejected,
            T("Gift.ForcedCooldown", ("REMAINING", remainingText)));
    }

    private static DateTimeOffset? CurrentRaidCooldownUntil(
        ClashOfRimNetworkState state,
        string defenderUserId,
        string? defenderColonyId,
        DateTimeOffset nowUtc)
    {
        return string.IsNullOrWhiteSpace(defenderColonyId)
            ? null
            : RaidCooldownRuntimeService.CurrentUntil(
                state,
                defenderUserId,
                defenderColonyId,
                nowUtc);
    }

    private static bool IsForcedGiftDelivery(CreateGiftRequest request)
    {
        return string.Equals(request.DeliveryKind, ItemDeliveryEventPayload.ForcedDeliveryKind, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeGiftDeliveryKind(string? deliveryKind)
    {
        return string.Equals(deliveryKind, ItemDeliveryEventPayload.ForcedDeliveryKind, StringComparison.OrdinalIgnoreCase)
            ? ItemDeliveryEventPayload.ForcedDeliveryKind
            : null;
    }

    private static IResult QuoteTradeOrderFee(QuoteTradeOrderFeeRequest request, ClashOfRimNetworkState state)
    {
        if (request is null
            || request.Owner is null
            || string.IsNullOrWhiteSpace(request.Owner.UserId)
            || string.IsNullOrWhiteSpace(request.Owner.ColonyId)
            || ((request.OfferedThings is null || request.OfferedThings.Count == 0)
                && (request.RequestedThings is null || request.RequestedThings.Count == 0)))
        {
            return Results.Ok(new TradeOrderFeeQuoteResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Trade.OrderMissingFields")),
                requiredFeeSilver: 0));
        }

        if (!state.ServerConfiguration.TradeMarketplaceEnabled)
        {
            return Results.Ok(new TradeOrderFeeQuoteResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Trade.Disabled")),
                requiredFeeSilver: 0));
        }

        if (!TryValidateInlinePawnPackages(request.OfferedThings ?? Array.Empty<ThingReferenceDto>(), out ProtocolResponse? pawnFailure)
            || !TryValidateInlinePawnPackages(request.RequestedThings ?? Array.Empty<ThingReferenceDto>(), out pawnFailure)
            || !TryValidateInlineThingPackages(request.OfferedThings ?? Array.Empty<ThingReferenceDto>(), out pawnFailure)
            || !TryValidateInlineThingPackages(request.RequestedThings ?? Array.Empty<ThingReferenceDto>(), out pawnFailure))
        {
            return Results.Ok(new TradeOrderFeeQuoteResponse(
                pawnFailure ?? ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.ParseFailed")),
                requiredFeeSilver: 0));
        }

        if (ContainsConcreteThingPackage(request.RequestedThings))
        {
            return Results.Ok(new TradeOrderFeeQuoteResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, "trade request requirements cannot carry thing packages"),
                requiredFeeSilver: 0));
        }

        TradeFeeCalculationResult feeCalculation =
            state.AdminBaseline.BuildEffectiveTradeFeePolicy(state.ServerConfiguration)
                .CalculateRequiredFeeResult(
                    request.OfferedThings ?? Array.Empty<ThingReferenceDto>(),
                    request.RequestedThings ?? Array.Empty<ThingReferenceDto>());
        if (!feeCalculation.Accepted)
        {
            return Results.Ok(new TradeOrderFeeQuoteResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ServerRejected,
                    T("Trade.MissingMarketValues", ("DEFS", string.Join(", ", feeCalculation.MissingMarketValueDefs)))),
                requiredFeeSilver: 0,
                feeCalculation.MissingMarketValueDefs));
        }

        return Results.Ok(new TradeOrderFeeQuoteResponse(
            ProtocolResponse.Ok(T("Trade.FeeQuoted")),
            feeCalculation.RequiredFeeSilver,
            feeCalculation.MissingMarketValueDefs));
    }

    private static IResult CreateTradeOrder() => Results.Ok(new EventCreationResponse(
        ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Trade.SnapshotEndpointRequired")),
        eventId: null, ProtocolDeliverySemantics.ServerNotification));

    private static int CountOpenMarketTradeOrdersForOwner(IAuthoritativeEventLedger ledger, ProtocolIdentity owner)
        => ledger.ListByTypeForActor(ServerEventType.Trade, owner.UserId, owner.ColonyId).Count(IsOpenMarketTradeOrderForOwner);

    private static async Task<IResult> CreateTradeOrderWithSnapshot(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        MultipartSnapshotRequest<CreateTradeOrderWithSnapshotRequest>? multipart =
            await ReadMultipartSnapshotRequest<CreateTradeOrderWithSnapshotRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Trade.OrderTransactionMissingPayload")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        CreateTradeOrderWithSnapshotRequest transaction = multipart.Request;
        CreateTradeOrderRequest? request = transaction.TradeOrder;
        if (request is null
            || request.Owner is null
            || string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.Owner.UserId)
            || string.IsNullOrWhiteSpace(request.Owner.ColonyId)
            || ((request.OfferedThings is null || request.OfferedThings.Count == 0)
                && (request.RequestedThings is null || request.RequestedThings.Count == 0)))
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Trade.OrderTransactionMissingFields")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        if (transaction.ConfirmedSnapshot is null
            || string.IsNullOrWhiteSpace(transaction.ConfirmedSnapshot.SnapshotId))
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Trade.OrderTransactionMissingSnapshot")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        if (!MultipartSnapshotAuthentication.MatchesPrincipal(
                httpRequest,
                request.Owner.UserId,
                request.Owner.ColonyId))
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        ApplyTradeOrderExpirations(state, DateTimeOffset.UtcNow);
        using (state.EnterSnapshotMutation())
        {
            if (!ReauthorizeSnapshotPrincipal(httpRequest, state, request.Owner))
                return Results.Ok(new EventCreationResponse(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")), null, ProtocolDeliverySemantics.OnlineImmediate));
            RecoverSnapshotCommits(state);
            AuthoritativeEvent? existingEvent = state.Ledger.FindByIdempotencyKey(request.IdempotencyKey);
            if (existingEvent is not null)
                return TradeOrderReplayResponse(state, request, transaction.ConfirmedSnapshot.SnapshotId!, existingEvent);
        }
        if (!state.ServerConfiguration.TradeMarketplaceEnabled)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Trade.Disabled")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        if (!TryValidateInlinePawnPackages(request.OfferedThings ?? Array.Empty<ThingReferenceDto>(), out ProtocolResponse? pawnFailure)
            || !TryValidateInlinePawnPackages(request.RequestedThings ?? Array.Empty<ThingReferenceDto>(), out pawnFailure)
            || !TryValidateInlineThingPackages(request.OfferedThings ?? Array.Empty<ThingReferenceDto>(), out pawnFailure)
            || !TryValidateInlineThingPackages(request.RequestedThings ?? Array.Empty<ThingReferenceDto>(), out pawnFailure))
        {
            return Results.Ok(new EventCreationResponse(
                pawnFailure ?? ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.ParseFailed")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        TradeFeeCalculationResult feeCalculation =
            state.AdminBaseline.BuildEffectiveTradeFeePolicy(state.ServerConfiguration)
                .CalculateRequiredFeeResult(
                    request.OfferedThings ?? Array.Empty<ThingReferenceDto>(),
                    request.RequestedThings ?? Array.Empty<ThingReferenceDto>());
        if (!feeCalculation.Accepted)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ServerRejected,
                    T("Trade.MissingMarketValues", ("DEFS", string.Join(", ", feeCalculation.MissingMarketValueDefs)))),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        if (request.FeeSilver < feeCalculation.RequiredFeeSilver)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ServerRejected,
                    T(
                        "Trade.InsufficientFee",
                        ("SUBMITTED", request.FeeSilver.ToString(CultureInfo.InvariantCulture)),
                        ("REQUIRED", feeCalculation.RequiredFeeSilver.ToString(CultureInfo.InvariantCulture)))),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        int maxOpenOrders = state.ServerConfiguration.MaxOpenTradeOrdersPerOwner;
        if (maxOpenOrders > 0)
        {
            int openOrderCount = CountOpenMarketTradeOrdersForOwner(state.Ledger, request.Owner);
            if (openOrderCount >= maxOpenOrders)
            {
                return Results.Ok(new EventCreationResponse(
                    ProtocolResponse.Reject(
                        ProtocolErrorCode.ServerRejected,
                        T(
                            "Trade.OpenOrderLimitReached",
                            ("CURRENT", openOrderCount.ToString(CultureInfo.InvariantCulture)),
                            ("MAX", maxOpenOrders.ToString(CultureInfo.InvariantCulture)))),
                    eventId: null,
                    ProtocolDeliverySemantics.OnlineImmediate));
            }
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (TryRejectExpiredPendingConfirmationSnapshot(
                state,
                request.Owner.UserId,
                request.Owner.ColonyId ?? string.Empty,
                nowUtc,
                out ProtocolResponse? pendingRejection))
        {
            return Results.Ok(new EventCreationResponse(
                pendingRejection!,
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        SnapshotUploadResult upload = ReceiveSnapshot(
            state,
            request.Owner.UserId,
            request.Owner.ColonyId ?? string.Empty,
            transaction.ConfirmedSnapshot.SnapshotId!,
            transaction.ConfirmedSnapshot,
            multipart.Payload,
            nowUtc, storeAccepted: false);
        using (state.EnterSnapshotMutation())
        {
            if (!ReauthorizeSnapshotPrincipal(httpRequest, state, request.Owner))
                return Results.Ok(new EventCreationResponse(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")), null, ProtocolDeliverySemantics.OnlineImmediate));
            RecoverSnapshotCommits(state);
            ApplyTradeOrderExpirations(state, DateTimeOffset.UtcNow);
            AuthoritativeEvent? existing = state.Ledger.FindByIdempotencyKey(request.IdempotencyKey);
            if (existing is not null)
                return TradeOrderReplayResponse(state, request, transaction.ConfirmedSnapshot.SnapshotId!, existing);
            if (!upload.Accepted || upload.AcceptedSnapshot is null)
            {
                return Results.Ok(new EventCreationResponse(
                    ToProtocolResponse(upload),
                    eventId: null,
                    ProtocolDeliverySemantics.OnlineImmediate));
            }
            if (maxOpenOrders > 0 && CountOpenMarketTradeOrdersForOwner(state.Ledger, request.Owner) >= maxOpenOrders)
                return Results.Ok(new EventCreationResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict,
                    "Open trade order limit reached during preparation."), null, ProtocolDeliverySemantics.OfflinePending));
            upload = CreateSnapshotReceiver(state).CommitPrepared(
                new SnapshotUploadContext(request.Owner.UserId, request.Owner.ColonyId!, transaction.ConfirmedSnapshot.SnapshotId!, null, upload.SnapshotUploadKind),
                ProtocolDtoMapper.ToSaveSnapshotPackage(transaction.ConfirmedSnapshot, multipart.Payload), upload, storeAccepted: false);
            if (!upload.Accepted || upload.AcceptedSnapshot is null)
                return Results.Ok(new EventCreationResponse(ToProtocolResponse(upload), null, ProtocolDeliverySemantics.OfflinePending));
            string commitId = Guid.NewGuid().ToString("N");
            string acceptedSnapshotId = upload.AcceptedSnapshot.Identity.SnapshotId
                ?? transaction.ConfirmedSnapshot.SnapshotId!;
            if (!TryStoreInlinePawnPackages(
                    state,
                    new ProtocolIdentity(request.Owner.UserId, request.Owner.ColonyId, acceptedSnapshotId),
                    "trade-order-offer:" + commitId,
                    request.OfferedThings ?? Array.Empty<ThingReferenceDto>(),
                    out IReadOnlyList<ThingReferenceDto> offeredThings,
                    out pawnFailure)
                || !TryStoreInlinePawnPackages(
                    state,
                    new ProtocolIdentity(request.Owner.UserId, request.Owner.ColonyId, acceptedSnapshotId),
                    "trade-order-request:" + commitId,
                    request.RequestedThings ?? Array.Empty<ThingReferenceDto>(),
                    out IReadOnlyList<ThingReferenceDto> requestedThings,
                    out pawnFailure))
            {
                return Results.Ok(new EventCreationResponse(
                    pawnFailure ?? ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.ParseFailed")),
                    eventId: null,
                    ProtocolDeliverySemantics.OnlineImmediate));
            }
            TradeDeliveryEndpoint ownerEndpoint = ResolveTradeDeliveryEndpoint(
                state,
                request.Owner.UserId,
                request.Owner.ColonyId,
                acceptedSnapshotId,
                preferredContext: null, upload.AcceptedSnapshot);
            EventTargetContext? targetContext = ownerEndpoint.WorldObjectId is null
                && ownerEndpoint.MapUniqueId is null
                && ownerEndpoint.Tile is null
                    ? null
                    : new EventTargetContext(
                        ownerEndpoint.WorldObjectId,
                        ownerEndpoint.MapUniqueId,
                        ownerEndpoint.Tile,
                        EventLandingMode.DropPod);

            AuthoritativeEvent tradeEvent = AuthoritativeEventFactory.CreateForLedger(state.Ledger,
                ServerEventType.Trade,
                ToEventParty(request.Owner),
                new EventParty("server"),
                request.IdempotencyKey,
                targetOnline: false,
                new TradeEventPayload(
                    request.IdempotencyKey,
                    TradeStage.MarketOrder,
                    offeredThings.Select(thing => ToEventThingReference(thing, acceptedSnapshotId)).ToList(),
                    requestedThings.Select(thing => ToEventThingReference(thing, acceptedSnapshotId)).ToList(),
                    request.FeeSilver,
                    AcceptedByUserId: null,
                    FulfillmentMode: ResolveTradeFulfillmentMode(request),
                    PostagePaidByAcceptor: false),
                nowUtc,
                targetContext);

            tradeEvent = tradeEvent with { EventId = "trade-order:" + commitId };
            CommitTradeSnapshot(state, ProtocolDtoMapper.ToSaveSnapshotPackage(transaction.ConfirmedSnapshot, multipart.Payload), upload,
                new TradeSnapshotMutation(new LedgerBatch(new[] { tradeEvent }, Array.Empty<LedgerStatusChange>()),
                    state.OnlinePresence.ListOnlineUsers().Where(user => user != request.Owner.UserId).ToArray()));
            var append = new LedgerAppendResult(tradeEvent, Created: true);
            EventCreationResponse created = ToEventCreationResponse(
                append,
                ProtocolDeliverySemantics.OfflinePending,
                T("Trade.OrderCreated"));
            return Results.Ok(new EventCreationResponse(
                created.Result,
                created.EventId,
                created.DeliverySemantics,
                acceptedSnapshotId,
                upload.AcceptedSnapshot.Envelope.NextLineageToken));
        }
    }

    private static IResult ListTradeOrders(ListTradeOrdersRequest request, ClashOfRimNetworkState state)
    {
        ApplyTradeOrderExpirations(state, DateTimeOffset.UtcNow);

        if (string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.CurrentSnapshotId))
        {
            return Results.Ok(new ListTradeOrdersResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Trade.MarketRequestMissingFields")),
                Array.Empty<TradeOrderSummaryDto>(),
                state.ServerConfiguration.TradeMarketplaceEnabled));
        }

        IReadOnlyList<AuthoritativeEvent> events = state.Ledger.ListByType(ServerEventType.Trade);
        IReadOnlyDictionary<string, AuthoritativeEvent> viewerMemoByTradeId = BuildLatestActiveViewerTradeMemoByTradeId(
            events,
            request.UserId);
        IReadOnlySet<string> viewerInvolvedTradeIds = BuildViewerInvolvedTradeIds(events, request.UserId);
        string scope = string.IsNullOrWhiteSpace(request.Scope)
            ? "Open"
            : request.Scope.Trim();

        int offset = Math.Max(0, request.Offset);
        int limit = request.Limit <= 0 ? 10 : Math.Clamp(request.Limit, 1, 50);
        List<AuthoritativeEvent> visibleOrderEvents = events
            .Where(ledgerEvent => IsTradeOrderVisibleInScope(
                ledgerEvent,
                request.UserId,
                scope,
                viewerMemoByTradeId.ContainsKey(ledgerEvent.EventId),
                viewerInvolvedTradeIds.Contains(ledgerEvent.EventId)))
            .OrderByDescending(ledgerEvent => ledgerEvent.CreatedAtUtc)
            .ThenBy(ledgerEvent => ledgerEvent.EventId, StringComparer.Ordinal)
            .ToList();

        List<AuthoritativeEvent> pageEvents = visibleOrderEvents
            .Skip(offset)
            .Take(limit)
            .ToList();
        HashSet<string> pageTradeIds = pageEvents
            .Select(ledgerEvent => ledgerEvent.EventId)
            .ToHashSet(StringComparer.Ordinal);
        IReadOnlyDictionary<string, int> acceptedMemoCountByTradeId = BuildActiveMemoCountByTradeId(events, pageTradeIds);
        IReadOnlyDictionary<string, ProtocolIdentity> counterpartyByTradeId = BuildTradeCounterpartyByTradeId(events, pageTradeIds);
        List<TradeOrderSummaryDto> page = pageEvents
            .Select(ledgerEvent => ToTradeOrderSummary(
                ledgerEvent,
                acceptedMemoCountByTradeId.TryGetValue(ledgerEvent.EventId, out int memoCount) ? memoCount : 0,
                state,
                request.UserId,
                request.ColonyId,
                request.CurrentSnapshotId,
                viewerMemoByTradeId.TryGetValue(ledgerEvent.EventId, out AuthoritativeEvent? viewerMemo)
                    ? viewerMemo
                    : null,
                counterpartyByTradeId.TryGetValue(ledgerEvent.EventId, out ProtocolIdentity? counterparty)
                    ? counterparty
                    : null))
            .ToList();

        return Results.Ok(new ListTradeOrdersResponse(
            ProtocolResponse.Ok(ServerLocalization.Text("Trade.MarketReturned")),
            page,
            state.ServerConfiguration.TradeMarketplaceEnabled,
            visibleOrderEvents.Count,
            offset,
            limit,
            offset + page.Count < visibleOrderEvents.Count));
    }

    private static IReadOnlyDictionary<string, AuthoritativeEvent> BuildLatestActiveViewerTradeMemoByTradeId(
        IReadOnlyList<AuthoritativeEvent> events,
        string viewerUserId)
    {
        var result = new Dictionary<string, AuthoritativeEvent>(StringComparer.Ordinal);
        foreach (AuthoritativeEvent ledgerEvent in events)
        {
            if (!IsTradeAcceptanceMemoForViewer(ledgerEvent, viewerUserId, includeTerminal: false)
                || ledgerEvent.Payload is not TradeEventPayload payload)
            {
                continue;
            }

            if (!result.TryGetValue(payload.TradeId, out AuthoritativeEvent? current)
                || ledgerEvent.CreatedAtUtc > current.CreatedAtUtc)
            {
                result[payload.TradeId] = ledgerEvent;
            }
        }

        return result;
    }

    private static IReadOnlySet<string> BuildViewerInvolvedTradeIds(
        IReadOnlyList<AuthoritativeEvent> events,
        string viewerUserId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(viewerUserId))
        {
            return result;
        }

        foreach (AuthoritativeEvent ledgerEvent in events)
        {
            if (ledgerEvent.Type != ServerEventType.Trade
                || ledgerEvent.Payload is not TradeEventPayload payload
                || string.IsNullOrWhiteSpace(payload.TradeId))
            {
                continue;
            }

            if ((payload.Stage == TradeStage.AcceptedMemo
                    && string.Equals(payload.AcceptedByUserId, viewerUserId, StringComparison.Ordinal)
                    && ledgerEvent.Status is not ServerEventStatus.Failed
                        and not ServerEventStatus.RejectedByTarget
                        and not ServerEventStatus.Conflict)
                || (payload.Stage is TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange
                    && (string.Equals(payload.AcceptedByUserId, viewerUserId, StringComparison.Ordinal)
                        || string.Equals(ledgerEvent.Actor.UserId, viewerUserId, StringComparison.Ordinal))))
            {
                result.Add(payload.TradeId);
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, int> BuildActiveMemoCountByTradeId(
        IReadOnlyList<AuthoritativeEvent> events,
        IReadOnlySet<string> tradeIds)
    {
        var result = new Dictionary<string, int>(tradeIds.Count, StringComparer.Ordinal);
        if (tradeIds.Count == 0)
        {
            return result;
        }

        foreach (AuthoritativeEvent ledgerEvent in events)
        {
            if (!IsActiveTradeAcceptanceMemo(ledgerEvent)
                || ledgerEvent.Payload is not TradeEventPayload payload
                || !tradeIds.Contains(payload.TradeId))
            {
                continue;
            }

            result[payload.TradeId] = result.TryGetValue(payload.TradeId, out int count)
                ? count + 1
                : 1;
        }

        return result;
    }

    private static IResult FulfillTradeOrder() => Results.Ok(new FulfillTradeOrderResponse(
        ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Trade.SnapshotEndpointRequired")),
        tradeEventId: null, acceptedMemoEventId: null, exchangeEventId: null, exchangeCreated: false,
        Array.Empty<ThingReferenceDto>(), Array.Empty<string>(), tradeStatus: string.Empty));

    private static async Task<IResult> FulfillTradeOrderWithSnapshot(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        MultipartSnapshotRequest<FulfillTradeOrderWithSnapshotRequest>? multipart =
            await ReadMultipartSnapshotRequest<FulfillTradeOrderWithSnapshotRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
        {
            return Results.Ok(new FulfillTradeOrderResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Trade.FulfillmentTransactionMissingPayload")),
                tradeEventId: null,
                acceptedMemoEventId: null,
                exchangeEventId: null,
                exchangeCreated: false,
                Array.Empty<ThingReferenceDto>(),
                Array.Empty<string>(),
                tradeStatus: string.Empty));
        }

        FulfillTradeOrderWithSnapshotRequest transaction = multipart.Request;
        FulfillTradeOrderRequest? request = transaction.Fulfillment;
        if (request is null
            || transaction.ConfirmedSnapshot is null
            || string.IsNullOrWhiteSpace(transaction.ConfirmedSnapshot.SnapshotId))
        {
            return Results.Ok(new FulfillTradeOrderResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Trade.FulfillmentTransactionMissingFields")),
                request?.TradeEventId,
                request?.AcceptedMemoEventId,
                exchangeEventId: null,
                exchangeCreated: false,
                Array.Empty<ThingReferenceDto>(),
                Array.Empty<string>(),
                tradeStatus: string.Empty));
        }

        ApplyTradeOrderExpirations(state, DateTimeOffset.UtcNow);
        ProtocolResponse? validation = ValidateTradeFulfillmentRequest(request);
        if (validation is not null)
        {
            return Results.Ok(new FulfillTradeOrderResponse(
                validation,
                request.TradeEventId,
                request.AcceptedMemoEventId,
                exchangeEventId: null,
                exchangeCreated: false,
                Array.Empty<ThingReferenceDto>(),
                Array.Empty<string>(),
                tradeStatus: string.Empty));
        }

        if (!MultipartSnapshotAuthentication.MatchesPrincipal(
                httpRequest,
                request.Acceptor.UserId,
                request.Acceptor.ColonyId))
        {
            return Results.Ok(new FulfillTradeOrderResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")),
                request.TradeEventId,
                request.AcceptedMemoEventId,
                exchangeEventId: null,
                exchangeCreated: false,
                Array.Empty<ThingReferenceDto>(),
                Array.Empty<string>(),
                tradeStatus: string.Empty));
        }

        TradeFulfillmentMode fulfillmentMode = ResolveRequestedTradeFulfillmentMode(request.FulfillmentMode);
        if (fulfillmentMode is not TradeFulfillmentMode.SelfDelivery and not TradeFulfillmentMode.ServerDropPod)
        {
            return Results.Ok(new FulfillTradeOrderResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Trade.InvalidFulfillmentMode")),
                request.TradeEventId,
                request.AcceptedMemoEventId,
                exchangeEventId: null,
                exchangeCreated: false,
                Array.Empty<ThingReferenceDto>(),
                Array.Empty<string>(),
                tradeStatus: string.Empty));
        }

        using (state.EnterSnapshotMutation())
        {
            if (!ReauthorizeSnapshotPrincipal(httpRequest, state, request.Acceptor))
                return TradeFulfillmentFailure(request, ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")));
            RecoverSnapshotCommits(state);
            AuthoritativeEvent? existing = state.Ledger.FindByIdempotencyKey(request.IdempotencyKey);
            if (existing is not null)
                return TradeFulfillmentReplayResponse(state, request, transaction.ConfirmedSnapshot.SnapshotId!, existing);
        }
        AuthoritativeEvent? tradeOrder = state.Ledger.Find(request.TradeEventId);
        AuthoritativeEvent? acceptedMemo = string.IsNullOrWhiteSpace(request.AcceptedMemoEventId)
            ? null
            : state.Ledger.Find(request.AcceptedMemoEventId);
        if (tradeOrder is null
            || !IsOpenMarketTradeOrder(tradeOrder, request.Acceptor.UserId)
            || tradeOrder.Payload is not TradeEventPayload orderPayload
            || (fulfillmentMode == TradeFulfillmentMode.SelfDelivery
                && (acceptedMemo is null
                    || !IsMatchingActiveTradeAcceptanceMemo(acceptedMemo, tradeOrder.EventId, request.Acceptor.UserId, request.Acceptor.ColonyId))))
        {
            return Results.Ok(new FulfillTradeOrderResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Trade.OrderOrMemoUnavailable")),
                request.TradeEventId,
                request.AcceptedMemoEventId,
                exchangeEventId: null,
                exchangeCreated: false,
                Array.Empty<ThingReferenceDto>(),
                Array.Empty<string>(),
                tradeOrder?.Status.ToString() ?? string.Empty));
        }

        IReadOnlyList<ThingReferenceDto> requiredThings = orderPayload.RequestedItems.Select(ToThingReferenceDto).ToList();
        if (!TryValidateInlinePawnPackages(request.DeliveredThings, out ProtocolResponse? pawnFailure)
            || !TryValidateInlineThingPackages(request.DeliveredThings, out pawnFailure))
        {
            return Results.Ok(new FulfillTradeOrderResponse(
                pawnFailure ?? ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.ParseFailed")),
                tradeOrder.EventId,
                acceptedMemo?.EventId,
                exchangeEventId: null,
                exchangeCreated: false,
                orderPayload.OfferedItems.Select(ToThingReferenceDto).ToList(),
                Array.Empty<string>(),
                tradeOrder.Status.ToString()));
        }

        if (!TradeThingRequirementMatcher.Satisfies(
                requiredThings,
                request.DeliveredThings,
                out IReadOnlyList<string> missingRequirements,
                state.Plugins.ActiveTradeThingMetadataMatchers(state.CompatibilityBaseline.Current)))
        {
            return Results.Ok(new FulfillTradeOrderResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    T("Trade.MissingRequirements", ("MISSING", string.Join("; ", missingRequirements)))),
                tradeOrder.EventId,
                acceptedMemo?.EventId,
                exchangeEventId: null,
                exchangeCreated: false,
                orderPayload.OfferedItems.Select(ToThingReferenceDto).ToList(),
                missingRequirements,
                tradeOrder.Status.ToString()));
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (TryRejectExpiredPendingConfirmationSnapshot(
                state,
                request.Acceptor.UserId,
                request.Acceptor.ColonyId ?? string.Empty,
                nowUtc,
                out ProtocolResponse? pendingRejection))
        {
            return Results.Ok(new FulfillTradeOrderResponse(
                pendingRejection!,
                tradeOrder.EventId,
                acceptedMemo?.EventId,
                exchangeEventId: null,
                exchangeCreated: false,
                orderPayload.OfferedItems.Select(ToThingReferenceDto).ToList(),
                Array.Empty<string>(),
                tradeOrder.Status.ToString()));
        }

        SnapshotUploadResult upload = ReceiveSnapshot(
            state,
            request.Acceptor.UserId,
            request.Acceptor.ColonyId ?? string.Empty,
            transaction.ConfirmedSnapshot.SnapshotId!,
            transaction.ConfirmedSnapshot,
            multipart.Payload,
            nowUtc, storeAccepted: false);
        using (state.EnterSnapshotMutation())
        {
            if (!ReauthorizeSnapshotPrincipal(httpRequest, state, request.Acceptor))
                return TradeFulfillmentFailure(request, ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")));
            RecoverSnapshotCommits(state);
            ApplyTradeOrderExpirations(state, DateTimeOffset.UtcNow);
            AuthoritativeEvent? existing = state.Ledger.FindByIdempotencyKey(request.IdempotencyKey);
            if (existing is not null)
                return TradeFulfillmentReplayResponse(state, request, transaction.ConfirmedSnapshot.SnapshotId!, existing);
            if (!upload.Accepted || upload.AcceptedSnapshot is null)
            {
                return Results.Ok(new FulfillTradeOrderResponse(
                    ToProtocolResponse(upload),
                    tradeOrder.EventId,
                    acceptedMemo?.EventId,
                    exchangeEventId: null,
                    exchangeCreated: false,
                    orderPayload.OfferedItems.Select(ToThingReferenceDto).ToList(),
                    Array.Empty<string>(),
                    tradeOrder.Status.ToString()));
            }
            tradeOrder = state.Ledger.Find(request.TradeEventId);
            acceptedMemo = string.IsNullOrWhiteSpace(request.AcceptedMemoEventId) ? null : state.Ledger.Find(request.AcceptedMemoEventId);
            if (tradeOrder is null || !IsOpenMarketTradeOrder(tradeOrder, request.Acceptor.UserId)
                || tradeOrder.Payload is not TradeEventPayload currentPayload
                || (fulfillmentMode == TradeFulfillmentMode.SelfDelivery && acceptedMemo is null)
                || (TradeFulfillmentMemoId(request) is not null && (acceptedMemo is null
                    || !IsMatchingActiveTradeAcceptanceMemo(acceptedMemo, tradeOrder.EventId, request.Acceptor.UserId, request.Acceptor.ColonyId))))
                return TradeFulfillmentFailure(request, ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Trade.OrderOrMemoUnavailable")));
            orderPayload = currentPayload;
            upload = CreateSnapshotReceiver(state).CommitPrepared(
                new SnapshotUploadContext(request.Acceptor.UserId, request.Acceptor.ColonyId!, transaction.ConfirmedSnapshot.SnapshotId!, null, upload.SnapshotUploadKind),
                ProtocolDtoMapper.ToSaveSnapshotPackage(transaction.ConfirmedSnapshot, multipart.Payload), upload, storeAccepted: false);
            if (!upload.Accepted || upload.AcceptedSnapshot is null)
                return TradeFulfillmentFailure(request, ToProtocolResponse(upload));
            string commitId = Guid.NewGuid().ToString("N");
            string acceptedSnapshotId = upload.AcceptedSnapshot.Identity.SnapshotId
                ?? transaction.ConfirmedSnapshot.SnapshotId!;
            if (!TryStoreInlinePawnPackages(
                    state,
                    new ProtocolIdentity(request.Acceptor.UserId, request.Acceptor.ColonyId, acceptedSnapshotId),
                    "trade-fulfill-delivered:" + commitId,
                    request.DeliveredThings,
                    out IReadOnlyList<ThingReferenceDto> deliveredThings,
                    out pawnFailure))
            {
                return Results.Ok(new FulfillTradeOrderResponse(
                    pawnFailure ?? ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.ParseFailed")),
                    tradeOrder.EventId,
                    acceptedMemo?.EventId,
                    exchangeEventId: null,
                    exchangeCreated: false,
                    orderPayload.OfferedItems.Select(ToThingReferenceDto).ToList(),
                    Array.Empty<string>(),
                    tradeOrder.Status.ToString()));
            }
            var acceptorParty = ToEventParty(request.Acceptor);
            TradeStage exchangeStage = fulfillmentMode == TradeFulfillmentMode.ServerDropPod
                ? TradeStage.ServerDropPodExchange
                : TradeStage.SelfDeliveryExchange;
            AuthoritativeEvent exchangeEvent = AuthoritativeEventFactory.CreateForLedger(state.Ledger,
                ServerEventType.Trade,
                acceptorParty,
                tradeOrder.Actor,
                request.IdempotencyKey,
                targetOnline: state.OnlinePresence.IsUserOnline(tradeOrder.Actor.UserId),
                new TradeEventPayload(
                    tradeOrder.EventId,
                    exchangeStage,
                    orderPayload.OfferedItems,
                    deliveredThings.Select(thing => ToEventThingReference(thing, acceptedSnapshotId)).ToList(),
                    orderPayload.FeeSilver,
                    AcceptedByUserId: request.Acceptor.UserId,
                    AcceptedMemoEventId: acceptedMemo?.EventId,
                    FulfillmentMode: fulfillmentMode,
                    PostagePaidByAcceptor: fulfillmentMode == TradeFulfillmentMode.ServerDropPod),
                nowUtc,
                tradeOrder.TargetContext);

            exchangeEvent = exchangeEvent with
            {
                EventId = "trade-exchange:" + commitId,
                Status = ServerEventStatus.AppliedToSnapshot,
                DeliveredToSnapshotId = request.Acceptor.SnapshotId,
                DeliveredAtUtc = nowUtc,
                AppliedSnapshotId = acceptedSnapshotId,
                AppliedAtUtc = nowUtc,
                LastApplicationResult = EventApplicationResultKind.Applied
            };
            IReadOnlyList<AuthoritativeEvent> activeMemos = FindActiveTradeAcceptanceMemos(state.Ledger, tradeOrder.EventId);
            var changes = activeMemos.Select(memo => new LedgerStatusChange(memo.EventId, memo.Status,
                memo.EventId == acceptedMemo?.EventId ? ServerEventStatus.AppliedToSnapshot : ServerEventStatus.Cancelled)).ToList();
            changes.Insert(0, new LedgerStatusChange(tradeOrder.EventId, tradeOrder.Status, ServerEventStatus.AppliedToSnapshot));
            string fulfillmentKey = acceptedMemo?.EventId ?? request.Acceptor.UserId + ":" + request.Acceptor.ColonyId;
            AuthoritativeEvent ownerDelivery = CreateTradeItemDeliveryEvent(state,
                $"trade-completed-owner-delivery:{tradeOrder.EventId}:{fulfillmentKey}", acceptorParty, tradeOrder.Actor,
                deliveredThings.Select(thing => ToEventThingReference(thing, acceptedSnapshotId)).ToList(),
                ItemDeliveryPurpose.TradeCompletedOwnerDelivery, ResolveTradeCompletedOwnerDeliveryTargetContext(tradeOrder, fulfillmentMode), nowUtc);
            var events = new List<AuthoritativeEvent> { exchangeEvent, ownerDelivery };
            AuthoritativeEvent? acceptorDelivery = null;
            if (fulfillmentMode == TradeFulfillmentMode.ServerDropPod)
            {
                acceptorDelivery = CreateTradeItemDeliveryEvent(state,
                    $"trade-completed-acceptor-delivery:{tradeOrder.EventId}:{fulfillmentKey}", tradeOrder.Actor, acceptorParty,
                    orderPayload.OfferedItems, ItemDeliveryPurpose.TradeCompletedAcceptorDelivery,
                    ResolveTradeTargetContext(state, request.Acceptor.UserId, request.Acceptor.ColonyId!, acceptedSnapshotId,
                        EventLandingMode.DropPod, upload.AcceptedSnapshot), nowUtc);
                events.Add(acceptorDelivery);
            }
            CommitTradeSnapshot(state, ProtocolDtoMapper.ToSaveSnapshotPackage(transaction.ConfirmedSnapshot, multipart.Payload), upload,
                new TradeSnapshotMutation(new LedgerBatch(events, changes), activeMemos.Select(memo => memo.Target.UserId)
                    .Append(tradeOrder.Actor.UserId).Append(request.Acceptor.UserId).Distinct(StringComparer.Ordinal).ToArray()));
            return Results.Ok(new FulfillTradeOrderResponse(
                ProtocolResponse.Ok(T("Trade.FulfillmentTransactionCreated")), tradeOrder.EventId, acceptedMemo?.EventId,
                exchangeEvent.EventId, true, orderPayload.OfferedItems.Select(ToThingReferenceDto).ToList(), Array.Empty<string>(),
                ServerEventStatus.AppliedToSnapshot.ToString(), acceptorDelivery?.EventId, ownerDelivery.EventId,
                acceptedSnapshotId, upload.AcceptedSnapshot.Envelope.NextLineageToken));
        }
    }
    private static EventTargetContext? ResolveTradeCompletedOwnerDeliveryTargetContext(
        AuthoritativeEvent tradeOrder,
        TradeFulfillmentMode fulfillmentMode)
    {
        if (tradeOrder.TargetContext is null)
        {
            return null;
        }

        EventLandingMode landingMode = fulfillmentMode == TradeFulfillmentMode.ServerDropPod
            ? EventLandingMode.DropPod
            : EventLandingMode.MapEdge;
        return tradeOrder.TargetContext with { LandingMode = landingMode };
    }

    private static IReadOnlyList<string> FindUnavailableTradeThingDefs(
        IEnumerable<EventThingReference> things,
        AdminBaselineSnapshot? baseline,
        ISet<string> validThingDefs)
    {
        var unavailable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (EventThingReference thing in things.Where(ShouldValidateTradeThingDef))
        {
            string? effectiveDef = string.IsNullOrWhiteSpace(thing.MinifiedInnerDefName)
                ? thing.Def
                : thing.MinifiedInnerDefName;
            if (!string.IsNullOrWhiteSpace(effectiveDef) && !validThingDefs.Contains(effectiveDef!))
            {
                unavailable.Add(effectiveDef!);
            }

            if (!string.IsNullOrWhiteSpace(thing.MinifiedInnerDefName)
                && baseline is not null
                && !baseline.IsApprovedPackableBuilding(thing.MinifiedInnerDefName))
            {
                unavailable.Add(thing.MinifiedInnerDefName!);
            }
        }

        return unavailable
            .OrderBy(defName => defName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ContainsConcreteThingPackage(IReadOnlyList<ThingReferenceDto>? things)
    {
        return things is not null
            && things.Any(thing => thing is not null
                && (thing.ThingPackage is not null || !string.IsNullOrWhiteSpace(thing.ThingPackageId)));
    }

    private static HashSet<string> BuildValidTradeThingDefSet(
        AdminBaselineSnapshot? baseline,
        ClashOfRimServerConfiguration configuration)
    {
        var validThingDefs = new HashSet<string>(
            baseline?.StandardMarketValuePerThing.Keys ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (string defName in configuration.TradeFeePolicy.FixedFeePerThing.Keys)
        {
            if (!string.IsNullOrWhiteSpace(defName))
            {
                validThingDefs.Add(defName);
            }
        }

        if (baseline is not null)
        {
            foreach (PackableBuildingDto building in baseline.PackableBuildings)
            {
                if (!string.IsNullOrWhiteSpace(building.DefName))
                {
                    validThingDefs.Add(building.DefName);
                }
            }
        }

        return validThingDefs;
    }

    private static IReadOnlyList<string> FindUnavailablePackableBuildingDefs(
        IEnumerable<EventThingReference> things,
        AdminBaselineSnapshot baseline)
    {
        return things
            .Select(thing => thing.MinifiedInnerDefName)
            .Where(defName => !string.IsNullOrWhiteSpace(defName)
                && !baseline.IsApprovedPackableBuilding(defName))
            .Select(defName => defName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(defName => defName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ShouldValidateTradeThingDef(EventThingReference thing)
    {
        return !string.IsNullOrWhiteSpace(thing.Def)
            && thing.PawnPackage is null
            && string.IsNullOrWhiteSpace(thing.PawnPackageId);
    }

    private static IResult CancelTradeOrder(CloseTradeOrderRequest request, ClashOfRimNetworkState state)
    {
        using (state.EnterSnapshotMutation())
        {
            string failure = T("Auth.IdentityDenied");
            if (request.Owner is null || !IsAuthorizedForColony(state, request.AuthToken, request.Owner.UserId,
                    request.Owner.ColonyId ?? string.Empty, null, null, DateTimeOffset.UtcNow, out failure))
                return Results.Ok(new CloseTradeOrderResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, failure), request.TradeEventId, string.Empty, 0));
            RecoverSnapshotCommits(state);
            ApplyTradeOrderExpirations(state, DateTimeOffset.UtcNow);

            ProtocolResponse? validation = ValidateTradeCloseRequest(request);
            if (validation is not null)
            {
                return Results.Ok(new CloseTradeOrderResponse(
                    validation,
                    request.TradeEventId,
                    string.Empty,
                    notifiedAcceptorCount: 0));
            }

            AuthoritativeEvent? tradeOrder = state.Ledger.Find(request.TradeEventId);
            var cancellation = state.Ledger.FindByIdempotencyKey(request.IdempotencyKey);
            if (cancellation?.Payload is TradeEventPayload { Stage: TradeStage.AcceptanceCancelled })
                return ReplayTradeMemoCancellation(request, state, tradeOrder, cancellation, duplicate: true);
            if (tradeOrder is not null
                && tradeOrder.Type == ServerEventType.Trade
                && tradeOrder.Payload is TradeEventPayload { Stage: TradeStage.MarketOrder }
                && (!string.Equals(tradeOrder.Actor.UserId, request.Owner.UserId, StringComparison.Ordinal)
                    || !string.Equals(tradeOrder.Actor.ColonyId, request.Owner.ColonyId, StringComparison.Ordinal)))
            {
                return CancelTradeAcceptanceMemo(request, state, tradeOrder);
            }

            return CancelOwnedTradeOrder(request, state);
        }
    }

    private static IResult CancelOwnedTradeOrder(CloseTradeOrderRequest request, ClashOfRimNetworkState state)
    {
        using (state.EnterSnapshotMutation())
        {
            string failure = T("Auth.IdentityDenied");
            if (request.Owner is null || !IsAuthorizedForColony(state, request.AuthToken, request.Owner.UserId,
                    request.Owner.ColonyId ?? string.Empty, null, null, DateTimeOffset.UtcNow, out failure))
                return Results.Ok(new CloseTradeOrderResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, failure), request.TradeEventId, string.Empty, 0));
            RecoverSnapshotCommits(state);
            ApplyTradeOrderExpirations(state, DateTimeOffset.UtcNow);

            ProtocolResponse? validation = ValidateTradeCloseRequest(request);
            if (validation is not null)
            {
                return Results.Ok(new CloseTradeOrderResponse(
                    validation,
                    request.TradeEventId,
                    string.Empty,
                    notifiedAcceptorCount: 0));
            }

            AuthoritativeEvent? tradeOrder = state.Ledger.Find(request.TradeEventId);
            if (tradeOrder is null
                || tradeOrder.Type != ServerEventType.Trade
                || tradeOrder.Payload is not TradeEventPayload payload
                || payload.Stage != TradeStage.MarketOrder
                || !string.Equals(tradeOrder.Actor.UserId, request.Owner.UserId, StringComparison.Ordinal)
                || !string.Equals(tradeOrder.Actor.ColonyId, request.Owner.ColonyId, StringComparison.Ordinal))
            {
                return Results.Ok(new CloseTradeOrderResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Trade.CloseNotOwner")),
                    request.TradeEventId,
                    string.Empty,
                    notifiedAcceptorCount: 0));
            }

            bool duplicate = tradeOrder.Status == ServerEventStatus.Cancelled;
            if (!duplicate && !IsOpenMarketTradeOrderForOwner(tradeOrder))
                return Results.Ok(new CloseTradeOrderResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict,
                    T("Trade.NotOpen")), tradeOrder.EventId, tradeOrder.Status.ToString(), 0));
            IResult Conflict() => Results.Ok(new CloseTradeOrderResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict,
                "Trade cancellation conflicts with its memos, fulfillment or escrow return."), tradeOrder.EventId, tradeOrder.Status.ToString(), 0));
            if (!TryPrepareOwnedTradeCancellation(state, tradeOrder, out var activeMemos, out var batch)) return Conflict();
            try { if (batch is not null) state.Ledger.ApplyBatch(batch); }
            catch (InvalidOperationException) { return Conflict(); }
            IReadOnlyList<string> notifiedUsers = activeMemos
                .Select(memo => memo.Target.UserId)
                .Where(userId => !string.IsNullOrWhiteSpace(userId))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            // Cancellation creates an automatic item-return event for the owner too.
            // Without this signal a connected seller waits for an unrelated event or reconnect.
            state.EventNotifications.SignalUsers(notifiedUsers.Append(tradeOrder.Actor.UserId));

            return Results.Ok(new CloseTradeOrderResponse(
                new ProtocolResponse(true, duplicate ? ProtocolErrorCode.DuplicateRequest : ProtocolErrorCode.None, T("Trade.CancelledWithNotifications")),
                tradeOrder.EventId,
                ServerEventStatus.Cancelled.ToString(),
                notifiedUsers.Count));
        }
    }
}
