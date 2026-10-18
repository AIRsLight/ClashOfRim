using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Economy;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;
using Microsoft.Extensions.Logging;
using static AIRsLight.ClashOfRim.Network.Modules.Shop.ShopRules;
using static AIRsLight.ClashOfRim.Network.Modules.Trades.TradeRules;
using static AIRsLight.ClashOfRim.Network.Modules.Transfers.ItemTransferMapper;
using static AIRsLight.ClashOfRim.Network.Modules.Snapshots.SnapshotTransactions;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.Shop;

internal sealed class ShopService(ServerShopRegistry shop, SnapshotRequestAuthorization requests, AdministratorAuthorization administrators,
    SnapshotMutationCoordinator mutations, SnapshotTransactions transactions, PendingConfirmationReconciler pending, ShopSnapshotCommitter commits,
    AdminBaselineRegistry baseline, ServerPluginRegistry plugins, CompatibilityBaselineRegistry compatibility, ILogger logger,
    Func<ClashOfRimServerConfiguration> configuration)
{
    internal ListServerShopResponse List(ListServerShopRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
            return new(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Shop.ListMissingUser")), Array.Empty<ServerShopListingDto>());
        using var access = requests.EnterColony(request.AuthToken, request.UserId, request.ColonyId);
        if (access.Rejection is { } denied) return new(denied, Array.Empty<ServerShopListingDto>());
        var viewer = new ProtocolIdentity(request.UserId, request.ColonyId, null);
        return new(ProtocolResponse.Ok(Format("Shop.Listed")), shop.List().Select(listing => ToDto(listing, shop, viewer)).ToList());
    }

    internal UpsertServerShopListingResponse Upsert(UpsertServerShopListingRequest request)
    {
        if (ValidateListing(request.Item, request.ListingKind, request.PriceSilver, request.StockCount) is { } invalid) return new(invalid, null);
        using var access = mutations.Enter(() => administrators.Authorize(request.UserId, request.ColonyId, request.AuthToken));
        if (access.Rejection is { } denied) return new(denied, null);
        if (baseline.Current is { } current)
        {
            var unavailable = FindUnavailableTradeThingDefs(new[] { ToEventThingReference(request.Item, null) }, current,
                BuildValidTradeThingDefSet(current, configuration()));
            if (unavailable.Count > 0) return new(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected,
                Format("Shop.InvalidThingDef", ("DEFS", string.Join(", ", unavailable)))), null);
        }
        var listing = shop.Upsert(request.ListingId, request.ListingKind, NormalizeItem(request.Item), request.PriceSilver, request.StockCount,
            request.PriceIncreaseRatio, request.QualityRequirementMode, request.HitPointsRequirementMode, request.UserId, DateTimeOffset.UtcNow);
        return new(ProtocolResponse.Ok(Format("Shop.Upserted")), ToDto(listing, shop, new(request.UserId, request.ColonyId, null)));
    }

    internal RemoveServerShopListingResponse Remove(RemoveServerShopListingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ListingId)) return new(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Shop.MissingListingId")), "", false);
        using var access = mutations.Enter(() => administrators.Authorize(request.UserId, request.ColonyId, request.AuthToken));
        if (access.Rejection is { } denied) return new(denied, request.ListingId, false);
        bool removed = shop.Remove(request.ListingId);
        return new(removed ? ProtocolResponse.Ok(Format("Shop.Removed"))
            : ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, Format("Shop.ListingMissing")), request.ListingId, removed);
    }

    internal PurchaseServerShopListingResponse Purchase(PurchaseServerShopListingWithSnapshotRequest transaction, byte[] payload, string? principalToken)
    {
        var request = transaction.Purchase;
        if (request?.Buyer is null || string.IsNullOrWhiteSpace(request.IdempotencyKey) || string.IsNullOrWhiteSpace(request.Buyer.UserId)
            || string.IsNullOrWhiteSpace(request.Buyer.ColonyId) || !ServerShopListingKinds.IsKnown(request.ListingKind) || string.IsNullOrWhiteSpace(request.ListingId))
            return new(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Shop.PurchaseMissingFields")), request?.ListingId, 0);
        if (transaction.ConfirmedSnapshot is null || string.IsNullOrWhiteSpace(transaction.ConfirmedSnapshot.SnapshotId))
            return new(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Shop.PurchaseMissingSnapshot")), request.ListingId, 0);
        var metadata = transaction.ConfirmedSnapshot;
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        ServerShopPurchaseResult purchase;
        // Shared snapshot mutation gate precedes the shop aggregate gate, at both boundaries.
        using (var access = requests.EnterPrincipal(principalToken, request.Buyer))
        lock (shop.SnapshotCommitGate)
        {
            if (access.Rejection is { } denied) return new(denied, request.ListingId, 0);
            if (requests.AuthorizePrincipal(principalToken, request.Buyer) is { } principalFailure) return new(principalFailure, request.ListingId, 0);
            purchase = ValidatePurchase(request);
            if (!purchase.Accepted) return Rejected(request, purchase, "prevalidation");
            if (purchase.Duplicate) return Replay(request, metadata.SnapshotId!);
        }
        if (request.ListingKind == ServerShopListingKinds.BuyFromPlayer
            && (!TryValidateInlinePawnPackages(request.DeliveredThings ?? Array.Empty<ThingReferenceDto>(), out var packageFailure)
                || !TryValidateInlineThingPackages(request.DeliveredThings ?? Array.Empty<ThingReferenceDto>(), out packageFailure)))
            return new(packageFailure ?? ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("PawnPackage.ParseFailed")), request.ListingId, purchase.RemainingStockCount);
        if (ValidateDelivery(request, purchase.Listing!) is { } deliveryFailure) return new(deliveryFailure, request.ListingId, purchase.RemainingStockCount);

        var upload = transactions.PrepareTransactionSnapshot(request.Buyer.UserId, request.Buyer.ColonyId!, metadata.SnapshotId!, metadata, payload, nowUtc);
        using (var access = requests.EnterPrincipal(principalToken, request.Buyer))
        lock (shop.SnapshotCommitGate)
        {
            if (access.Rejection is { } denied) return new(denied, request.ListingId, 0);
            if (requests.AuthorizePrincipal(principalToken, request.Buyer) is { } principalFailure) return new(principalFailure, request.ListingId, 0);
            purchase = ValidatePurchase(request);
            if (!purchase.Accepted) return Rejected(request, purchase, "final-validation");
            if (purchase.Duplicate) return Replay(request, metadata.SnapshotId!);
            // Resolve receipts first; reconcile pending economy state only at the final authorized boundary.
            if (pending.ReconcileSnapshot(request.Buyer.UserId, request.Buyer.ColonyId!, DateTimeOffset.UtcNow) is { } pendingFailure)
                return new(pendingFailure, request.ListingId, purchase.RemainingStockCount);
            if (!upload.Accepted || upload.AcceptedSnapshot is null) return new(ToProtocolResponse(upload), request.ListingId, purchase.RemainingStockCount);
            if (ValidateDelivery(request, purchase.Listing!) is { } finalDeliveryFailure) return new(finalDeliveryFailure, request.ListingId, purchase.RemainingStockCount);
            var package = ProtocolDtoMapper.ToSaveSnapshotPackage(metadata, payload);
            upload = transactions.CreateSnapshotReceiver().CommitPrepared(new SnapshotUploadContext(request.Buyer.UserId, request.Buyer.ColonyId!,
                metadata.SnapshotId!, null, upload.SnapshotUploadKind), package, upload, storeAccepted: false);
            if (!upload.Accepted || upload.AcceptedSnapshot is null) return new(ToProtocolResponse(upload), request.ListingId, purchase.RemainingStockCount);
            var mutation = shop.PrepareSnapshotCommit(request, upload.AcceptedSnapshot);
            commits.Commit(package, upload, mutation);
            logger.LogInformation("Server shop purchase accepted: listing={ListingId} user={UserId} colony={ColonyId} snapshot={SnapshotId} count={Count} stock={Stock}",
                request.ListingId, request.Buyer.UserId, request.Buyer.ColonyId, mutation.Receipt.SnapshotId, request.PurchaseCount, mutation.Receipt.RemainingStockCount);
            return new(ProtocolResponse.Ok(Format("Shop.Purchased")), request.ListingId, mutation.Receipt.RemainingStockCount, mutation.Receipt.SnapshotId, upload.AcceptedSnapshot.Envelope.NextLineageToken);
        }
    }

    private ServerShopPurchaseResult ValidatePurchase(PurchaseServerShopListingRequest request) =>
        shop.ValidatePurchase(request.IdempotencyKey, request.ListingId, request.ListingKind, request.UnitPriceSilver, request.TotalPriceSilver, request.PurchaseCount, request.Buyer);
    private ProtocolResponse? ValidateDelivery(PurchaseServerShopListingRequest request, ServerShopListingRecord listing)
    {
        if (request.ListingKind != ServerShopListingKinds.BuyFromPlayer) return null;
        var requirement = MultiplyRequirement(listing.Item, request.PurchaseCount);
        if (requirement is null) return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Shop.InvalidPurchaseCount"));
        if (TradeThingRequirementMatcher.Satisfies(new[] { requirement },
            request.DeliveredThings ?? Array.Empty<ThingReferenceDto>(), out var missing,
            plugins.ActiveTradeThingMetadataMatchers(compatibility.Current), listing.QualityRequirementMode, listing.HitPointsRequirementMode)) return null;
        logger.LogWarning("Server shop purchase rejected: phase=delivered-things listing={ListingId} user={UserId} colony={ColonyId} count={Count} missing={Missing}",
            request.ListingId, request.Buyer.UserId, request.Buyer.ColonyId, request.PurchaseCount, string.Join("; ", missing));
        return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Shop.DeliveredThingsMismatch", ("MISSING", string.Join("; ", missing))));
    }
    private PurchaseServerShopListingResponse Replay(PurchaseServerShopListingRequest request, string snapshotId)
    {
        var receipt = shop.FindReceipt(request.IdempotencyKey);
        if (receipt is null || !receipt.Matches(request, snapshotId)) return new(ProtocolResponse.Reject(ProtocolErrorCode.Conflict, Format("Shop.PurchaseRejected")), request.ListingId, 0);
        return new(new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, Format("Shop.PurchaseDuplicate")), request.ListingId,
            receipt.RemainingStockCount, receipt.SnapshotId, transactions.FindSnapshotNextLineageToken(receipt.UserId, receipt.ColonyId, receipt.SnapshotId));
    }
    private PurchaseServerShopListingResponse Rejected(PurchaseServerShopListingRequest request, ServerShopPurchaseResult purchase, string phase)
    {
        logger.LogWarning("Server shop purchase rejected: phase={Phase} listing={ListingId} user={UserId} colony={ColonyId} count={Count} unitPrice={UnitPrice} totalPrice={TotalPrice} expectedUnitPrice={ExpectedUnitPrice} expectedTotalPrice={ExpectedTotalPrice} stock={Stock} failure={FailureKey}",
            phase, request.ListingId, request.Buyer?.UserId, request.Buyer?.ColonyId, request.PurchaseCount, request.UnitPriceSilver,
            request.TotalPriceSilver, purchase.CurrentUnitPriceSilver, purchase.ExpectedTotalPriceSilver, purchase.RemainingStockCount, purchase.FailureKey ?? "none");
        return new(PurchaseRejection(purchase, request), request.ListingId, purchase.RemainingStockCount);
    }
}
