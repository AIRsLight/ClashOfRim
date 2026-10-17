using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Economy;
using AIRsLight.ClashOfRim.Network.Modules.Events;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Modules.Transfers;
using System.Globalization;
using Microsoft.Extensions.Logging;
using static AIRsLight.ClashOfRim.Network.Modules.Trades.TradeRules;
using static AIRsLight.ClashOfRim.Network.Modules.Events.EventProtocolMapper;
using static AIRsLight.ClashOfRim.Network.Modules.Transfers.ItemTransferMapper;
using static AIRsLight.ClashOfRim.Network.Modules.Snapshots.SnapshotTransactions;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.Trades;

internal sealed class TradeService(
    IAuthoritativeEventLedger ledger, SnapshotRequestAuthorization requests, ColonyAccessAuthorization colonies,
    SnapshotMutationCoordinator mutations, SnapshotTransactions transactions, PendingConfirmationReconciler pending,
    ItemTransferPackages packages, TradeSnapshotCommitter snapshotCommits, TradeLifecycle lifecycle, TradeDelivery delivery,
    AdminBaselineRegistry baseline, ServerPluginRegistry plugins, CompatibilityBaselineRegistry compatibility,
    OnlinePresenceRegistry presence, EventNotificationHub notifications, ILogger logger,
    Func<ClashOfRimServerConfiguration> configuration)
{
    internal TradeOrderFeeQuoteResponse QuoteTradeOrderFee(QuoteTradeOrderFeeRequest request)
    {
        if (request is null
            || request.Owner is null
            || string.IsNullOrWhiteSpace(request.Owner.UserId)
            || string.IsNullOrWhiteSpace(request.Owner.ColonyId)
            || ((request.OfferedThings is null || request.OfferedThings.Count == 0)
                && (request.RequestedThings is null || request.RequestedThings.Count == 0)))
        {
            return new TradeOrderFeeQuoteResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Trade.OrderMissingFields")),
                requiredFeeSilver: 0);
        }

        if (!configuration().TradeMarketplaceEnabled)
        {
            return new TradeOrderFeeQuoteResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, Format("Trade.Disabled")),
                requiredFeeSilver: 0);
        }

        if (!TryValidateInlinePawnPackages(request.OfferedThings ?? Array.Empty<ThingReferenceDto>(), out ProtocolResponse? pawnFailure)
            || !TryValidateInlinePawnPackages(request.RequestedThings ?? Array.Empty<ThingReferenceDto>(), out pawnFailure)
            || !TryValidateInlineThingPackages(request.OfferedThings ?? Array.Empty<ThingReferenceDto>(), out pawnFailure)
            || !TryValidateInlineThingPackages(request.RequestedThings ?? Array.Empty<ThingReferenceDto>(), out pawnFailure))
        {
            return new TradeOrderFeeQuoteResponse(
                pawnFailure ?? ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("PawnPackage.ParseFailed")),
                requiredFeeSilver: 0);
        }

        if (ContainsConcreteThingPackage(request.RequestedThings))
        {
            return new TradeOrderFeeQuoteResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, "trade request requirements cannot carry thing packages"),
                requiredFeeSilver: 0);
        }

        TradeFeeCalculationResult feeCalculation =
            baseline.BuildEffectiveTradeFeePolicy(configuration())
                .CalculateRequiredFeeResult(
                    request.OfferedThings ?? Array.Empty<ThingReferenceDto>(),
                    request.RequestedThings ?? Array.Empty<ThingReferenceDto>());
        if (!feeCalculation.Accepted)
        {
            return new TradeOrderFeeQuoteResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ServerRejected,
                    Format("Trade.MissingMarketValues", ("DEFS", string.Join(", ", feeCalculation.MissingMarketValueDefs)))),
                requiredFeeSilver: 0,
                feeCalculation.MissingMarketValueDefs);
        }

        return new TradeOrderFeeQuoteResponse(
            ProtocolResponse.Ok(Format("Trade.FeeQuoted")),
            feeCalculation.RequiredFeeSilver,
            feeCalculation.MissingMarketValueDefs);
    }

    private ProtocolResponse? ValidateTradeOrderConfiguration(CreateTradeOrderRequest request)
    {
        if (!configuration().TradeMarketplaceEnabled)
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, Format("Trade.Disabled"));
        var fee = baseline.BuildEffectiveTradeFeePolicy(configuration()).CalculateRequiredFeeResult(
            request.OfferedThings ?? Array.Empty<ThingReferenceDto>(), request.RequestedThings ?? Array.Empty<ThingReferenceDto>());
        if (!fee.Accepted)
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected,
                Format("Trade.MissingMarketValues", ("DEFS", string.Join(", ", fee.MissingMarketValueDefs))));
        return request.FeeSilver < fee.RequiredFeeSilver
            ? ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, Format("Trade.InsufficientFee",
                ("SUBMITTED", request.FeeSilver.ToString(CultureInfo.InvariantCulture)),
                ("REQUIRED", fee.RequiredFeeSilver.ToString(CultureInfo.InvariantCulture)))) : null;
    }

    private int CountOpenMarketTradeOrdersForOwner(ProtocolIdentity owner)
        => ledger.ListByTypeForActor(ServerEventType.Trade, owner.UserId, owner.ColonyId).Count(IsOpenMarketTradeOrderForOwner);

    internal EventCreationResponse CreateTradeOrderWithSnapshot(CreateTradeOrderWithSnapshotRequest transaction, byte[] payload, string? principalToken)
    {
        CreateTradeOrderRequest? request = transaction.TradeOrder;
        if (request is null
            || request.Owner is null
            || string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.Owner.UserId)
            || string.IsNullOrWhiteSpace(request.Owner.ColonyId)
            || ((request.OfferedThings is null || request.OfferedThings.Count == 0)
                && (request.RequestedThings is null || request.RequestedThings.Count == 0)))
        {
            return new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Trade.OrderTransactionMissingFields")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate);
        }

        if (transaction.ConfirmedSnapshot is null
            || string.IsNullOrWhiteSpace(transaction.ConfirmedSnapshot.SnapshotId))
        {
            return new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Trade.OrderTransactionMissingSnapshot")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate);
        }

        // Hash the typed request and actual bytes before taking the business gate; header credentials are not part of the body.
        string binding = SnapshotRequestBinding.Hash(transaction, payload);
        using (var recovery = requests.EnterPrincipal(principalToken, request.Owner))
        {
            if (recovery.Rejection is { } denied)
                return new EventCreationResponse(denied, null, ProtocolDeliverySemantics.OnlineImmediate);
            lifecycle.ApplyRecoveredTradeOrderExpirations(DateTimeOffset.UtcNow);
            AuthoritativeEvent? existingEvent = ledger.FindByIdempotencyKey(request.IdempotencyKey);
            if (existingEvent is not null)
                return TradeOrderReplayResponse(request, transaction.ConfirmedSnapshot.SnapshotId!, existingEvent, binding);
            if (pending.ReconcileSnapshot(request.Owner.UserId, request.Owner.ColonyId ?? string.Empty, DateTimeOffset.UtcNow) is { } pendingRejection)
                return new EventCreationResponse(pendingRejection, null, ProtocolDeliverySemantics.OnlineImmediate);
        }

        if (!configuration().TradeMarketplaceEnabled)
            return new EventCreationResponse(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected,
                Format("Trade.Disabled")), null, ProtocolDeliverySemantics.OnlineImmediate);

        if (!TryValidateInlinePawnPackages(request.OfferedThings ?? Array.Empty<ThingReferenceDto>(), out ProtocolResponse? pawnFailure)
            || !TryValidateInlinePawnPackages(request.RequestedThings ?? Array.Empty<ThingReferenceDto>(), out pawnFailure)
            || !TryValidateInlineThingPackages(request.OfferedThings ?? Array.Empty<ThingReferenceDto>(), out pawnFailure)
            || !TryValidateInlineThingPackages(request.RequestedThings ?? Array.Empty<ThingReferenceDto>(), out pawnFailure))
        {
            return new EventCreationResponse(
                pawnFailure ?? ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("PawnPackage.ParseFailed")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate);
        }

        if (ValidateTradeOrderConfiguration(request) is { } configurationFailure)
            return new EventCreationResponse(configurationFailure, null, ProtocolDeliverySemantics.OnlineImmediate);

        int maxOpenOrders = configuration().MaxOpenTradeOrdersPerOwner;
        if (maxOpenOrders > 0)
        {
            int openOrderCount = CountOpenMarketTradeOrdersForOwner(request.Owner);
            if (openOrderCount >= maxOpenOrders)
            {
                return new EventCreationResponse(
                    ProtocolResponse.Reject(
                        ProtocolErrorCode.ServerRejected,
                        Format(
                            "Trade.OpenOrderLimitReached",
                            ("CURRENT", openOrderCount.ToString(CultureInfo.InvariantCulture)),
                            ("MAX", maxOpenOrders.ToString(CultureInfo.InvariantCulture)))),
                    eventId: null,
                    ProtocolDeliverySemantics.OnlineImmediate);
            }

        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        SnapshotUploadResult upload = transactions.PrepareTransactionSnapshot(
            request.Owner.UserId,
            request.Owner.ColonyId ?? string.Empty,
            transaction.ConfirmedSnapshot.SnapshotId!,
            transaction.ConfirmedSnapshot,
            payload,
            nowUtc);
        using (var recovery = requests.EnterPrincipal(principalToken, request.Owner))
        {
            if (recovery.Rejection is { } denied)
                return new EventCreationResponse(denied, null, ProtocolDeliverySemantics.OnlineImmediate);
            lifecycle.ApplyRecoveredTradeOrderExpirations(DateTimeOffset.UtcNow);
            AuthoritativeEvent? existing = ledger.FindByIdempotencyKey(request.IdempotencyKey);
            if (existing is not null)
                return TradeOrderReplayResponse(request, transaction.ConfirmedSnapshot.SnapshotId!, existing, binding);
            if (pending.ReconcileSnapshot(request.Owner.UserId, request.Owner.ColonyId ?? string.Empty, DateTimeOffset.UtcNow) is { } pendingRejection)
                return new EventCreationResponse(pendingRejection, null, ProtocolDeliverySemantics.OnlineImmediate);
            if (!upload.Accepted || upload.AcceptedSnapshot is null)
            {
                return new EventCreationResponse(
                    ToProtocolResponse(upload),
                    eventId: null,
                    ProtocolDeliverySemantics.OnlineImmediate);
            }

            if (ValidateTradeOrderConfiguration(request) is { } finalConfigurationFailure)
                return new EventCreationResponse(finalConfigurationFailure, null, ProtocolDeliverySemantics.OnlineImmediate);
            maxOpenOrders = configuration().MaxOpenTradeOrdersPerOwner;
            if (maxOpenOrders > 0 && CountOpenMarketTradeOrdersForOwner(request.Owner) >= maxOpenOrders)
                return new EventCreationResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict,
                    "Open trade order limit reached during preparation."), null, ProtocolDeliverySemantics.OfflinePending);
            upload = transactions.CreateSnapshotReceiver().CommitPrepared(
                new SnapshotUploadContext(request.Owner.UserId, request.Owner.ColonyId!, transaction.ConfirmedSnapshot.SnapshotId!, null, upload.SnapshotUploadKind),
                ProtocolDtoMapper.ToSaveSnapshotPackage(transaction.ConfirmedSnapshot, payload), upload, storeAccepted: false);
            if (!upload.Accepted || upload.AcceptedSnapshot is null)
                return new EventCreationResponse(ToProtocolResponse(upload), null, ProtocolDeliverySemantics.OfflinePending);
            string commitId = Guid.NewGuid().ToString("N");
            string acceptedSnapshotId = upload.AcceptedSnapshot.Identity.SnapshotId
                ?? transaction.ConfirmedSnapshot.SnapshotId!;
            if (!packages.TryStore(
                    new ProtocolIdentity(request.Owner.UserId, request.Owner.ColonyId, acceptedSnapshotId),
                    "trade-order-offer:" + commitId,
                    request.OfferedThings ?? Array.Empty<ThingReferenceDto>(),
                    out IReadOnlyList<ThingReferenceDto> offeredThings,
                    out pawnFailure)
                || !packages.TryStore(
                    new ProtocolIdentity(request.Owner.UserId, request.Owner.ColonyId, acceptedSnapshotId),
                    "trade-order-request:" + commitId,
                    request.RequestedThings ?? Array.Empty<ThingReferenceDto>(),
                    out IReadOnlyList<ThingReferenceDto> requestedThings,
                    out pawnFailure))
            {
                return new EventCreationResponse(
                    pawnFailure ?? ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("PawnPackage.ParseFailed")),
                    eventId: null,
                    ProtocolDeliverySemantics.OnlineImmediate);
            }

            var ownerEndpoint = delivery.ResolveTradeDeliveryEndpoint(
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

            AuthoritativeEvent tradeEvent = AuthoritativeEventFactory.CreateForLedger(ledger,
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
                    PostagePaidByAcceptor: false, CreationRequestHash: binding),
                nowUtc,
                targetContext);

            tradeEvent = tradeEvent with { EventId = "trade-order:" + commitId };
            snapshotCommits.Commit(ProtocolDtoMapper.ToSaveSnapshotPackage(transaction.ConfirmedSnapshot, payload), upload,
                new TradeSnapshotMutation(new LedgerBatch(new[] { tradeEvent }, Array.Empty<LedgerStatusChange>()),
                    presence.ListOnlineUsers().Where(user => user != request.Owner.UserId).ToArray()));
            var append = new LedgerAppendResult(tradeEvent, Created: true);
            EventCreationResponse created = ToEventCreationResponse(
                append,
                ProtocolDeliverySemantics.OfflinePending,
                Format("Trade.OrderCreated"));
            return new EventCreationResponse(
                created.Result,
                created.EventId,
                created.DeliverySemantics,
                acceptedSnapshotId,
                upload.AcceptedSnapshot.Envelope.NextLineageToken);
        }

    }

    internal FulfillTradeOrderResponse FulfillTradeOrderWithSnapshot(FulfillTradeOrderWithSnapshotRequest transaction, byte[] payload, string? principalToken)
    {
        FulfillTradeOrderRequest? request = transaction.Fulfillment;
        if (request is null
            || transaction.ConfirmedSnapshot is null
            || string.IsNullOrWhiteSpace(transaction.ConfirmedSnapshot.SnapshotId))
        {
            return new FulfillTradeOrderResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Trade.FulfillmentTransactionMissingFields")),
                request?.TradeEventId,
                request?.AcceptedMemoEventId,
                exchangeEventId: null,
                exchangeCreated: false,
                Array.Empty<ThingReferenceDto>(),
                Array.Empty<string>(),
                tradeStatus: string.Empty);
        }

        ProtocolResponse? validation = ValidateTradeFulfillmentRequest(request);
        if (validation is not null)
        {
            return new FulfillTradeOrderResponse(
                validation,
                request.TradeEventId,
                request.AcceptedMemoEventId,
                exchangeEventId: null,
                exchangeCreated: false,
                Array.Empty<ThingReferenceDto>(),
                Array.Empty<string>(),
                tradeStatus: string.Empty);
        }

        TradeFulfillmentMode fulfillmentMode = ResolveRequestedTradeFulfillmentMode(request.FulfillmentMode);
        if (fulfillmentMode is not TradeFulfillmentMode.SelfDelivery and not TradeFulfillmentMode.ServerDropPod)
        {
            return new FulfillTradeOrderResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Trade.InvalidFulfillmentMode")),
                request.TradeEventId,
                request.AcceptedMemoEventId,
                exchangeEventId: null,
                exchangeCreated: false,
                Array.Empty<ThingReferenceDto>(),
                Array.Empty<string>(),
                tradeStatus: string.Empty);
        }

        // Hash the typed request and actual bytes before taking the business gate; header credentials are not part of the body.
        string binding = SnapshotRequestBinding.Hash(transaction, payload);
        using (var recovery = requests.EnterPrincipal(principalToken, request.Acceptor))
        {
            if (recovery.Rejection is { } denied)
                return TradeFulfillmentFailure(request, denied);
            lifecycle.ApplyRecoveredTradeOrderExpirations(DateTimeOffset.UtcNow);
            AuthoritativeEvent? existing = ledger.FindByIdempotencyKey(request.IdempotencyKey);
            if (existing is not null)
                return TradeFulfillmentReplayResponse(request, transaction.ConfirmedSnapshot.SnapshotId!, existing, binding);
            if (pending.ReconcileSnapshot(request.Acceptor.UserId, request.Acceptor.ColonyId ?? string.Empty, DateTimeOffset.UtcNow) is { } pendingRejection)
                return TradeFulfillmentFailure(request, pendingRejection);
        }

        AuthoritativeEvent? tradeOrder = ledger.Find(request.TradeEventId);
        AuthoritativeEvent? acceptedMemo = string.IsNullOrWhiteSpace(request.AcceptedMemoEventId)
            ? null
            : ledger.Find(request.AcceptedMemoEventId);
        if (tradeOrder is null
            || !IsOpenMarketTradeOrder(tradeOrder, request.Acceptor.UserId)
            || tradeOrder.Payload is not TradeEventPayload orderPayload
            || (fulfillmentMode == TradeFulfillmentMode.SelfDelivery
                && (acceptedMemo is null
                    || !IsMatchingActiveTradeAcceptanceMemo(acceptedMemo, tradeOrder.EventId, request.Acceptor.UserId, request.Acceptor.ColonyId))))
        {
            return new FulfillTradeOrderResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, Format("Trade.OrderOrMemoUnavailable")),
                request.TradeEventId,
                request.AcceptedMemoEventId,
                exchangeEventId: null,
                exchangeCreated: false,
                Array.Empty<ThingReferenceDto>(),
                Array.Empty<string>(),
                tradeOrder?.Status.ToString() ?? string.Empty);
        }

        IReadOnlyList<ThingReferenceDto> requiredThings = orderPayload.RequestedItems.Select(ToThingReferenceDto).ToList();
        if (!TryValidateInlinePawnPackages(request.DeliveredThings, out ProtocolResponse? pawnFailure)
            || !TryValidateInlineThingPackages(request.DeliveredThings, out pawnFailure))
        {
            return new FulfillTradeOrderResponse(
                pawnFailure ?? ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("PawnPackage.ParseFailed")),
                tradeOrder.EventId,
                acceptedMemo?.EventId,
                exchangeEventId: null,
                exchangeCreated: false,
                orderPayload.OfferedItems.Select(ToThingReferenceDto).ToList(),
                Array.Empty<string>(),
                tradeOrder.Status.ToString());
        }

        if (!TradeThingRequirementMatcher.Satisfies(
                requiredThings,
                request.DeliveredThings,
                out IReadOnlyList<string> missingRequirements,
                plugins.ActiveTradeThingMetadataMatchers(compatibility.Current)))
        {
            return new FulfillTradeOrderResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    Format("Trade.MissingRequirements", ("MISSING", string.Join("; ", missingRequirements)))),
                tradeOrder.EventId,
                acceptedMemo?.EventId,
                exchangeEventId: null,
                exchangeCreated: false,
                orderPayload.OfferedItems.Select(ToThingReferenceDto).ToList(),
                missingRequirements,
                tradeOrder.Status.ToString());
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        SnapshotUploadResult upload = transactions.PrepareTransactionSnapshot(
            request.Acceptor.UserId,
            request.Acceptor.ColonyId ?? string.Empty,
            transaction.ConfirmedSnapshot.SnapshotId!,
            transaction.ConfirmedSnapshot,
            payload,
            nowUtc);
        using (var recovery = requests.EnterPrincipal(principalToken, request.Acceptor))
        {
            if (recovery.Rejection is { } denied)
                return TradeFulfillmentFailure(request, denied);
            lifecycle.ApplyRecoveredTradeOrderExpirations(DateTimeOffset.UtcNow);
            AuthoritativeEvent? existing = ledger.FindByIdempotencyKey(request.IdempotencyKey);
            if (existing is not null)
                return TradeFulfillmentReplayResponse(request, transaction.ConfirmedSnapshot.SnapshotId!, existing, binding);
            if (pending.ReconcileSnapshot(request.Acceptor.UserId, request.Acceptor.ColonyId ?? string.Empty, DateTimeOffset.UtcNow) is { } pendingRejection)
                return TradeFulfillmentFailure(request, pendingRejection);
            if (!upload.Accepted || upload.AcceptedSnapshot is null)
            {
                return new FulfillTradeOrderResponse(
                    ToProtocolResponse(upload),
                    tradeOrder.EventId,
                    acceptedMemo?.EventId,
                    exchangeEventId: null,
                    exchangeCreated: false,
                    orderPayload.OfferedItems.Select(ToThingReferenceDto).ToList(),
                    Array.Empty<string>(),
                    tradeOrder.Status.ToString());
            }

            tradeOrder = ledger.Find(request.TradeEventId);
            acceptedMemo = string.IsNullOrWhiteSpace(request.AcceptedMemoEventId) ? null : ledger.Find(request.AcceptedMemoEventId);
            if (tradeOrder is null || !IsOpenMarketTradeOrder(tradeOrder, request.Acceptor.UserId)
                || tradeOrder.Payload is not TradeEventPayload currentPayload
                || (fulfillmentMode == TradeFulfillmentMode.SelfDelivery && acceptedMemo is null)
                || (TradeFulfillmentMemoId(request) is not null && (acceptedMemo is null
                    || !IsMatchingActiveTradeAcceptanceMemo(acceptedMemo, tradeOrder.EventId, request.Acceptor.UserId, request.Acceptor.ColonyId))))
                return TradeFulfillmentFailure(request, ProtocolResponse.Reject(ProtocolErrorCode.Conflict, Format("Trade.OrderOrMemoUnavailable")));
            orderPayload = currentPayload;
            upload = transactions.CreateSnapshotReceiver().CommitPrepared(
                new SnapshotUploadContext(request.Acceptor.UserId, request.Acceptor.ColonyId!, transaction.ConfirmedSnapshot.SnapshotId!, null, upload.SnapshotUploadKind),
                ProtocolDtoMapper.ToSaveSnapshotPackage(transaction.ConfirmedSnapshot, payload), upload, storeAccepted: false);
            if (!upload.Accepted || upload.AcceptedSnapshot is null)
                return TradeFulfillmentFailure(request, ToProtocolResponse(upload));
            string commitId = Guid.NewGuid().ToString("N");
            string acceptedSnapshotId = upload.AcceptedSnapshot.Identity.SnapshotId
                ?? transaction.ConfirmedSnapshot.SnapshotId!;
            if (!packages.TryStore(
                    new ProtocolIdentity(request.Acceptor.UserId, request.Acceptor.ColonyId, acceptedSnapshotId),
                    "trade-fulfill-delivered:" + commitId,
                    request.DeliveredThings,
                    out IReadOnlyList<ThingReferenceDto> deliveredThings,
                    out pawnFailure))
            {
                return new FulfillTradeOrderResponse(
                    pawnFailure ?? ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("PawnPackage.ParseFailed")),
                    tradeOrder.EventId,
                    acceptedMemo?.EventId,
                    exchangeEventId: null,
                    exchangeCreated: false,
                    orderPayload.OfferedItems.Select(ToThingReferenceDto).ToList(),
                    Array.Empty<string>(),
                    tradeOrder.Status.ToString());
            }

            var acceptorParty = ToEventParty(request.Acceptor);
            TradeStage exchangeStage = fulfillmentMode == TradeFulfillmentMode.ServerDropPod
                ? TradeStage.ServerDropPodExchange
                : TradeStage.SelfDeliveryExchange;
            AuthoritativeEvent exchangeEvent = AuthoritativeEventFactory.CreateForLedger(ledger,
                ServerEventType.Trade,
                acceptorParty,
                tradeOrder.Actor,
                request.IdempotencyKey,
                targetOnline: presence.IsUserOnline(tradeOrder.Actor.UserId),
                new TradeEventPayload(
                    tradeOrder.EventId,
                    exchangeStage,
                    orderPayload.OfferedItems,
                    deliveredThings.Select(thing => ToEventThingReference(thing, acceptedSnapshotId)).ToList(),
                    orderPayload.FeeSilver,
                    AcceptedByUserId: request.Acceptor.UserId,
                    AcceptedMemoEventId: acceptedMemo?.EventId,
                    FulfillmentMode: fulfillmentMode,
                    PostagePaidByAcceptor: fulfillmentMode == TradeFulfillmentMode.ServerDropPod, CreationRequestHash: binding),
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
            IReadOnlyList<AuthoritativeEvent> activeMemos = FindActiveTradeAcceptanceMemos(ledger, tradeOrder.EventId);
            var changes = activeMemos.Select(memo => new LedgerStatusChange(memo.EventId, memo.Status,
                memo.EventId == acceptedMemo?.EventId ? ServerEventStatus.AppliedToSnapshot : ServerEventStatus.Cancelled)).ToList();
            changes.Insert(0, new LedgerStatusChange(tradeOrder.EventId, tradeOrder.Status, ServerEventStatus.AppliedToSnapshot));
            string fulfillmentKey = acceptedMemo?.EventId ?? request.Acceptor.UserId + ":" + request.Acceptor.ColonyId;
            AuthoritativeEvent ownerDelivery = delivery.CreateTradeItemDeliveryEvent($"trade-completed-owner-delivery:{tradeOrder.EventId}:{fulfillmentKey}", acceptorParty, tradeOrder.Actor,
                deliveredThings.Select(thing => ToEventThingReference(thing, acceptedSnapshotId)).ToList(),
                ItemDeliveryPurpose.TradeCompletedOwnerDelivery, ResolveTradeCompletedOwnerDeliveryTargetContext(tradeOrder, fulfillmentMode), nowUtc);
            var events = new List<AuthoritativeEvent> { exchangeEvent, ownerDelivery };
            AuthoritativeEvent? acceptorDelivery = null;
            if (fulfillmentMode == TradeFulfillmentMode.ServerDropPod)
            {
                acceptorDelivery = delivery.CreateTradeItemDeliveryEvent($"trade-completed-acceptor-delivery:{tradeOrder.EventId}:{fulfillmentKey}", tradeOrder.Actor, acceptorParty,
                    orderPayload.OfferedItems, ItemDeliveryPurpose.TradeCompletedAcceptorDelivery,
                    delivery.ResolveTradeTargetContext(request.Acceptor.UserId, request.Acceptor.ColonyId!, acceptedSnapshotId,
                        EventLandingMode.DropPod, upload.AcceptedSnapshot), nowUtc);
                events.Add(acceptorDelivery);
            }

            snapshotCommits.Commit(ProtocolDtoMapper.ToSaveSnapshotPackage(transaction.ConfirmedSnapshot, payload), upload,
                new TradeSnapshotMutation(new LedgerBatch(events, changes), activeMemos.Select(memo => memo.Target.UserId)
                    .Append(tradeOrder.Actor.UserId).Append(request.Acceptor.UserId).Distinct(StringComparer.Ordinal).ToArray()));
            return new FulfillTradeOrderResponse(
                ProtocolResponse.Ok(Format("Trade.FulfillmentTransactionCreated")), tradeOrder.EventId, acceptedMemo?.EventId,
                exchangeEvent.EventId, true, orderPayload.OfferedItems.Select(ToThingReferenceDto).ToList(), Array.Empty<string>(),
                ServerEventStatus.AppliedToSnapshot.ToString(), acceptorDelivery?.EventId, ownerDelivery.EventId,
                acceptedSnapshotId, upload.AcceptedSnapshot.Envelope.NextLineageToken);
        }

    }
    internal CloseTradeOrderResponse CancelTradeOrder(CloseTradeOrderRequest request)
    {
        CloseTradeOrderResponse Reject(ProtocolResponse rejection) => new CloseTradeOrderResponse(
            rejection, request.TradeEventId, string.Empty, 0);
        if (request.Owner is null)
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, Format("Auth.IdentityDenied")));
        using (var mutation = requests.EnterColony(request.AuthToken, request.Owner.UserId,
            request.Owner.ColonyId ?? string.Empty))
        {
            if (mutation.Rejection is { } rejection) return Reject(rejection);
            lifecycle.ApplyRecoveredTradeOrderExpirations(DateTimeOffset.UtcNow);

            ProtocolResponse? validation = ValidateTradeCloseRequest(request);
            if (validation is not null)
            {
                return new CloseTradeOrderResponse(
                    validation,
                    request.TradeEventId,
                    string.Empty,
                    notifiedAcceptorCount: 0);
            }

            AuthoritativeEvent? tradeOrder = ledger.Find(request.TradeEventId);
            var cancellation = ledger.FindByIdempotencyKey(request.IdempotencyKey);
            if (cancellation?.Payload is TradeEventPayload { Stage: TradeStage.AcceptanceCancelled })
                return ReplayTradeMemoCancellation(request, tradeOrder, cancellation, duplicate: true);
            if (tradeOrder is not null
                && tradeOrder.Type == ServerEventType.Trade
                && tradeOrder.Payload is TradeEventPayload { Stage: TradeStage.MarketOrder }
                && (!string.Equals(tradeOrder.Actor.UserId, request.Owner.UserId, StringComparison.Ordinal)
                    || !string.Equals(tradeOrder.Actor.ColonyId, request.Owner.ColonyId, StringComparison.Ordinal)))
            {
                return CancelTradeAcceptanceMemo(request, tradeOrder);
            }

            return CancelOwnedTradeOrderLocked(request);
        }

    }

    // Called under the authenticated, recovered and validated CancelTradeOrder scope.
    private CloseTradeOrderResponse CancelOwnedTradeOrderLocked(CloseTradeOrderRequest request)
    {
        AuthoritativeEvent? tradeOrder = ledger.Find(request.TradeEventId);
        if (tradeOrder is null
            || tradeOrder.Type != ServerEventType.Trade
            || tradeOrder.Payload is not TradeEventPayload payload
            || payload.Stage != TradeStage.MarketOrder
            || !string.Equals(tradeOrder.Actor.UserId, request.Owner.UserId, StringComparison.Ordinal)
            || !string.Equals(tradeOrder.Actor.ColonyId, request.Owner.ColonyId, StringComparison.Ordinal))
        {
            return new CloseTradeOrderResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, Format("Trade.CloseNotOwner")),
                request.TradeEventId,
                string.Empty,
                notifiedAcceptorCount: 0);
        }

        bool duplicate = tradeOrder.Status == ServerEventStatus.Cancelled;
        if (!duplicate && !IsOpenMarketTradeOrderForOwner(tradeOrder))
            return new CloseTradeOrderResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict,
                Format("Trade.NotOpen")), tradeOrder.EventId, tradeOrder.Status.ToString(), 0);
        CloseTradeOrderResponse Conflict() => new CloseTradeOrderResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict,
            "Trade cancellation conflicts with its memos, fulfillment or escrow return."), tradeOrder.EventId, tradeOrder.Status.ToString(), 0);
        if (!lifecycle.TryPrepareOwnedTradeCancellation(tradeOrder, out var activeMemos, out var batch)) return Conflict();
        try { if (batch is not null) ledger.ApplyBatch(batch); }
        catch (InvalidOperationException) { return Conflict(); }
        IReadOnlyList<string> notifiedUsers = activeMemos
            .Select(memo => memo.Target.UserId)
            .Where(userId => !string.IsNullOrWhiteSpace(userId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        // Cancellation creates an automatic item-return event for the owner too.
        // Without this signal a connected seller waits for an unrelated event or reconnect.
        notifications.SignalUsers(notifiedUsers.Append(tradeOrder.Actor.UserId));

        return new CloseTradeOrderResponse(
            new ProtocolResponse(true, duplicate ? ProtocolErrorCode.DuplicateRequest : ProtocolErrorCode.None, Format("Trade.CancelledWithNotifications")),
            tradeOrder.EventId,
            ServerEventStatus.Cancelled.ToString(),
            notifiedUsers.Count);
    }

    internal AcceptTradeOrderResponse AcceptTradeOrder(AcceptTradeOrderRequest request)
    {
        AcceptTradeOrderResponse Reject(ProtocolResponse rejection) => new AcceptTradeOrderResponse(
            rejection, request.TradeEventId, null, false);
        if (request.Acceptor is null)
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, Format("Auth.IdentityDenied")));
        using (var mutation = requests.EnterColony(request.AuthToken, request.Acceptor.UserId,
            request.Acceptor.ColonyId ?? string.Empty))
        {
            if (mutation.Rejection is { } rejection) return Reject(rejection);
            lifecycle.ApplyRecoveredTradeOrderExpirations(DateTimeOffset.UtcNow);

            ProtocolResponse? validation = ValidateTradeAcceptanceRequest(request);
            if (validation is not null)
            {
                return new AcceptTradeOrderResponse(
                    validation,
                    request.TradeEventId,
                    memoEventId: null,
                    memoCreated: false);
            }

            AuthoritativeEvent? tradeOrder = ledger.Find(request.TradeEventId);
            if (tradeOrder is null || !IsOpenMarketTradeOrder(tradeOrder, request.Acceptor.UserId))
            {
                return new AcceptTradeOrderResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, Format("Trade.OrderNotAcceptable")),
                    request.TradeEventId,
                    memoEventId: null,
                    memoCreated: false);
            }

            AcceptTradeOrderResponse Conflict() => new AcceptTradeOrderResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, "Trade acceptance conflicts with an existing memo or idempotency key."),
                request.TradeEventId, null, false);
            var byKey = ledger.FindByIdempotencyKey(request.IdempotencyKey);
            if (byKey is not null && !MatchesTradeAcceptance(byKey, tradeOrder, request)) return Conflict();

            // A second key may retry the same active acceptance, but cannot mask another key's owner.
            var candidates = FindActiveAcceptanceCandidates(ledger, tradeOrder.EventId, request.Acceptor);
            if (candidates.Length > 1 || candidates.Any(item => !MatchesTradeAcceptance(item, tradeOrder, request))) return Conflict();
            AuthoritativeEvent? existingMemo = candidates.SingleOrDefault();
            if (existingMemo is not null)
            {
                return new AcceptTradeOrderResponse(
                    new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Trade.OrderAcceptedDuplicate")),
                    request.TradeEventId,
                    existingMemo.EventId,
                    memoCreated: false);
            }

            if (request.PostagePaidByAcceptor)
            {
                TradePostageQuoteDto postage = delivery.BuildTradePostageQuote(
                    tradeOrder,
                    request.Acceptor.UserId,
                    request.Acceptor.ColonyId ?? string.Empty,
                    request.Acceptor.SnapshotId ?? string.Empty);
                if (!postage.Reachable || postage.PostageSilver is null)
                {
                    return new AcceptTradeOrderResponse(
                        ProtocolResponse.Reject(
                            ProtocolErrorCode.ServerRejected,
                            Format("Trade.DropPodUnavailable", ("STATUS", postage.Status))),
                        request.TradeEventId,
                        memoEventId: null,
                        memoCreated: false);
                }

            }

            var acceptorParty = ToEventParty(request.Acceptor);
            TradeEventPayload orderPayload = (TradeEventPayload)tradeOrder.Payload;
            AuthoritativeEvent memoEvent = AuthoritativeEventFactory.Create(
                ServerEventType.Trade,
                acceptorParty,
                acceptorParty,
                request.IdempotencyKey,
                targetOnline: presence.IsUserOnline(request.Acceptor.UserId),
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

            LedgerAppendResult append;
            try
            {
                memoEvent = AuthoritativeEventFactory.ResolvePersistedIdentity(ledger, memoEvent);
                append = ledger.Append(memoEvent);
            }

            catch (InvalidOperationException) { return Conflict(); }
            // Append protects identity; business equivalence remains the trade service's responsibility.
            if (!MatchesTradeAcceptance(append.Event, tradeOrder, request)) return Conflict();
            EventDiagnostics.LogAppend(logger, append, "trade-order-accepted-memo");

            return new AcceptTradeOrderResponse(
                append.Created
                    ? ProtocolResponse.Ok(ServerLocalization.Text("Trade.OrderAccepted"))
                    : new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Trade.OrderAcceptedDuplicate")),
                tradeOrder.EventId,
                append.Event.EventId,
                append.Created);
        }

    }

    private CloseTradeOrderResponse CancelTradeAcceptanceMemo(CloseTradeOrderRequest request, AuthoritativeEvent order)
    {
        var byKey = ledger.FindByIdempotencyKey(request.IdempotencyKey);
        if (byKey is not null) return ReplayTradeMemoCancellation(request, order, byKey, duplicate: true);
        if (!IsOpenMarketTradeOrderForOwner(order)) return MemoCancellationConflict(request);
        var candidates = FindActiveAcceptanceCandidates(ledger, order.EventId, request.Owner);
        if (candidates.Length == 0)
            return new CloseTradeOrderResponse(ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound,
                Format("Trade.AcceptanceMemoNotFound")), order.EventId, string.Empty, 0);
        if (candidates.Length != 1) return MemoCancellationConflict(request);
        var memo = candidates[0]; var payload = (TradeEventPayload)memo.Payload;
        var acceptance = new AcceptTradeOrderRequest(memo.IdempotencyKey, order.EventId, request.Owner, payload.PostagePaidByAcceptor);
        if (!MatchesTradeAcceptance(memo, order, acceptance)) return MemoCancellationConflict(request);

        var receipt = AuthoritativeEventFactory.Create(ServerEventType.Trade, memo.Actor, order.Actor, request.IdempotencyKey, false,
            payload with { Stage = TradeStage.AcceptanceCancelled, AcceptedMemoEventId = memo.EventId }, DateTimeOffset.UtcNow,
            order.TargetContext) with { Status = ServerEventStatus.Cancelled };
        try
        {
            ledger.ApplyBatch(new([receipt], [new(memo.EventId, memo.Status, ServerEventStatus.Cancelled, ExpectedEvent: memo)]));
        }

        catch (InvalidOperationException) { return MemoCancellationConflict(request); }
        return ReplayTradeMemoCancellation(request, order, receipt, duplicate: false);
    }

    private CloseTradeOrderResponse ReplayTradeMemoCancellation(CloseTradeOrderRequest request, AuthoritativeEvent? order, AuthoritativeEvent receipt, bool duplicate)
    {
        if (order?.Type != ServerEventType.Trade || order.Payload is not TradeEventPayload { Stage: TradeStage.MarketOrder }
            || receipt.Type != ServerEventType.Trade || receipt.Status != ServerEventStatus.Cancelled
            || receipt.IdempotencyKey != request.IdempotencyKey
            || !AuthoritativeEventFactory.IsKnownEventId(receipt.Type, receipt.IdempotencyKey, receipt.EventId)
            || receipt.Payload is not TradeEventPayload { Stage: TradeStage.AcceptanceCancelled } payload
            || payload.TradeId != order.EventId || receipt.Target != order.Actor
            || receipt.Actor.UserId != request.Owner.UserId || receipt.Actor.ColonyId != request.Owner.ColonyId
            || payload.AcceptedMemoEventId is null)
            return MemoCancellationConflict(request);
        var memo = ledger.Find(payload.AcceptedMemoEventId);
        if (memo?.Status != ServerEventStatus.Cancelled || memo.Payload is not TradeEventPayload original
            || memo.Actor != receipt.Actor
            || !MatchesTradeAcceptance(memo with { Status = ServerEventStatus.Recorded }, order,
                new AcceptTradeOrderRequest(memo.IdempotencyKey, order.EventId, request.Owner, original.PostagePaidByAcceptor))
            || !SameTradeEvidence(payload, original with
                { Stage = TradeStage.AcceptanceCancelled, AcceptedMemoEventId = memo.EventId })
            || !SameTradeEvidence(receipt.TargetContext, order.TargetContext))
            return MemoCancellationConflict(request);

        // The durable receipt chooses the original memo even after another acceptance or an order closure.
        string[] users = [order.Actor.UserId, request.Owner.UserId];
        notifications.SignalUsers(users);
        return new CloseTradeOrderResponse(new ProtocolResponse(true,
            duplicate ? ProtocolErrorCode.DuplicateRequest : ProtocolErrorCode.None, Format("Trade.AcceptanceMemoCancelled")),
            order.EventId, ServerEventStatus.Cancelled.ToString(), users.Distinct(StringComparer.Ordinal).Count());
    }

    private CloseTradeOrderResponse MemoCancellationConflict(CloseTradeOrderRequest request) => new CloseTradeOrderResponse(
        ProtocolResponse.Reject(ProtocolErrorCode.Conflict, "Trade memo cancellation conflicts with its original acceptance."),
        request.TradeEventId, string.Empty, 0);
    internal CloseTradeOrderResponse CompleteTradeOrder(CloseTradeOrderRequest request)
    {
        ProtocolResponse? AuthorizeAndValidate()
        {
            string failure = Format("Auth.IdentityDenied");
            if (request.Owner is null || !colonies.TryAuthorize(request.AuthToken, request.Owner.UserId,
                    request.Owner.ColonyId ?? string.Empty, null, null, DateTimeOffset.UtcNow, out failure))
                return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, failure);
            // Invalid legacy completion requests must not trigger journal recovery.
            return ValidateTradeCloseRequest(request);
        }

        using (var mutation = mutations.Enter(AuthorizeAndValidate))
        {
            CloseTradeOrderResponse Reject(ProtocolErrorCode code, string message) => new CloseTradeOrderResponse(
                ProtocolResponse.Reject(code, message), request.TradeEventId, string.Empty, 0);
            if (mutation.Rejection is { } rejection)
                return new CloseTradeOrderResponse(rejection, request.TradeEventId, string.Empty, 0);
            var order = ledger.Find(request.TradeEventId);
            if (order?.Type != ServerEventType.Trade || order.Payload is not TradeEventPayload { Stage: TradeStage.MarketOrder }
                || !EventVisibility.MatchesParty(order.Actor, request.Owner.UserId, request.Owner.ColonyId!))
                return Reject(ProtocolErrorCode.EventNotFound, Format("Trade.CloseNotOwner"));
            // Completion belongs to the snapshot fulfillment transaction. This legacy request cannot spend escrow.
            if (!TryGetCompletedTradeParticipants(ledger, order, out var participants))
                return Reject(ProtocolErrorCode.Conflict, "Trade completion requires a committed snapshot fulfillment and matching delivery evidence.");
            notifications.SignalUsers(participants.Append(order.Actor.UserId));
            return new CloseTradeOrderResponse(new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest,
                Format("Trade.Completed")), order.EventId, order.Status.ToString(), participants.Count);
        }

    }

    private EventCreationResponse TradeOrderReplayResponse(CreateTradeOrderRequest request,
        string snapshotId, AuthoritativeEvent existing, string binding)
    {
        if (existing.Payload is not TradeEventPayload payload || payload.CreationRequestHash != binding
            || !TradeSnapshotReplayPolicy.Matches(existing, request.Owner.UserId, request.Owner.ColonyId, snapshotId, order: true))
            return new EventCreationResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict,
                "Trade idempotency key belongs to another operation."), null, ProtocolDeliverySemantics.OfflinePending);
        return new EventCreationResponse(
            new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, Format("Trade.OrderTransactionDuplicate")),
            existing.EventId, ProtocolDeliverySemantics.OfflinePending, snapshotId,
            transactions.FindSnapshotNextLineageToken(request.Owner.UserId, request.Owner.ColonyId!, snapshotId));
    }

    private FulfillTradeOrderResponse TradeFulfillmentFailure(FulfillTradeOrderRequest request, ProtocolResponse failure)
        => new FulfillTradeOrderResponse(failure, request.TradeEventId, request.AcceptedMemoEventId,
            null, false, Array.Empty<ThingReferenceDto>(), Array.Empty<string>(), string.Empty);

    private FulfillTradeOrderResponse TradeFulfillmentReplayResponse(FulfillTradeOrderRequest request,
        string snapshotId, AuthoritativeEvent existing, string binding)
    {
        if (!TradeSnapshotReplayPolicy.Matches(existing, request.Acceptor.UserId, request.Acceptor.ColonyId, snapshotId, order: false)
            || existing.Payload is not TradeEventPayload payload || payload.CreationRequestHash != binding || payload.TradeId != request.TradeEventId
            || payload.AcceptedMemoEventId != TradeFulfillmentMemoId(request)
            || payload.FulfillmentMode != ResolveRequestedTradeFulfillmentMode(request.FulfillmentMode))
            return TradeFulfillmentFailure(request, ProtocolResponse.Reject(ProtocolErrorCode.Conflict,
                "Trade idempotency key belongs to another operation."));
        string fulfillmentKey = payload.AcceptedMemoEventId ?? request.Acceptor.UserId + ":" + request.Acceptor.ColonyId;
        return new FulfillTradeOrderResponse(
            new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, Format("Trade.FulfillmentTransactionDuplicate")),
            payload.TradeId, payload.AcceptedMemoEventId, existing.EventId, false,
            payload.OfferedItems.Select(ToThingReferenceDto).ToList(), Array.Empty<string>(),
            ledger.Find(payload.TradeId)?.Status.ToString() ?? existing.Status.ToString(),
            ledger.FindByIdempotencyKey($"trade-completed-acceptor-delivery:{payload.TradeId}:{fulfillmentKey}")?.EventId,
            ledger.FindByIdempotencyKey($"trade-completed-owner-delivery:{payload.TradeId}:{fulfillmentKey}")?.EventId,
            snapshotId, transactions.FindSnapshotNextLineageToken(request.Acceptor.UserId, request.Acceptor.ColonyId!, snapshotId));
    }
}
