using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using System.Globalization;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Economy;
using AIRsLight.ClashOfRim.Network.Modules.Events;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Modules.Transfers;
using static AIRsLight.ClashOfRim.Network.Modules.Snapshots.SnapshotTransactions;
using static AIRsLight.ClashOfRim.Network.Modules.Transfers.ItemTransferMapper;
using static AIRsLight.ClashOfRim.Network.Modules.Events.EventProtocolMapper;

namespace AIRsLight.ClashOfRim.Network.Modules.Gifts;

internal sealed class GiftService(
    IAuthoritativeEventLedger ledger, IColonySnapshotIndexStore snapshots,
    BankLoanRegistry loans, MercenaryContractRegistry mercenaries, SnapshotCommitService commits,
    SnapshotRequestAuthorization requests, SnapshotTransactions transactions, PendingConfirmationReconciler pending,
    ItemTransferPackages packages, DeliveryTargetResolver targets, OnlinePresenceRegistry presence,
    EventNotificationHub notifications, DiplomacyRelationRegistry relations, IRaidDeliveryProtection raidProtection,
    ISnapshotCommitEffects effects, Func<ClashOfRimServerConfiguration> configuration)
{
    internal EventCreationResponse CreateWithSnapshot(CreateGiftWithSnapshotRequest transaction,
        byte[] payload, string? principalToken)
    {
        CreateGiftRequest? request = transaction.Gift;
        if (request is null)
        {
            return new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Gift.TransactionMissingGift")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate);
        }

        ProtocolResponse? validation = ValidateGiftCreationRequest(request);
        if (validation is not null)
        {
            return new EventCreationResponse(
                validation,
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate);
        }

        if (transaction.ConfirmedSnapshot is null
            || string.IsNullOrWhiteSpace(transaction.ConfirmedSnapshot.SnapshotId))
        {
            return new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Gift.TransactionMissingSnapshot")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate);
        }

        // Hash the typed request and actual bytes before taking the business gate; header credentials are not part of the body.
        string binding = SnapshotRequestBinding.Hash(transaction, payload);
        using (var recovery = requests.EnterPrincipal(principalToken, request.Actor))
        {
            if (recovery.Rejection is { } denied)
                return new EventCreationResponse(denied, null, ProtocolDeliverySemantics.OnlineImmediate);

            AuthoritativeEvent? existingEvent = ledger.FindByIdempotencyKey(request.IdempotencyKey);
            if (existingEvent is not null)
                return Replay(request, transaction.ConfirmedSnapshot.SnapshotId!, existingEvent, binding);
            if (!configuration().GiftsEnabled)
                return new EventCreationResponse(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected,
                    ServerLocalization.Text("Gift.Disabled")), null, ProtocolDeliverySemantics.OnlineImmediate);
            if (pending.ReconcileSnapshot(request.Actor.UserId, request.Actor.ColonyId ?? string.Empty, DateTimeOffset.UtcNow) is { } expired)
                return new EventCreationResponse(expired, null, ProtocolDeliverySemantics.OnlineImmediate);
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        EventTargetContext? targetContext = ResolveTargetContext(request);
        if (IsForcedGiftDelivery(request))
        {
            // Raid recovery may prepare snapshots; run it outside the business gate.
            // Final admission below rechecks credentials and protection after preparation.
            raidProtection.Reconcile(nowUtc);
            ProtocolResponse? forcedValidation = ValidateForcedGiftDelivery(request, nowUtc, targetContext);
            if (forcedValidation is not null)
            {
                return new EventCreationResponse(
                    forcedValidation,
                    eventId: null,
                    ProtocolDeliverySemantics.ServerNotification);
            }
        }

        if (!TryValidateInlinePawnPackages(request.Things, out ProtocolResponse? packageFailure)
            || !TryValidateInlineThingPackages(request.Things, out packageFailure))
        {
            return new EventCreationResponse(
                packageFailure ?? ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("PawnPackage.ParseFailed")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate);
        }

        SnapshotUploadResult upload = transactions.PrepareTransactionSnapshot(
            request.Actor.UserId,
            request.Actor.ColonyId ?? string.Empty,
            transaction.ConfirmedSnapshot.SnapshotId!,
            transaction.ConfirmedSnapshot,
            payload,
            nowUtc);
        using (var recovery = requests.EnterPrincipal(principalToken, request.Actor))
        {
            if (recovery.Rejection is { } denied)
                return new EventCreationResponse(denied, null, ProtocolDeliverySemantics.OnlineImmediate);
            AuthoritativeEvent? existingEvent = ledger.FindByIdempotencyKey(request.IdempotencyKey);
            if (existingEvent is not null)
                return Replay(request, transaction.ConfirmedSnapshot.SnapshotId!, existingEvent, binding);
            if (pending.ReconcileSnapshot(request.Actor.UserId, request.Actor.ColonyId ?? string.Empty, DateTimeOffset.UtcNow) is { } expired)
                return new EventCreationResponse(expired, null, ProtocolDeliverySemantics.OnlineImmediate);
            if (!upload.Accepted || upload.AcceptedSnapshot is null)
            {
                return new EventCreationResponse(
                    ToProtocolResponse(upload),
                    eventId: null,
                    ProtocolDeliverySemantics.OnlineImmediate);
            }
            if (!configuration().GiftsEnabled)
                return new EventCreationResponse(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected,
                    ServerLocalization.Text("Gift.Disabled")), null, ProtocolDeliverySemantics.OnlineImmediate);
            nowUtc = DateTimeOffset.UtcNow;
            targetContext = ResolveTargetContext(request);
            if (IsForcedGiftDelivery(request) && ValidateForcedGiftDelivery(request, nowUtc, targetContext) is { } finalGiftFailure)
                return new EventCreationResponse(finalGiftFailure, null, ProtocolDeliverySemantics.ServerNotification);
            upload = transactions.CreateSnapshotReceiver().CommitPrepared(
                new SnapshotUploadContext(request.Actor.UserId, request.Actor.ColonyId ?? string.Empty,
                    transaction.ConfirmedSnapshot.SnapshotId!, null, upload.SnapshotUploadKind),
                ProtocolDtoMapper.ToSaveSnapshotPackage(transaction.ConfirmedSnapshot, payload),
                upload, storeAccepted: false);
            if (!upload.Accepted || upload.AcceptedSnapshot is null)
                return new EventCreationResponse(ToProtocolResponse(upload), null,
                    ProtocolDeliverySemantics.OnlineImmediate);
            string acceptedSnapshotId = upload.AcceptedSnapshot.Identity.SnapshotId
                ?? transaction.ConfirmedSnapshot.SnapshotId!;
            bool targetOnline = presence.IsUserOnline(request.Target.UserId);
            var acceptedActor = new ProtocolIdentity(request.Actor.UserId, request.Actor.ColonyId, acceptedSnapshotId);
            string giftCommitId = Guid.NewGuid().ToString("N");
            if (!packages.TryStore(
                    acceptedActor,
                    "gift-commit:" + giftCommitId,
                    request.Things,
                    out IReadOnlyList<ThingReferenceDto> giftThings,
                    out packageFailure))
            {
                return new EventCreationResponse(
                    packageFailure ?? ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("PawnPackage.ParseFailed")),
                    eventId: null,
                    ProtocolDeliverySemantics.OnlineImmediate);
            }

            AuthoritativeEvent giftEvent = AuthoritativeEventFactory.CreateForLedger(ledger,
                ServerEventType.ItemDelivery,
                ToEventParty(acceptedActor),
                ToEventParty(request.Target),
                request.IdempotencyKey,
                targetOnline,
                new ItemDeliveryEventPayload(
                    giftThings.Select(thing => ToEventThingReference(thing, acceptedSnapshotId)).ToList(),
                    request.Message,
                    NormalizeGiftDeliveryKind(request.DeliveryKind), CreationRequestHash: binding),
                nowUtc,
                targetContext) with
            { EventId = "gift:" + giftCommitId };

            commits.CommitGift(snapshots, loans, ledger,
                ProtocolDtoMapper.ToSaveSnapshotPackage(transaction.ConfirmedSnapshot, payload),
                upload.AcceptedSnapshot, giftEvent,
                effects.AfterCommit,
                userId => notifications.SignalUsers(new[] { userId }), upload.SnapshotUploadKind, mercenaries, preparedStoragePayload: upload.PreparedStoragePayload);
            var append = new LedgerAppendResult(giftEvent, Created: true);
            EventCreationResponse created = ToEventCreationResponse(
                append,
                targetOnline ? ProtocolDeliverySemantics.OnlineImmediate : ProtocolDeliverySemantics.OfflinePending,
                ServerLocalization.Text("Gift.Created"));
            return new EventCreationResponse(
                created.Result,
                created.EventId,
                created.DeliverySemantics,
                acceptedSnapshotId,
                upload.AcceptedSnapshot.Envelope.NextLineageToken);
        }
    }

    private EventCreationResponse Replay(CreateGiftRequest request,
        string snapshotId, AuthoritativeEvent existing, string binding)
    {
        if (existing.Payload is not ItemDeliveryEventPayload payload || payload.CreationRequestHash != binding
            || !GiftSnapshotReplayPolicy.Matches(existing, request.Actor.UserId, request.Actor.ColonyId, snapshotId)
            || existing.Target.UserId != request.Target.UserId || existing.Target.ColonyId != request.Target.ColonyId)
            return new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, "Gift idempotency key belongs to another operation."),
                null, ProtocolDeliverySemantics.OnlineImmediate);
        return new EventCreationResponse(
            new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Gift.TransactionDuplicate")),
            existing.EventId,
            existing.Status == ServerEventStatus.ReadyForImmediateDelivery
                ? ProtocolDeliverySemantics.OnlineImmediate : ProtocolDeliverySemantics.OfflinePending,
            snapshotId, transactions.FindSnapshotNextLineageToken(request.Actor.UserId, request.Actor.ColonyId ?? string.Empty, snapshotId));
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
                ServerLocalization.Text("Gift.CreateMissingFields"));
        }

        if (request.Things is null || request.Things.Count == 0)
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                ServerLocalization.Text("Gift.Empty"));
        }

        foreach (ThingReferenceDto thing in request.Things)
        {
            if (string.IsNullOrWhiteSpace(thing.GlobalKey)
                || string.IsNullOrWhiteSpace(thing.DefName)
                || thing.StackCount <= 0)
            {
                return ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    ServerLocalization.Text("Gift.InvalidThing"));
            }

            if (thing.PawnPackage is not null)
            {
                if (!TryToPawnExchangePackage(thing.PawnPackage, out PawnExchangePackage? package, out string packageFailure)
                    || package is null)
                {
                    return ProtocolResponse.Reject(
                        ProtocolErrorCode.ValidationFailed,
                        ServerLocalization.Text("Gift.PawnPackageInvalid", new Dictionary<string, string?> { ["MESSAGE"] = packageFailure }));
                }

                try
                {
                    _ = SafePawnExchangeSerializer.Serialize(package);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    return ProtocolResponse.Reject(
                        ProtocolErrorCode.ValidationFailed,
                        ServerLocalization.Text("Gift.PawnPackageInvalid", new Dictionary<string, string?> { ["MESSAGE"] = ex.Message }));
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
                ServerLocalization.Text("Gift.UnknownDeliveryKind"));
        }

        return null;
    }

    private ProtocolResponse? ValidateForcedGiftDelivery(
        CreateGiftRequest request,
        DateTimeOffset nowUtc,
        EventTargetContext? targetContext)
    {
        if (!configuration().PvpEnabled)
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ServerRejected,
                ServerLocalization.Text("Pvp.Disabled"));
        }

        AuthoritativeEvent? defenderBlockingRaid = raidProtection.FindBlockingRaid(
            request.Target.UserId,
            request.Target.ColonyId);
        if (defenderBlockingRaid is not null)
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ServerRejected,
                ServerLocalization.Text("Gift.ForcedBlockedByRaid"));
        }

        LatestSnapshotRecord? defenderSnapshot = snapshots.GetLatest(
            request.Target.UserId,
            request.Target.ColonyId ?? string.Empty);
        if (defenderSnapshot is null)
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.SnapshotMismatch,
                ServerLocalization.Text("Gift.ForcedMissingTargetSnapshot"));
        }

        if (targetContext is null || string.IsNullOrWhiteSpace(targetContext.MapUniqueId))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                ServerLocalization.Text("Gift.ForcedMissingTargetMap"));
        }

        string relationKind = relations.GetRelationKind(
            request.Actor.UserId,
            request.Actor.ColonyId,
            request.Target.UserId,
            request.Target.ColonyId);
        DateTimeOffset? cooldownUntilUtc = raidProtection.CurrentCooldownUntil(
            request.Target.UserId,
            request.Target.ColonyId,
            nowUtc);
        RaidEligibilityResult eligibility = RaidEligibilityChecker.Check(new RaidEligibilityRequest(
            ToEventParty(request.Actor),
            ToEventParty(request.Target),
            string.Equals(relationKind, DiplomacyRelationRegistry.RelationHostile, StringComparison.Ordinal),
            presence.IsUserOnline(request.Target.UserId),
            nowUtc,
            cooldownUntilUtc,
            DefenderWealth: int.MaxValue,
            defenderSnapshot.Identity,
            defenderSnapshot.Index.Maps,
            targetContext.MapUniqueId),
            RaidPolicies.BuildEligibility(configuration()));
        if (eligibility.Eligible)
        {
            return ValidateForcedGiftDeliveryCooldown(request, nowUtc);
        }

        return ProtocolResponse.Reject(
            ProtocolErrorCode.ServerRejected,
            ServerLocalization.Text("Gift.ForcedRaidProtection", new Dictionary<string, string?> { ["REASONS"] = string.Join(", ", eligibility.FailureReasons) }));
    }

    private ProtocolResponse? ValidateForcedGiftDeliveryCooldown(
        CreateGiftRequest request,
        DateTimeOffset nowUtc)
    {
        TimeSpan cooldown = configuration().ForcedGiftDeliveryCooldown;
        if (cooldown <= TimeSpan.Zero)
        {
            return null;
        }

        AuthoritativeEvent? latest = ledger.ListByType(ServerEventType.ItemDelivery)
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
            ? ServerLocalization.Text("Diplomacy.TimeMinutes", new Dictionary<string, string?> { ["VALUE"] = Math.Ceiling(remaining.TotalMinutes).ToString("0", CultureInfo.InvariantCulture) })
            : ServerLocalization.Text("Diplomacy.TimeSeconds", new Dictionary<string, string?> { ["VALUE"] = Math.Ceiling(remaining.TotalSeconds).ToString("0", CultureInfo.InvariantCulture) });
        return ProtocolResponse.Reject(
            ProtocolErrorCode.ServerRejected,
            ServerLocalization.Text("Gift.ForcedCooldown", new Dictionary<string, string?> { ["REMAINING"] = remainingText }));
    }

    private EventTargetContext? ResolveTargetContext(CreateGiftRequest request)
    {
        if (request.TargetContext is not null)
        {
            EventTargetContext requestedContext = ToEventTargetContext(request.TargetContext);
            if (!string.IsNullOrWhiteSpace(requestedContext.MapUniqueId))
            {
                return requestedContext;
            }

            EventLandingMode requestedLandingMode = requestedContext.LandingMode == EventLandingMode.Unspecified
                ? EventLandingMode.DropPod
                : requestedContext.LandingMode;
            return targets.ResolveLatest(request.Target, requestedLandingMode);
        }

        return targets.ResolveLatest(request.Target, EventLandingMode.DropPod);
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

    internal RejectGiftResponse Reject(RejectGiftRequest request)
    {
        RejectGiftResponse Deny(ProtocolErrorCode code, string message) => new RejectGiftResponse(
            ProtocolResponse.Reject(code, message), request.EventId, null, false);
        if (string.IsNullOrWhiteSpace(request.EventId) || string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId) || string.IsNullOrWhiteSpace(request.CurrentSnapshotId))
            return Deny(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Gift.RejectMissingFields"));
        using (var mutation = requests.EnterColony(request.AuthToken, request.UserId, request.ColonyId))
        {
            if (mutation.Rejection is { } rejection)
                return Deny(rejection.ErrorCode, rejection.Message ?? ServerLocalization.Text("Auth.IdentityDenied"));
            var gift = ledger.Find(request.EventId);
            if (gift is null || !EventVisibility.MatchesParty(gift.Target, request.UserId, request.ColonyId))
                return Deny(ProtocolErrorCode.EventNotFound, ServerLocalization.Text("Gift.NotFoundOrInvisible"));
            if (gift.Type != ServerEventType.ItemDelivery || gift.Payload is not ItemDeliveryEventPayload { Purpose: ItemDeliveryPurpose.Gift })
                return Deny(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Gift.RejectGiftOnly"));
            if (gift.RejectionPolicy != EventRejectionPolicy.RejectableByTarget)
                return Deny(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Gift.NotRejectable"));
            if (gift.Status is ServerEventStatus.AppliedToSnapshot or ServerEventStatus.Cancelled or ServerEventStatus.Failed)
                return Deny(ProtocolErrorCode.Conflict, ServerLocalization.Text("Gift.NotRejectable"));

            GiftReturnResult result;
            try
            {
                // Replays run the same source/return validation and preserve the original decision.
                result = ledger.RejectGiftAndCreateReturn(gift.EventId, DateTimeOffset.UtcNow, request.Reason,
                    presence.IsUserOnline(gift.Actor.UserId),
                    targets.ResolveLatest(new ProtocolIdentity(gift.Actor.UserId, gift.Actor.ColonyId, null), EventLandingMode.DropPod));
            }
            catch (InvalidOperationException)
            {
                return Deny(ProtocolErrorCode.Conflict, ServerLocalization.Text("Gift.NotRejectable"));
            }
            // Repeat the wake after a committed-but-unacknowledged attempt, even for consumed returns.
            notifications.SignalUsers([result.RejectedGift.Target.UserId, result.ReturnEvent.Target.UserId]);
            return new RejectGiftResponse(result.ReturnEventCreated
                ? ProtocolResponse.Ok(ServerLocalization.Text("Gift.RejectedReturnCreated"))
                : new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Gift.RejectedDuplicate")),
                result.RejectedGift.EventId, result.ReturnEvent.EventId, result.ReturnEventCreated);
        }
    }
}
