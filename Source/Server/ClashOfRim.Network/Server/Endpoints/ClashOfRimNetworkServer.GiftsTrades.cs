using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using System.Globalization;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult CreateGift(CreateGiftRequest request, ClashOfRimNetworkState state)
    {
        ProtocolResponse? validation = ValidateGiftCreationRequest(request);
        if (validation is not null)
        {
            return Results.Ok(new EventCreationResponse(
                validation,
                eventId: null,
                ProtocolDeliverySemantics.OfflinePending));
        }

        if (!state.ServerConfiguration.GiftsEnabled)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Gift.Disabled")),
                eventId: null,
                ProtocolDeliverySemantics.ServerNotification));
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        AuthoritativeEvent? existingEvent = FindEventByIdempotencyKey(state.Ledger, request.IdempotencyKey);
        if (existingEvent is not null)
        {
            return Results.Ok(new EventCreationResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Gift.TransactionDuplicate")),
                existingEvent.EventId,
                existingEvent.Status == ServerEventStatus.ReadyForImmediateDelivery
                    ? ProtocolDeliverySemantics.OnlineImmediate
                    : ProtocolDeliverySemantics.OfflinePending));
        }

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

        bool targetOnline = state.OnlinePresence.IsUserOnline(request.Target.UserId);
        if (!TryStoreInlinePawnPackages(
                state,
                request.Actor,
                "gift:" + request.IdempotencyKey,
                request.Things,
                out IReadOnlyList<ThingReferenceDto> giftThings,
                out ProtocolResponse? packageFailure))
        {
            return Results.Ok(new EventCreationResponse(
                packageFailure ?? ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.ParseFailed")),
                eventId: null,
                ProtocolDeliverySemantics.OfflinePending));
        }

        AuthoritativeEvent giftEvent = AuthoritativeEventFactory.Create(
            ServerEventType.Gift,
            ToEventParty(request.Actor),
            ToEventParty(request.Target),
            request.IdempotencyKey,
            targetOnline,
            new GiftEventPayload(
                giftThings.Select(thing => ToEventThingReference(thing, request.Actor.SnapshotId)).ToList(),
                request.Message,
                NormalizeGiftDeliveryKind(request.DeliveryKind)),
            nowUtc,
            targetContext);

        LedgerAppendResult append = state.Ledger.Append(giftEvent);
        SignalIfCreated(state, append, giftEvent.Target.UserId);
        return Results.Ok(ToEventCreationResponse(
            append,
            targetOnline ? ProtocolDeliverySemantics.OnlineImmediate : ProtocolDeliverySemantics.OfflinePending,
            T("Gift.Created")));
    }

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

        AuthoritativeEvent? existingEvent = FindEventByIdempotencyKey(state.Ledger, request.IdempotencyKey);
        if (existingEvent is not null)
        {
            string? sourceSnapshotId = existingEvent.Payload is GiftEventPayload existingPayload
                ? existingPayload.Items.FirstOrDefault()?.SourceSnapshotId
                : null;
            string? nextLineageToken = FindSnapshotNextLineageToken(
                state,
                request.Actor.UserId,
                request.Actor.ColonyId ?? string.Empty,
                sourceSnapshotId);
            return Results.Ok(new EventCreationResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Gift.TransactionDuplicate")),
                existingEvent.EventId,
                existingEvent.Status == ServerEventStatus.ReadyForImmediateDelivery
                    ? ProtocolDeliverySemantics.OnlineImmediate
                    : ProtocolDeliverySemantics.OfflinePending,
                sourceSnapshotId,
                nextLineageToken));
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
            nowUtc);
        if (!upload.Accepted || upload.AcceptedSnapshot is null)
        {
            return Results.Ok(new EventCreationResponse(
                ToProtocolResponse(upload),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        string acceptedSnapshotId = upload.AcceptedSnapshot.Identity.SnapshotId
            ?? transaction.ConfirmedSnapshot.SnapshotId!;
        bool targetOnline = state.OnlinePresence.IsUserOnline(request.Target.UserId);
        var acceptedActor = new ProtocolIdentity(request.Actor.UserId, request.Actor.ColonyId, acceptedSnapshotId);
        if (!TryStoreInlinePawnPackages(
                state,
                acceptedActor,
                "gift:" + request.IdempotencyKey,
                request.Things,
                out IReadOnlyList<ThingReferenceDto> giftThings,
                out ProtocolResponse? packageFailure))
        {
            return Results.Ok(new EventCreationResponse(
                packageFailure ?? ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.ParseFailed")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        AuthoritativeEvent giftEvent = AuthoritativeEventFactory.Create(
            ServerEventType.Gift,
            ToEventParty(acceptedActor),
            ToEventParty(request.Target),
            request.IdempotencyKey,
            targetOnline,
            new GiftEventPayload(
                giftThings.Select(thing => ToEventThingReference(thing, acceptedSnapshotId)).ToList(),
                request.Message,
                NormalizeGiftDeliveryKind(request.DeliveryKind)),
            nowUtc,
            targetContext);

        LedgerAppendResult append = state.Ledger.Append(giftEvent);
        SignalIfCreated(state, append, giftEvent.Target.UserId);
        RunSnapshotPostUploadProcessors(
            state,
            request.Actor.UserId,
            request.Actor.ColonyId ?? string.Empty,
            sessionId: null,
            upload,
            nowUtc);

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
            && !string.Equals(request.DeliveryKind, GiftEventPayload.ForcedDeliveryKind, StringComparison.OrdinalIgnoreCase))
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

        AuthoritativeEvent? latest = state.Ledger.ListByType(ServerEventType.Gift)
            .Where(evt => !string.Equals(evt.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal))
            .Where(evt => evt.Payload is GiftEventPayload payload && payload.IsForcedDelivery)
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
        RaidCooldownStatus cooldown = RaidCooldownProjector.BuildForDefender(
            defenderUserId,
            defenderColonyId,
            state.Ledger.ListByTypeForTarget(ServerEventType.Raid, defenderUserId, defenderColonyId),
            policy: BuildRaidCooldownPolicy(state.ServerConfiguration),
            defenderProtectionStartResolver: raid => ResolveRaidProtectionActivatedAt(state, raid),
            requireDefenderProtectionActivation: true);
        return cooldown.Records
            .Where(record => record.CooldownUntilUtc > nowUtc)
            .OrderByDescending(record => record.CooldownUntilUtc)
            .Select(record => (DateTimeOffset?)record.CooldownUntilUtc)
            .FirstOrDefault();
    }

    private static bool IsForcedGiftDelivery(CreateGiftRequest request)
    {
        return string.Equals(request.DeliveryKind, GiftEventPayload.ForcedDeliveryKind, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeGiftDeliveryKind(string? deliveryKind)
    {
        return string.Equals(deliveryKind, GiftEventPayload.ForcedDeliveryKind, StringComparison.OrdinalIgnoreCase)
            ? GiftEventPayload.ForcedDeliveryKind
            : null;
    }

    private static IResult RejectGift(RejectGiftRequest request, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.EventId)
            || string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.CurrentSnapshotId))
        {
            return Results.Ok(new RejectGiftResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Gift.RejectMissingFields")),
                request.EventId,
                returnEventId: null,
                returnEventCreated: false));
        }

        AuthoritativeEvent? giftEvent = state.Ledger.Find(request.EventId);
        if (giftEvent is null || !IsVisibleParty(giftEvent.Target, request.UserId, request.ColonyId))
        {
            return Results.Ok(new RejectGiftResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Gift.NotFoundOrInvisible")),
                request.EventId,
                returnEventId: null,
                returnEventCreated: false));
        }

        if (giftEvent.Type != ServerEventType.Gift || giftEvent.Payload is not GiftEventPayload)
        {
            return Results.Ok(new RejectGiftResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Gift.RejectGiftOnly")),
                request.EventId,
                returnEventId: null,
                returnEventCreated: false));
        }

        if (giftEvent.RejectionPolicy != EventRejectionPolicy.RejectableByTarget)
        {
            return Results.Ok(new RejectGiftResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Gift.NotRejectable")),
                request.EventId,
                returnEventId: null,
                returnEventCreated: false));
        }

        if (giftEvent.Status == ServerEventStatus.AppliedToSnapshot)
        {
            return Results.Ok(new RejectGiftResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Gift.AlreadyAppliedCannotReject")),
                request.EventId,
                returnEventId: null,
                returnEventCreated: false));
        }

        if (giftEvent.Status == ServerEventStatus.RejectedByTarget)
        {
            AuthoritativeEvent? existingReturn = FindGiftReturnFor(state.Ledger, giftEvent);
            if (existingReturn is not null)
            {
                return Results.Ok(new RejectGiftResponse(
                    new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Gift.RejectedDuplicate")),
                    giftEvent.EventId,
                    existingReturn.EventId,
                    returnEventCreated: false));
            }
        }

        bool actorOnline = state.OnlinePresence.IsUserOnline(giftEvent.Actor.UserId);
        GiftReturnResult result = state.Ledger.RejectGiftAndCreateReturn(
            giftEvent.EventId,
            DateTimeOffset.UtcNow,
            request.Reason,
            originalActorOnline: actorOnline);
        if (result.ReturnEventCreated)
        {
            state.EventNotifications.SignalUser(giftEvent.Actor.UserId);
        }

        return Results.Ok(new RejectGiftResponse(
            result.ReturnEventCreated
                ? ProtocolResponse.Ok(T("Gift.RejectedReturnCreated"))
                : new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Gift.RejectedDuplicate")),
            result.RejectedGift.EventId,
            result.ReturnEvent.EventId,
            result.ReturnEventCreated));
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
            || !TryValidateInlinePawnPackages(request.RequestedThings ?? Array.Empty<ThingReferenceDto>(), out pawnFailure))
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

    private static IResult CreateTradeOrder(CreateTradeOrderRequest request, ClashOfRimNetworkState state)
    {
        ApplyTradeOrderExpirations(state, DateTimeOffset.UtcNow);
        AuthoritativeEvent? existingEvent = FindEventByIdempotencyKey(state.Ledger, request.IdempotencyKey);
        if (existingEvent is not null)
        {
            return Results.Ok(ToEventCreationResponse(
                new LedgerAppendResult(existingEvent, Created: false),
                ProtocolDeliverySemantics.OfflinePending,
                T("Trade.OrderCreated")));
        }

        if (!state.ServerConfiguration.TradeMarketplaceEnabled)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Trade.Disabled")),
                eventId: null,
                ProtocolDeliverySemantics.OfflinePending));
        }

        if ((request.OfferedThings is null || request.OfferedThings.Count == 0)
            && (request.RequestedThings is null || request.RequestedThings.Count == 0))
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Trade.OrderMissingFields")),
                eventId: null,
                ProtocolDeliverySemantics.OfflinePending));
        }

        if (ContainsConcreteThingPackage(request.RequestedThings))
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, "trade request requirements cannot carry thing packages"),
                eventId: null,
                ProtocolDeliverySemantics.OfflinePending));
        }

        if (!TryStoreInlinePawnPackages(
                state,
                request.Owner,
                "trade-order-offer:" + request.IdempotencyKey,
                request.OfferedThings ?? Array.Empty<ThingReferenceDto>(),
                out IReadOnlyList<ThingReferenceDto> offeredThings,
                out ProtocolResponse? pawnFailure)
            || !TryStoreInlinePawnPackages(
                state,
                request.Owner,
                "trade-order-request:" + request.IdempotencyKey,
                request.RequestedThings ?? Array.Empty<ThingReferenceDto>(),
                out IReadOnlyList<ThingReferenceDto> requestedThings,
                out pawnFailure))
        {
            return Results.Ok(new EventCreationResponse(
                pawnFailure ?? ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.ParseFailed")),
                eventId: null,
                ProtocolDeliverySemantics.OfflinePending));
        }

        AdminBaselineSnapshot? currentBaseline = state.AdminBaseline.Current;
        IReadOnlyList<string> invalidBaselineDefs = currentBaseline is null
            ? Array.Empty<string>()
            : FindUnavailableTradeThingDefs(
                offeredThings.Select(thing => ToEventThingReference(thing, request.Owner.SnapshotId)),
                currentBaseline,
                BuildValidTradeThingDefSet(currentBaseline, state.ServerConfiguration));
        IReadOnlyList<string> invalidRequestedPackableDefs = currentBaseline is null
            ? Array.Empty<string>()
            : FindUnavailablePackableBuildingDefs(
                requestedThings.Select(thing => ToEventThingReference(thing, request.Owner.SnapshotId)),
                currentBaseline);
        if (invalidBaselineDefs.Count > 0)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ServerRejected,
                    T("Trade.MissingMarketValues", ("DEFS", string.Join(", ", invalidBaselineDefs)))),
                eventId: null,
                ProtocolDeliverySemantics.OfflinePending));
        }

        if (invalidRequestedPackableDefs.Count > 0)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ServerRejected,
                    T("Trade.MissingMarketValues", ("DEFS", string.Join(", ", invalidRequestedPackableDefs)))),
                eventId: null,
                ProtocolDeliverySemantics.OfflinePending));
        }

        TradeFeeCalculationResult feeCalculation =
            state.AdminBaseline.BuildEffectiveTradeFeePolicy(state.ServerConfiguration)
                .CalculateRequiredFeeResult(offeredThings, requestedThings);
        if (!feeCalculation.Accepted)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ServerRejected,
                    T("Trade.MissingMarketValues", ("DEFS", string.Join(", ", feeCalculation.MissingMarketValueDefs)))),
                eventId: null,
                ProtocolDeliverySemantics.OfflinePending));
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
                ProtocolDeliverySemantics.OfflinePending));
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
                    ProtocolDeliverySemantics.OfflinePending));
            }
        }

        AuthoritativeEvent tradeEvent = AuthoritativeEventFactory.Create(
            ServerEventType.Trade,
            ToEventParty(request.Owner),
            new EventParty("server"),
            request.IdempotencyKey,
            targetOnline: false,
            new TradeEventPayload(
                request.IdempotencyKey,
                TradeStage.MarketOrder,
                offeredThings.Select(thing => ToEventThingReference(thing, request.Owner.SnapshotId)).ToList(),
                requestedThings.Select(thing => ToEventThingReference(thing, request.Owner.SnapshotId)).ToList(),
                request.FeeSilver,
                AcceptedByUserId: null,
                FulfillmentMode: ResolveTradeFulfillmentMode(request),
                PostagePaidByAcceptor: false),
            DateTimeOffset.UtcNow,
            ResolveTradeOwnerTargetContext(request, state));

        LedgerAppendResult append = state.Ledger.Append(tradeEvent);
        LogEventAppend(state, append, "trade-order-created");
        if (append.Created)
        {
            state.EventNotifications.SignalUsers(
                state.OnlinePresence.ListOnlineUsers()
                    .Where(userId => !string.Equals(userId, request.Owner.UserId, StringComparison.Ordinal)));
        }

        return Results.Ok(ToEventCreationResponse(
            append,
            ProtocolDeliverySemantics.OfflinePending,
            T("Trade.OrderCreated")));
    }

    private static int CountOpenMarketTradeOrdersForOwner(
        IAuthoritativeEventLedger ledger,
        ProtocolIdentity owner)
    {
        return ledger
            .ListByTypeForActor(ServerEventType.Trade, owner.UserId, owner.ColonyId)
            .Count(IsOpenMarketTradeOrderForOwner);
    }

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

        ApplyTradeOrderExpirations(state, DateTimeOffset.UtcNow);
        AuthoritativeEvent? existingEvent = FindEventByIdempotencyKey(state.Ledger, request.IdempotencyKey);
        if (existingEvent is not null)
        {
            string? sourceSnapshotId = existingEvent.Payload is TradeEventPayload existingPayload
                ? existingPayload.OfferedItems.FirstOrDefault()?.SourceSnapshotId
                : null;
            string? nextLineageToken = FindSnapshotNextLineageToken(
                state,
                request.Owner.UserId,
                request.Owner.ColonyId ?? string.Empty,
                sourceSnapshotId);
            return Results.Ok(new EventCreationResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Trade.OrderTransactionDuplicate")),
                existingEvent.EventId,
                ProtocolDeliverySemantics.OfflinePending,
                sourceSnapshotId,
                nextLineageToken));
        }

        if (!state.ServerConfiguration.TradeMarketplaceEnabled)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Trade.Disabled")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        if (!TryValidateInlinePawnPackages(request.OfferedThings ?? Array.Empty<ThingReferenceDto>(), out ProtocolResponse? pawnFailure)
            || !TryValidateInlinePawnPackages(request.RequestedThings ?? Array.Empty<ThingReferenceDto>(), out pawnFailure))
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
            nowUtc);
        if (!upload.Accepted || upload.AcceptedSnapshot is null)
        {
            return Results.Ok(new EventCreationResponse(
                ToProtocolResponse(upload),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        string acceptedSnapshotId = upload.AcceptedSnapshot.Identity.SnapshotId
            ?? transaction.ConfirmedSnapshot.SnapshotId!;
        if (!TryStoreInlinePawnPackages(
                state,
                request.Owner,
                "trade-order-offer:" + request.IdempotencyKey,
                request.OfferedThings ?? Array.Empty<ThingReferenceDto>(),
                out IReadOnlyList<ThingReferenceDto> offeredThings,
                out pawnFailure)
            || !TryStoreInlinePawnPackages(
                state,
                request.Owner,
                "trade-order-request:" + request.IdempotencyKey,
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
            preferredContext: null);
        EventTargetContext? targetContext = ownerEndpoint.WorldObjectId is null
            && ownerEndpoint.MapUniqueId is null
            && ownerEndpoint.Tile is null
                ? null
                : new EventTargetContext(
                    ownerEndpoint.WorldObjectId,
                    ownerEndpoint.MapUniqueId,
                    ownerEndpoint.Tile,
                    EventLandingMode.DropPod);

        AuthoritativeEvent tradeEvent = AuthoritativeEventFactory.Create(
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

        LedgerAppendResult append = state.Ledger.Append(tradeEvent);
        LogEventAppend(state, append, "trade-order-created-with-snapshot");
        if (append.Created)
        {
            state.EventNotifications.SignalUsers(
                state.OnlinePresence.ListOnlineUsers()
                    .Where(userId => !string.Equals(userId, request.Owner.UserId, StringComparison.Ordinal)));
        }

        RunSnapshotPostUploadProcessors(
            state,
            request.Owner.UserId,
            request.Owner.ColonyId ?? string.Empty,
            sessionId: null,
            upload,
            nowUtc);

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

    private static IResult AcceptTradeOrder(AcceptTradeOrderRequest request, ClashOfRimNetworkState state)
    {
        ApplyTradeOrderExpirations(state, DateTimeOffset.UtcNow);

        ProtocolResponse? validation = ValidateTradeAcceptanceRequest(request);
        if (validation is not null)
        {
            return Results.Ok(new AcceptTradeOrderResponse(
                validation,
                request.TradeEventId,
                memoEventId: null,
                memoCreated: false));
        }

        AuthoritativeEvent? tradeOrder = state.Ledger.Find(request.TradeEventId);
        if (tradeOrder is null || !IsOpenMarketTradeOrder(tradeOrder, request.Acceptor.UserId))
        {
            return Results.Ok(new AcceptTradeOrderResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Trade.OrderNotAcceptable")),
                request.TradeEventId,
                memoEventId: null,
                memoCreated: false));
        }

        AuthoritativeEvent? existingMemo = FindTradeAcceptanceMemo(
            state.Ledger,
            request.TradeEventId,
            request.Acceptor.UserId,
            request.Acceptor.ColonyId);
        if (existingMemo is not null)
        {
            return Results.Ok(new AcceptTradeOrderResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Trade.OrderAcceptedDuplicate")),
                request.TradeEventId,
                existingMemo.EventId,
                memoCreated: false));
        }

        if (request.PostagePaidByAcceptor)
        {
            TradePostageQuoteDto postage = BuildTradePostageQuote(
                state,
                tradeOrder,
                request.Acceptor.UserId,
                request.Acceptor.ColonyId ?? string.Empty,
                request.Acceptor.SnapshotId ?? string.Empty);
            if (!postage.Reachable || postage.PostageSilver is null)
            {
                return Results.Ok(new AcceptTradeOrderResponse(
                    ProtocolResponse.Reject(
                        ProtocolErrorCode.ServerRejected,
                        T("Trade.DropPodUnavailable", ("STATUS", postage.Status))),
                    request.TradeEventId,
                    memoEventId: null,
                    memoCreated: false));
            }
        }

        var acceptorParty = ToEventParty(request.Acceptor);
        TradeEventPayload orderPayload = (TradeEventPayload)tradeOrder.Payload;
        AuthoritativeEvent memoEvent = AuthoritativeEventFactory.Create(
            ServerEventType.Trade,
            acceptorParty,
            acceptorParty,
            request.IdempotencyKey,
            targetOnline: state.OnlinePresence.IsUserOnline(request.Acceptor.UserId),
            new TradeEventPayload(
                tradeOrder.EventId,
                TradeStage.AcceptedMemo,
                orderPayload.OfferedItems,
                orderPayload.RequestedItems,
                orderPayload.FeeSilver,
                AcceptedByUserId: request.Acceptor.UserId,
                FulfillmentMode: TradeFulfillmentMode.Unspecified,
                PostagePaidByAcceptor: request.PostagePaidByAcceptor),
            DateTimeOffset.UtcNow,
            tradeOrder.TargetContext) with
            {
                Status = ServerEventStatus.Recorded,
                LastApplicationResult = EventApplicationResultKind.ReadyToApply
            };

        LedgerAppendResult append = state.Ledger.Append(memoEvent);
        LogEventAppend(state, append, "trade-order-accepted-memo");

        return Results.Ok(new AcceptTradeOrderResponse(
            append.Created
                ? ProtocolResponse.Ok(ServerLocalization.Text("Trade.OrderAccepted"))
                : new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Trade.OrderAcceptedDuplicate")),
            tradeOrder.EventId,
            append.Event.EventId,
            append.Created));
    }

    private static IResult FulfillTradeOrder(FulfillTradeOrderRequest request, ClashOfRimNetworkState state)
    {
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

        AuthoritativeEvent? existingExchange = FindTradeExchangeByIdempotencyKey(state.Ledger, request.IdempotencyKey);
        if (existingExchange is not null && existingExchange.Payload is TradeEventPayload existingPayload)
        {
            return Results.Ok(new FulfillTradeOrderResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Trade.FulfillmentDuplicate")),
                existingPayload.TradeId,
                existingPayload.AcceptedMemoEventId,
                existingExchange.EventId,
                exchangeCreated: false,
                existingPayload.OfferedItems.Select(ToThingReferenceDto).ToList(),
                Array.Empty<string>(),
                state.Ledger.Find(existingPayload.TradeId)?.Status.ToString() ?? existingExchange.Status.ToString()));
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

        if (!TryStoreInlinePawnPackages(
                state,
                request.Acceptor,
                "trade-fulfill-delivered:" + request.IdempotencyKey,
                request.DeliveredThings,
                out IReadOnlyList<ThingReferenceDto> deliveredThings,
                out ProtocolResponse? pawnFailure))
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
        IReadOnlyList<ThingReferenceDto> requiredThings = orderPayload.RequestedItems.Select(ToThingReferenceDto).ToList();
        if (!TradeThingRequirementMatcher.Satisfies(
                requiredThings,
                deliveredThings,
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

        var acceptorParty = ToEventParty(request.Acceptor);
        TradeStage exchangeStage = fulfillmentMode == TradeFulfillmentMode.ServerDropPod
            ? TradeStage.ServerDropPodExchange
            : TradeStage.SelfDeliveryExchange;
        AuthoritativeEvent exchangeEvent = AuthoritativeEventFactory.Create(
            ServerEventType.Trade,
            acceptorParty,
            tradeOrder.Actor,
            request.IdempotencyKey,
            targetOnline: state.OnlinePresence.IsUserOnline(tradeOrder.Actor.UserId),
            new TradeEventPayload(
                tradeOrder.EventId,
                exchangeStage,
                orderPayload.OfferedItems,
                deliveredThings.Select(thing => ToEventThingReference(thing, request.Acceptor.SnapshotId)).ToList(),
                orderPayload.FeeSilver,
                AcceptedByUserId: request.Acceptor.UserId,
                AcceptedMemoEventId: acceptedMemo?.EventId,
                FulfillmentMode: fulfillmentMode,
                PostagePaidByAcceptor: fulfillmentMode == TradeFulfillmentMode.ServerDropPod),
            DateTimeOffset.UtcNow,
            tradeOrder.TargetContext);

        LedgerAppendResult append = state.Ledger.Append(exchangeEvent);
        LogEventAppend(state, append, "trade-fulfillment-exchange");
        string? acceptorDeliveryEventId = null;
        string? ownerDeliveryEventId = null;
        if (append.Created)
        {
            state.Ledger.ChangeStatus(tradeOrder.EventId, ServerEventStatus.AppliedToSnapshot);
            IReadOnlyList<AuthoritativeEvent> activeMemos = FindActiveTradeAcceptanceMemos(state.Ledger, tradeOrder.EventId);
            foreach (AuthoritativeEvent memo in activeMemos)
            {
                state.Ledger.ChangeStatus(
                    memo.EventId,
                    acceptedMemo is not null
                        && string.Equals(memo.EventId, acceptedMemo.EventId, StringComparison.Ordinal)
                        ? ServerEventStatus.AppliedToSnapshot
                        : ServerEventStatus.Cancelled);
            }

            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            string fulfillmentKey = acceptedMemo?.EventId ?? request.Acceptor.UserId + ":" + request.Acceptor.ColonyId;
            LedgerAppendResult ownerDelivery = AppendTradeItemDeliveryEvent(
                state,
                $"trade-completed-owner-delivery:{tradeOrder.EventId}:{fulfillmentKey}",
                acceptorParty,
                tradeOrder.Actor,
                deliveredThings.Select(thing => ToEventThingReference(thing, request.Acceptor.SnapshotId)).ToList(),
                "TradeCompletedOwnerDelivery",
                ResolveTradeCompletedOwnerDeliveryTargetContext(tradeOrder, fulfillmentMode),
                nowUtc);
            ownerDeliveryEventId = ownerDelivery.Event.EventId;

            if (fulfillmentMode == TradeFulfillmentMode.ServerDropPod)
            {
                EventTargetContext? acceptorTargetContext = ResolveTradeTargetContext(
                    state,
                    request.Acceptor.UserId,
                    request.Acceptor.ColonyId ?? string.Empty,
                    request.Acceptor.SnapshotId ?? string.Empty,
                    EventLandingMode.DropPod);
                LedgerAppendResult acceptorDelivery = AppendTradeItemDeliveryEvent(
                    state,
                    $"trade-completed-acceptor-delivery:{tradeOrder.EventId}:{fulfillmentKey}",
                    tradeOrder.Actor,
                    acceptorParty,
                    orderPayload.OfferedItems,
                    "TradeCompletedAcceptorDelivery",
                    acceptorTargetContext,
                    nowUtc);
                acceptorDeliveryEventId = acceptorDelivery.Event.EventId;
            }

            IReadOnlyList<string> notifiedUsers = activeMemos
                .Select(memo => memo.Target.UserId)
                .Append(tradeOrder.Actor.UserId)
                .Where(userId => !string.IsNullOrWhiteSpace(userId))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            state.EventNotifications.SignalUsers(notifiedUsers);
        }

        return Results.Ok(new FulfillTradeOrderResponse(
            append.Created
                ? ProtocolResponse.Ok(ServerLocalization.Text("Trade.FulfillmentCreated"))
                : new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Trade.FulfillmentDuplicate")),
            tradeOrder.EventId,
            acceptedMemo?.EventId,
            append.Event.EventId,
            append.Created,
            orderPayload.OfferedItems.Select(ToThingReferenceDto).ToList(),
            Array.Empty<string>(),
            state.Ledger.Find(tradeOrder.EventId)?.Status.ToString() ?? tradeOrder.Status.ToString(),
            acceptorDeliveryEventId,
            ownerDeliveryEventId));
    }

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

        AuthoritativeEvent? existingExchange = FindTradeExchangeByIdempotencyKey(state.Ledger, request.IdempotencyKey);
        if (existingExchange is not null && existingExchange.Payload is TradeEventPayload existingPayload)
        {
            string? nextLineageToken = FindSnapshotNextLineageToken(
                state,
                request.Acceptor.UserId,
                request.Acceptor.ColonyId ?? string.Empty,
                existingExchange.AppliedSnapshotId);
            return Results.Ok(new FulfillTradeOrderResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Trade.FulfillmentTransactionDuplicate")),
                existingPayload.TradeId,
                existingPayload.AcceptedMemoEventId,
                existingExchange.EventId,
                exchangeCreated: false,
                existingPayload.OfferedItems.Select(ToThingReferenceDto).ToList(),
                Array.Empty<string>(),
                state.Ledger.Find(existingPayload.TradeId)?.Status.ToString() ?? existingExchange.Status.ToString(),
                appliedSnapshotId: existingExchange.AppliedSnapshotId,
                nextLineageToken: nextLineageToken));
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
        if (!TryValidateInlinePawnPackages(request.DeliveredThings, out ProtocolResponse? pawnFailure))
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
            nowUtc);
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

        string acceptedSnapshotId = upload.AcceptedSnapshot.Identity.SnapshotId
            ?? transaction.ConfirmedSnapshot.SnapshotId!;
        if (!TryStoreInlinePawnPackages(
                state,
                request.Acceptor,
                "trade-fulfill-delivered:" + request.IdempotencyKey,
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
        AuthoritativeEvent exchangeEvent = AuthoritativeEventFactory.Create(
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

        LedgerAppendResult append = state.Ledger.Append(exchangeEvent);
        LogEventAppend(state, append, "trade-fulfillment-exchange-with-snapshot");
        string? acceptorDeliveryEventId = null;
        string? ownerDeliveryEventId = null;
        AuthoritativeEvent appliedExchange = append.Event;
        if (append.Created)
        {
            state.Ledger.ChangeStatus(tradeOrder.EventId, ServerEventStatus.AppliedToSnapshot);
            IReadOnlyList<AuthoritativeEvent> activeMemos = FindActiveTradeAcceptanceMemos(state.Ledger, tradeOrder.EventId);
            foreach (AuthoritativeEvent memo in activeMemos)
            {
                state.Ledger.ChangeStatus(
                    memo.EventId,
                    acceptedMemo is not null
                        && string.Equals(memo.EventId, acceptedMemo.EventId, StringComparison.Ordinal)
                        ? ServerEventStatus.AppliedToSnapshot
                        : ServerEventStatus.Cancelled);
            }

            string fulfillmentKey = acceptedMemo?.EventId ?? request.Acceptor.UserId + ":" + request.Acceptor.ColonyId;
            LedgerAppendResult ownerDelivery = AppendTradeItemDeliveryEvent(
                state,
                $"trade-completed-owner-delivery:{tradeOrder.EventId}:{fulfillmentKey}",
                acceptorParty,
                tradeOrder.Actor,
                deliveredThings.Select(thing => ToEventThingReference(thing, acceptedSnapshotId)).ToList(),
                "TradeCompletedOwnerDelivery",
                ResolveTradeCompletedOwnerDeliveryTargetContext(tradeOrder, fulfillmentMode),
                nowUtc);
            ownerDeliveryEventId = ownerDelivery.Event.EventId;

            if (fulfillmentMode == TradeFulfillmentMode.ServerDropPod)
            {
                EventTargetContext? acceptorTargetContext = ResolveTradeTargetContext(
                    state,
                    request.Acceptor.UserId,
                    request.Acceptor.ColonyId ?? string.Empty,
                    acceptedSnapshotId,
                    EventLandingMode.DropPod);
                LedgerAppendResult acceptorDelivery = AppendTradeItemDeliveryEvent(
                    state,
                    $"trade-completed-acceptor-delivery:{tradeOrder.EventId}:{fulfillmentKey}",
                    tradeOrder.Actor,
                    acceptorParty,
                    orderPayload.OfferedItems,
                    "TradeCompletedAcceptorDelivery",
                    acceptorTargetContext,
                    nowUtc);
                acceptorDeliveryEventId = acceptorDelivery.Event.EventId;
            }

            AuthoritativeEvent delivered = state.Ledger.MarkDelivered(append.Event.EventId, request.Acceptor.SnapshotId ?? string.Empty, nowUtc);
            appliedExchange = state.Ledger.MarkApplied(delivered.EventId, acceptedSnapshotId, nowUtc);
            state.Ledger.ReportApplicationResult(
                appliedExchange.EventId,
                EventApplicationResultKind.Applied,
                failureReason: null,
                nextRetryAtUtc: null);
            RecordAuthoritativeEventAchievements(state, appliedExchange, nowUtc);

            IReadOnlyList<string> notifiedUsers = activeMemos
                .Select(memo => memo.Target.UserId)
                .Append(tradeOrder.Actor.UserId)
                .Where(userId => !string.IsNullOrWhiteSpace(userId))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            state.EventNotifications.SignalUsers(notifiedUsers);
        }

        RunSnapshotPostUploadProcessors(
            state,
            request.Acceptor.UserId,
            request.Acceptor.ColonyId ?? string.Empty,
            sessionId: null,
            upload,
            nowUtc);

        return Results.Ok(new FulfillTradeOrderResponse(
            append.Created
                ? ProtocolResponse.Ok(ServerLocalization.Text("Trade.FulfillmentTransactionCreated"))
                : new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Trade.FulfillmentTransactionDuplicate")),
            tradeOrder.EventId,
            acceptedMemo?.EventId,
            append.Event.EventId,
            append.Created,
            orderPayload.OfferedItems.Select(ToThingReferenceDto).ToList(),
            Array.Empty<string>(),
            state.Ledger.Find(tradeOrder.EventId)?.Status.ToString() ?? tradeOrder.Status.ToString(),
            acceptorDeliveryEventId,
            ownerDeliveryEventId,
            appliedExchange.AppliedSnapshotId ?? acceptedSnapshotId,
            upload.AcceptedSnapshot.Envelope.NextLineageToken));
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

    private static void ApplyTradeOrderExpirations(ClashOfRimNetworkState state, DateTimeOffset nowUtc)
    {
        TimeSpan expiration = state.ServerConfiguration.TradeOrderExpiration;
        if (expiration <= TimeSpan.Zero)
        {
            return;
        }

        IReadOnlyList<AuthoritativeEvent> expiredOrders = state.Ledger.ListByType(ServerEventType.Trade)
            .Where(IsOpenMarketTradeOrderForOwner)
            .Where(tradeOrder => nowUtc - tradeOrder.CreatedAtUtc >= expiration)
            .ToList();

        foreach (AuthoritativeEvent tradeOrder in expiredOrders)
        {
            IReadOnlyList<AuthoritativeEvent> activeMemos = FindActiveTradeAcceptanceMemos(
                state.Ledger,
                tradeOrder.EventId);

            state.Ledger.ChangeStatus(tradeOrder.EventId, ServerEventStatus.Cancelled);
            foreach (AuthoritativeEvent memo in activeMemos)
            {
                state.Ledger.ChangeStatus(memo.EventId, ServerEventStatus.Cancelled);
            }

            AppendTradeItemDeliveryEvent(
                state,
                $"trade-expired-owner-return:{tradeOrder.EventId}",
                new EventParty("server"),
                tradeOrder.Actor,
                ((TradeEventPayload)tradeOrder.Payload).OfferedItems,
                "TradeExpiredOwnerReturn",
                tradeOrder.TargetContext,
                nowUtc);

            IReadOnlyList<EventParty> targets = new[] { tradeOrder.Actor }
                .Concat(activeMemos.Select(memo => memo.Target))
                .Where(target => !string.IsNullOrWhiteSpace(target.UserId))
                .GroupBy(target => target.UserId + "\n" + target.ColonyId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            foreach (EventParty target in targets)
            {
                bool targetIsOwner = string.Equals(target.UserId, tradeOrder.Actor.UserId, StringComparison.Ordinal)
                    && string.Equals(target.ColonyId, tradeOrder.Actor.ColonyId, StringComparison.Ordinal);
                AppendTradeExpiredNotification(state, tradeOrder, target, targetIsOwner, nowUtc);
            }

            state.EventNotifications.SignalUsers(
                targets.Select(target => target.UserId).Distinct(StringComparer.Ordinal));
        }
    }

    private static int ApplyTradeOrderBaselineInvalidations(
        ClashOfRimNetworkState state,
        AdminBaselineSnapshot baseline,
        DateTimeOffset nowUtc)
    {
        HashSet<string> validThingDefs = BuildValidTradeThingDefSet(baseline, state.ServerConfiguration);

        if (validThingDefs.Count == 0)
        {
            return 0;
        }

        int cancelled = 0;
        IReadOnlyList<AuthoritativeEvent> invalidOrders = state.Ledger.ListByType(ServerEventType.Trade)
            .Where(IsOpenMarketTradeOrderForOwner)
            .Where(tradeOrder => tradeOrder.Payload is TradeEventPayload payload
                && FindUnavailableTradeThingDefs(payload.OfferedItems.Concat(payload.RequestedItems), baseline, validThingDefs).Count > 0)
            .ToList();

        foreach (AuthoritativeEvent tradeOrder in invalidOrders)
        {
            if (tradeOrder.Payload is not TradeEventPayload payload)
            {
                continue;
            }

            IReadOnlyList<string> unavailableDefs = FindUnavailableTradeThingDefs(
                payload.OfferedItems.Concat(payload.RequestedItems),
                baseline,
                validThingDefs);
            if (unavailableDefs.Count == 0)
            {
                continue;
            }

            IReadOnlyList<AuthoritativeEvent> activeMemos = FindActiveTradeAcceptanceMemos(
                state.Ledger,
                tradeOrder.EventId);

            state.Ledger.ChangeStatus(tradeOrder.EventId, ServerEventStatus.Cancelled);
            foreach (AuthoritativeEvent memo in activeMemos)
            {
                state.Ledger.ChangeStatus(memo.EventId, ServerEventStatus.Cancelled);
            }

            AppendTradeItemDeliveryEvent(
                state,
                $"trade-baseline-invalidated-owner-return:{tradeOrder.EventId}",
                new EventParty("server"),
                tradeOrder.Actor,
                payload.OfferedItems,
                "TradeBaselineChangedOwnerReturn",
                tradeOrder.TargetContext,
                nowUtc);

            IReadOnlyList<EventParty> targets = new[] { tradeOrder.Actor }
                .Concat(activeMemos.Select(memo => memo.Target))
                .Where(target => !string.IsNullOrWhiteSpace(target.UserId))
                .GroupBy(target => target.UserId + "\n" + target.ColonyId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            foreach (EventParty target in targets)
            {
                bool targetIsOwner = string.Equals(target.UserId, tradeOrder.Actor.UserId, StringComparison.Ordinal)
                    && string.Equals(target.ColonyId, tradeOrder.Actor.ColonyId, StringComparison.Ordinal);
                AppendTradeBaselineInvalidatedNotification(state, tradeOrder, target, targetIsOwner, unavailableDefs, nowUtc);
            }

            state.EventNotifications.SignalUsers(
                targets.Select(target => target.UserId).Distinct(StringComparer.Ordinal));
            cancelled++;
        }

        return cancelled;
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

    private static void AppendTradeExpiredNotification(
        ClashOfRimNetworkState state,
        AuthoritativeEvent tradeOrder,
        EventParty target,
        bool targetIsOwner,
        DateTimeOffset nowUtc)
    {
        string notificationId = $"trade-expired:{tradeOrder.EventId}:{target.UserId}:{target.ColonyId}";
        string message = targetIsOwner
            ? T("Trade.ExpiredOwnerMessage", ("EVENT", tradeOrder.EventId))
            : T("Trade.ExpiredAcceptorMessage", ("EVENT", tradeOrder.EventId));
        AuthoritativeEvent notification = AuthoritativeEventFactory.Create(
            ServerEventType.ServerNotification,
            new EventParty("server"),
            target,
            notificationId,
            state.OnlinePresence.IsUserOnline(target.UserId),
            new ServerNotificationEventPayload(
                notificationId,
                T("Trade.ExpiredTitle"),
                message,
                ServerNotificationSeverity.Warning,
                FromAdministrator: false),
            nowUtc);
        LogEventAppend(state, state.Ledger.Append(notification), "trade-expired-notification");
    }

    private static void AppendTradeBaselineInvalidatedNotification(
        ClashOfRimNetworkState state,
        AuthoritativeEvent tradeOrder,
        EventParty target,
        bool targetIsOwner,
        IReadOnlyList<string> unavailableDefs,
        DateTimeOffset nowUtc)
    {
        string notificationId = $"trade-baseline-invalidated:{tradeOrder.EventId}:{target.UserId}:{target.ColonyId}";
        string unavailable = string.Join(", ", unavailableDefs);
        string message = targetIsOwner
            ? T("Trade.BaselineInvalidatedOwnerMessage", ("EVENT", tradeOrder.EventId), ("ITEMS", unavailable))
            : T("Trade.BaselineInvalidatedAcceptorMessage", ("EVENT", tradeOrder.EventId), ("ITEMS", unavailable));
        AuthoritativeEvent notification = AuthoritativeEventFactory.Create(
            ServerEventType.ServerNotification,
            new EventParty("server"),
            target,
            notificationId,
            state.OnlinePresence.IsUserOnline(target.UserId),
            new ServerNotificationEventPayload(
                notificationId,
                T("Trade.BaselineInvalidatedTitle"),
                message,
                ServerNotificationSeverity.Warning,
                FromAdministrator: false),
            nowUtc);
        LogEventAppend(state, state.Ledger.Append(notification), "trade-baseline-invalidated-notification");
    }

    private static IResult CancelTradeOrder(CloseTradeOrderRequest request, ClashOfRimNetworkState state)
    {
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
        if (tradeOrder is not null
            && tradeOrder.Type == ServerEventType.Trade
            && tradeOrder.Payload is TradeEventPayload { Stage: TradeStage.MarketOrder }
            && (!string.Equals(tradeOrder.Actor.UserId, request.Owner.UserId, StringComparison.Ordinal)
                || !string.Equals(tradeOrder.Actor.ColonyId, request.Owner.ColonyId, StringComparison.Ordinal)))
        {
            return CancelTradeAcceptanceMemo(request, state, tradeOrder);
        }

        return CloseTradeOrder(
            request,
            state,
            ServerEventStatus.Cancelled,
            ServerEventStatus.Cancelled,
            T("Trade.CancelledWithNotifications"),
            T("Trade.Cancelled"));
    }

    private static IResult CancelTradeAcceptanceMemo(
        CloseTradeOrderRequest request,
        ClashOfRimNetworkState state,
        AuthoritativeEvent tradeOrder)
    {
        if (!IsOpenMarketTradeOrderForOwner(tradeOrder))
        {
            return Results.Ok(new CloseTradeOrderResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Trade.NotOpen")),
                tradeOrder.EventId,
                tradeOrder.Status.ToString(),
                notifiedAcceptorCount: 0));
        }

        AuthoritativeEvent? memo = FindTradeAcceptanceMemo(
            state.Ledger,
            tradeOrder.EventId,
            request.Owner.UserId,
            request.Owner.ColonyId);
        if (memo is null)
        {
            return Results.Ok(new CloseTradeOrderResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Trade.AcceptanceMemoNotFound")),
                tradeOrder.EventId,
                string.Empty,
                notifiedAcceptorCount: 0));
        }

        state.Ledger.ChangeStatus(memo.EventId, ServerEventStatus.Cancelled);
        IReadOnlyList<string> notifiedUsers = new[]
            {
                tradeOrder.Actor.UserId,
                request.Owner.UserId
            }
            .Where(userId => !string.IsNullOrWhiteSpace(userId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        state.EventNotifications.SignalUsers(notifiedUsers);

        return Results.Ok(new CloseTradeOrderResponse(
            ProtocolResponse.Ok(T("Trade.AcceptanceMemoCancelled")),
            tradeOrder.EventId,
            ServerEventStatus.Cancelled.ToString(),
            notifiedUsers.Count));
    }

    private static IResult CompleteTradeOrder(CloseTradeOrderRequest request, ClashOfRimNetworkState state)
    {
        return CloseTradeOrder(
            request,
            state,
            ServerEventStatus.AppliedToSnapshot,
            ServerEventStatus.AppliedToSnapshot,
            T("Trade.CompletedWithNotifications"),
            T("Trade.Completed"));
    }

    private static IResult CloseTradeOrder(
        CloseTradeOrderRequest request,
        ClashOfRimNetworkState state,
        ServerEventStatus tradeTerminalStatus,
        ServerEventStatus memoTerminalStatus,
        string successMessage,
        string alreadyClosedMessage)
    {
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

        if (tradeOrder.Status == tradeTerminalStatus)
        {
            IReadOnlyList<AuthoritativeEvent> existingMemos = FindActiveTradeAcceptanceMemos(
                state.Ledger,
                tradeOrder.EventId);
            state.EventNotifications.SignalUsers(existingMemos.Select(memo => memo.Target.UserId));
            return Results.Ok(new CloseTradeOrderResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, alreadyClosedMessage),
                tradeOrder.EventId,
                tradeTerminalStatus.ToString(),
                existingMemos.Select(memo => memo.Target.UserId).Distinct(StringComparer.Ordinal).Count()));
        }

        if (!IsOpenMarketTradeOrderForOwner(tradeOrder))
        {
            return Results.Ok(new CloseTradeOrderResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Trade.NotOpen")),
                tradeOrder.EventId,
                tradeOrder.Status.ToString(),
                notifiedAcceptorCount: 0));
        }

        IReadOnlyList<AuthoritativeEvent> activeMemos = FindActiveTradeAcceptanceMemos(
            state.Ledger,
            tradeOrder.EventId);
        state.Ledger.ChangeStatus(tradeOrder.EventId, tradeTerminalStatus);
        foreach (AuthoritativeEvent memo in activeMemos)
        {
            state.Ledger.ChangeStatus(memo.EventId, memoTerminalStatus);
        }

        if (tradeTerminalStatus == ServerEventStatus.Cancelled)
        {
            AppendTradeItemDeliveryEvent(
                state,
                $"trade-cancelled-owner-return:{tradeOrder.EventId}",
                new EventParty("server"),
                tradeOrder.Actor,
                payload.OfferedItems,
                "TradeCancelledOwnerReturn",
                tradeOrder.TargetContext,
                DateTimeOffset.UtcNow);
        }

        IReadOnlyList<string> notifiedUsers = activeMemos
            .Select(memo => memo.Target.UserId)
            .Where(userId => !string.IsNullOrWhiteSpace(userId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        state.EventNotifications.SignalUsers(notifiedUsers);

        return Results.Ok(new CloseTradeOrderResponse(
            ProtocolResponse.Ok(successMessage),
            tradeOrder.EventId,
            tradeTerminalStatus.ToString(),
            notifiedUsers.Count));
    }
}
