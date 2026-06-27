using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult ListServerShop(ListServerShopRequest request, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            return Results.Ok(new ListServerShopResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Shop.ListMissingUser")),
                Array.Empty<ServerShopListingDto>()));
        }

        var viewer = new ProtocolIdentity(request.UserId, request.ColonyId, snapshotId: null);
        return Results.Ok(new ListServerShopResponse(
            ProtocolResponse.Ok(T("Shop.Listed")),
            state.ServerShop.List().Select(listing => ToServerShopListingDto(listing, state.ServerShop, viewer)).ToList()));
    }

    private static async Task<IResult> UpsertServerShopListing(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        UpsertServerShopListingRequest? request = await ReadJsonRequest<UpsertServerShopListingRequest>(httpRequest);
        if (request is null)
        {
            return Results.Ok(new UpsertServerShopListingResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Shop.InvalidItem")),
                listing: null));
        }

        return UpsertServerShopListingCore(request, state);
    }

    private static IResult UpsertServerShopListingCore(UpsertServerShopListingRequest request, ClashOfRimNetworkState state)
    {
        ProtocolResponse? adminValidation = ValidateShopAdminRequest(request.UserId, state);
        if (adminValidation is not null)
        {
            return Results.Ok(new UpsertServerShopListingResponse(adminValidation, listing: null));
        }

        ProtocolResponse? validation = ValidateServerShopListing(request.Item, request.ListingKind, request.PriceSilver, request.StockCount);
        if (validation is not null)
        {
            return Results.Ok(new UpsertServerShopListingResponse(validation, listing: null));
        }

        if (request.Item.PawnPackage is not null
            && !TryValidateInlinePawnPackages(new[] { request.Item }, out ProtocolResponse? pawnPackageFailure))
        {
            return Results.Ok(new UpsertServerShopListingResponse(
                pawnPackageFailure ?? ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.ParseFailed")),
                listing: null));
        }

        AdminBaselineSnapshot? baseline = state.AdminBaseline.Current;
        if (baseline is not null)
        {
            HashSet<string> validThingDefs = BuildValidTradeThingDefSet(baseline, state.ServerConfiguration);
            IReadOnlyList<string> unavailable = FindUnavailableTradeThingDefs(
                new[] { ToEventThingReference(request.Item, sourceSnapshotId: null) },
                baseline,
                validThingDefs);
            if (unavailable.Count > 0)
            {
                return Results.Ok(new UpsertServerShopListingResponse(
                    ProtocolResponse.Reject(
                        ProtocolErrorCode.ServerRejected,
                        T("Shop.InvalidThingDef", ("DEFS", string.Join(", ", unavailable)))),
                    listing: null));
            }
        }

        ServerShopListingRecord listing = state.ServerShop.Upsert(
            request.ListingId,
            NormalizeShopListingKind(request.ListingKind),
            NormalizeShopItem(request.Item),
            request.PriceSilver,
            request.StockCount,
            request.PriceIncreaseRatio,
            ServerShopQualityRequirementModes.Normalize(request.QualityRequirementMode),
            ServerShopHitPointsRequirementModes.Normalize(request.HitPointsRequirementMode),
            request.UserId,
            DateTimeOffset.UtcNow);
        var viewer = new ProtocolIdentity(request.UserId, request.ColonyId, snapshotId: null);
        return Results.Ok(new UpsertServerShopListingResponse(
            ProtocolResponse.Ok(T("Shop.Upserted")),
            ToServerShopListingDto(listing, state.ServerShop, viewer)));
    }

    private static IResult RemoveServerShopListing(RemoveServerShopListingRequest request, ClashOfRimNetworkState state)
    {
        ProtocolResponse? adminValidation = ValidateShopAdminRequest(request.UserId, state);
        if (adminValidation is not null)
        {
            return Results.Ok(new RemoveServerShopListingResponse(adminValidation, request.ListingId, removed: false));
        }

        if (string.IsNullOrWhiteSpace(request.ListingId))
        {
            return Results.Ok(new RemoveServerShopListingResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Shop.MissingListingId")),
                string.Empty,
                removed: false));
        }

        bool removed = state.ServerShop.Remove(request.ListingId);
        return Results.Ok(new RemoveServerShopListingResponse(
            removed
                ? ProtocolResponse.Ok(T("Shop.Removed"))
                : ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Shop.ListingMissing")),
            request.ListingId,
            removed));
    }

    private static async Task<IResult> PurchaseServerShopListingWithSnapshot(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        MultipartSnapshotRequest<PurchaseServerShopListingWithSnapshotRequest>? multipart =
            await ReadMultipartSnapshotRequest<PurchaseServerShopListingWithSnapshotRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
        {
            return Results.Ok(new PurchaseServerShopListingResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Shop.PurchaseMissingPayload")),
                listingId: null,
                remainingStockCount: 0));
        }

        PurchaseServerShopListingRequest? request = multipart.Request.Purchase;
        if (request is null
            || request.Buyer is null
            || string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.Buyer.UserId)
            || string.IsNullOrWhiteSpace(request.Buyer.ColonyId)
            || string.IsNullOrWhiteSpace(request.ListingId))
        {
            return Results.Ok(new PurchaseServerShopListingResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Shop.PurchaseMissingFields")),
                request?.ListingId,
                remainingStockCount: 0));
        }

        if (multipart.Request.ConfirmedSnapshot is null
            || string.IsNullOrWhiteSpace(multipart.Request.ConfirmedSnapshot.SnapshotId))
        {
            return Results.Ok(new PurchaseServerShopListingResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Shop.PurchaseMissingSnapshot")),
                request.ListingId,
                remainingStockCount: 0));
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        string normalizedListingKind = NormalizeShopListingKind(request.ListingKind);
        ServerShopPurchaseResult prevalidation = state.ServerShop.ValidatePurchase(
            request.IdempotencyKey,
            request.ListingId,
            normalizedListingKind,
            request.UnitPriceSilver,
            request.TotalPriceSilver,
            request.PurchaseCount,
            request.Buyer);
        if (!prevalidation.Accepted)
        {
            LogServerShopPurchaseRejection(state, request, prevalidation, "prevalidation");
            return Results.Ok(new PurchaseServerShopListingResponse(
                BuildServerShopPurchaseRejection(prevalidation, request),
                request.ListingId,
                prevalidation.RemainingStockCount));
        }

        if (prevalidation.Duplicate)
        {
            state.RuntimeLogger.LogInformation(
                "Server shop purchase duplicate: listing={ListingId} user={UserId} colony={ColonyId} key={IdempotencyKey} stock={Stock}",
                request.ListingId,
                request.Buyer.UserId,
                request.Buyer.ColonyId,
                request.IdempotencyKey,
                prevalidation.RemainingStockCount);
            return Results.Ok(new PurchaseServerShopListingResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Shop.PurchaseDuplicate")),
                request.ListingId,
                prevalidation.RemainingStockCount));
        }

        if (string.Equals(normalizedListingKind, ServerShopListingKinds.BuyFromPlayer, StringComparison.Ordinal)
            && prevalidation.Listing is not null
            && !TradeThingRequirementMatcher.Satisfies(
                new[] { MultiplyShopRequirement(prevalidation.Listing.Item, request.PurchaseCount) },
                request.DeliveredThings ?? Array.Empty<ThingReferenceDto>(),
                out IReadOnlyList<string> missing,
                state.Plugins.ActiveTradeThingMetadataMatchers(state.CompatibilityBaseline.Current),
                prevalidation.Listing.QualityRequirementMode,
                prevalidation.Listing.HitPointsRequirementMode))
        {
            state.RuntimeLogger.LogWarning(
                "Server shop purchase rejected: phase=delivered-things listing={ListingId} user={UserId} colony={ColonyId} count={Count} missing={Missing}",
                request.ListingId,
                request.Buyer.UserId,
                request.Buyer.ColonyId,
                request.PurchaseCount,
                string.Join("; ", missing));
            return Results.Ok(new PurchaseServerShopListingResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    T("Shop.DeliveredThingsMismatch", ("MISSING", string.Join("; ", missing)))),
                request.ListingId,
                prevalidation.RemainingStockCount));
        }

        if (TryRejectExpiredPendingConfirmationSnapshot(
                state,
                request.Buyer.UserId,
                request.Buyer.ColonyId ?? string.Empty,
                nowUtc,
                out ProtocolResponse? pendingRejection))
        {
            state.RuntimeLogger.LogWarning(
                "Server shop purchase rejected: phase=pending-confirmation listing={ListingId} user={UserId} colony={ColonyId} count={Count} unitPrice={UnitPrice} message={Message}",
                request.ListingId,
                request.Buyer.UserId,
                request.Buyer.ColonyId,
                request.PurchaseCount,
                request.UnitPriceSilver,
                pendingRejection?.Message);
            return Results.Ok(new PurchaseServerShopListingResponse(
                pendingRejection!,
                request.ListingId,
                remainingStockCount: state.ServerShop.Find(request.ListingId)?.StockCount ?? 0));
        }

        SnapshotUploadResult? upload = null;
        ProtocolResponse? finalDeliveredThingsRejection = null;
        ServerShopPurchaseResult purchase = state.ServerShop.TryPurchaseAfterCommit(
            request.IdempotencyKey,
            request.ListingId,
            normalizedListingKind,
            request.UnitPriceSilver,
            request.TotalPriceSilver,
            request.PurchaseCount,
            request.Buyer,
            nowUtc,
            finalValidation =>
            {
                if (string.Equals(normalizedListingKind, ServerShopListingKinds.BuyFromPlayer, StringComparison.Ordinal)
                    && finalValidation.Listing is not null
                    && !TradeThingRequirementMatcher.Satisfies(
                        new[] { MultiplyShopRequirement(finalValidation.Listing.Item, request.PurchaseCount) },
                        request.DeliveredThings ?? Array.Empty<ThingReferenceDto>(),
                        out IReadOnlyList<string> finalMissing,
                        state.Plugins.ActiveTradeThingMetadataMatchers(state.CompatibilityBaseline.Current),
                        finalValidation.Listing.QualityRequirementMode,
                        finalValidation.Listing.HitPointsRequirementMode))
                {
                    finalDeliveredThingsRejection = ProtocolResponse.Reject(
                        ProtocolErrorCode.ValidationFailed,
                        T("Shop.DeliveredThingsMismatch", ("MISSING", string.Join("; ", finalMissing))));
                    state.RuntimeLogger.LogWarning(
                        "Server shop purchase rejected: phase=final-delivered-things listing={ListingId} user={UserId} colony={ColonyId} count={Count} missing={Missing}",
                        request.ListingId,
                        request.Buyer.UserId,
                        request.Buyer.ColonyId,
                        request.PurchaseCount,
                        string.Join("; ", finalMissing));
                    return false;
                }

                upload = ReceiveSnapshot(
                    state,
                    request.Buyer.UserId,
                    request.Buyer.ColonyId ?? string.Empty,
                    multipart.Request.ConfirmedSnapshot.SnapshotId!,
                    multipart.Request.ConfirmedSnapshot,
                    multipart.Payload,
                    nowUtc);
                return upload.Accepted && upload.AcceptedSnapshot is not null;
            });

        if (finalDeliveredThingsRejection is not null)
        {
            return Results.Ok(new PurchaseServerShopListingResponse(
                finalDeliveredThingsRejection,
                request.ListingId,
                remainingStockCount: state.ServerShop.Find(request.ListingId)?.StockCount ?? 0));
        }

        if (upload is not null && (!upload.Accepted || upload.AcceptedSnapshot is null))
        {
            state.RuntimeLogger.LogWarning(
                "Server shop purchase rejected: phase=snapshot listing={ListingId} user={UserId} colony={ColonyId} snapshot={SnapshotId} count={Count} unitPrice={UnitPrice} message={Message}",
                request.ListingId,
                request.Buyer.UserId,
                request.Buyer.ColonyId,
                multipart.Request.ConfirmedSnapshot.SnapshotId,
                request.PurchaseCount,
                request.UnitPriceSilver,
                upload.Message);
            return Results.Ok(new PurchaseServerShopListingResponse(
                ToProtocolResponse(upload),
                request.ListingId,
                remainingStockCount: state.ServerShop.Find(request.ListingId)?.StockCount ?? 0));
        }

        if (!purchase.Accepted)
        {
            LogServerShopPurchaseRejection(state, request, purchase, "final-validation");
            return Results.Ok(new PurchaseServerShopListingResponse(
                BuildServerShopPurchaseRejection(purchase, request),
                request.ListingId,
                purchase.RemainingStockCount));
        }

        if (purchase.Duplicate)
        {
            state.RuntimeLogger.LogInformation(
                "Server shop purchase duplicate: listing={ListingId} user={UserId} colony={ColonyId} key={IdempotencyKey} stock={Stock}",
                request.ListingId,
                request.Buyer.UserId,
                request.Buyer.ColonyId,
                request.IdempotencyKey,
                purchase.RemainingStockCount);
            return Results.Ok(new PurchaseServerShopListingResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Shop.PurchaseDuplicate")),
                request.ListingId,
                purchase.RemainingStockCount));
        }

        if (upload?.AcceptedSnapshot is null)
        {
            state.RuntimeLogger.LogWarning(
                "Server shop purchase rejected: phase=snapshot-not-committed listing={ListingId} user={UserId} colony={ColonyId} count={Count} unitPrice={UnitPrice}",
                request.ListingId,
                request.Buyer.UserId,
                request.Buyer.ColonyId,
                request.PurchaseCount,
                request.UnitPriceSilver);
            return Results.Ok(new PurchaseServerShopListingResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Shop.SnapshotRejected")),
                request.ListingId,
                purchase.RemainingStockCount));
        }

        RunSnapshotPostUploadProcessors(
            state,
            request.Buyer.UserId,
            request.Buyer.ColonyId ?? string.Empty,
            sessionId: null,
            upload,
            nowUtc);

        state.RuntimeLogger.LogInformation(
            "Server shop purchase accepted: listing={ListingId} user={UserId} colony={ColonyId} snapshot={SnapshotId} count={Count} unitPrice={UnitPrice} stock={Stock}",
            request.ListingId,
            request.Buyer.UserId,
            request.Buyer.ColonyId,
            upload.AcceptedSnapshot.Identity.SnapshotId,
            request.PurchaseCount,
            request.UnitPriceSilver,
            purchase.RemainingStockCount);

        return Results.Ok(new PurchaseServerShopListingResponse(
            ProtocolResponse.Ok(T("Shop.Purchased")),
            request.ListingId,
            purchase.RemainingStockCount,
            upload.AcceptedSnapshot.Identity.SnapshotId ?? multipart.Request.ConfirmedSnapshot.SnapshotId,
            upload.AcceptedSnapshot.Envelope.NextLineageToken));
    }

    private static ProtocolResponse BuildServerShopPurchaseRejection(
        ServerShopPurchaseResult purchase,
        PurchaseServerShopListingRequest request)
    {
        return ProtocolResponse.Reject(
            purchase.FailureKey == "Shop.PriceChanged" ? ProtocolErrorCode.Conflict : ProtocolErrorCode.ServerRejected,
            T(
                purchase.FailureKey ?? "Shop.PurchaseRejected",
                ("PRICE", request.UnitPriceSilver.ToString(CultureInfo.InvariantCulture)),
                ("EXPECTED", purchase.CurrentUnitPriceSilver.ToString(CultureInfo.InvariantCulture)),
                ("TOTAL", request.TotalPriceSilver.ToString(CultureInfo.InvariantCulture)),
                ("EXPECTEDTOTAL", purchase.ExpectedTotalPriceSilver.ToString(CultureInfo.InvariantCulture)),
                ("COUNT", request.PurchaseCount.ToString(CultureInfo.InvariantCulture)),
                ("STOCK", purchase.RemainingStockCount.ToString(CultureInfo.InvariantCulture))));
    }

    private static void LogServerShopPurchaseRejection(
        ClashOfRimNetworkState state,
        PurchaseServerShopListingRequest request,
        ServerShopPurchaseResult purchase,
        string phase)
    {
        state.RuntimeLogger.LogWarning(
            "Server shop purchase rejected: phase={Phase} listing={ListingId} user={UserId} colony={ColonyId} count={Count} unitPrice={UnitPrice} totalPrice={TotalPrice} expectedUnitPrice={ExpectedUnitPrice} expectedTotalPrice={ExpectedTotalPrice} stock={Stock} failure={FailureKey}",
            phase,
            request.ListingId,
            request.Buyer?.UserId,
            request.Buyer?.ColonyId,
            request.PurchaseCount,
            request.UnitPriceSilver,
            request.TotalPriceSilver,
            purchase.CurrentUnitPriceSilver,
            purchase.ExpectedTotalPriceSilver,
            purchase.RemainingStockCount,
            purchase.FailureKey ?? "none");
    }

    private static ProtocolResponse? ValidateShopAdminRequest(string userId, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Shop.AdminMissingUser"));
        }

        if (!state.WorldConfiguration.IsAdministrator(userId))
        {
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Shop.AdminOnly"));
        }

        return null;
    }

    private static ProtocolResponse? ValidateServerShopListing(ThingReferenceDto? item, string listingKind, int priceSilver, int stockCount)
    {
        if (!ServerShopListingKinds.IsKnown(listingKind))
        {
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Shop.InvalidKind"));
        }

        if (item is null
            || string.IsNullOrWhiteSpace(item.GlobalKey)
            || string.IsNullOrWhiteSpace(item.DefName)
            || item.StackCount <= 0)
        {
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Shop.InvalidItem"));
        }

        if (item.PawnPackage is not null || !string.IsNullOrWhiteSpace(item.PawnPackageId))
        {
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Shop.PawnUnsupported"));
        }

        if (priceSilver < 1)
        {
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Shop.InvalidPrice"));
        }

        if (stockCount < 0)
        {
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Shop.InvalidStock"));
        }

        return null;
    }

    private static ThingReferenceDto NormalizeShopItem(ThingReferenceDto item)
    {
        return new ThingReferenceDto(
            string.IsNullOrWhiteSpace(item.GlobalKey) ? "shop-item-" + Guid.NewGuid().ToString("N") : item.GlobalKey,
            item.DefName,
            Math.Max(1, item.StackCount),
            item.Quality,
            item.HitPoints,
            item.MinifiedInnerDefName,
            item.MinifiedInnerStuffDefName,
            item.MinifiedInnerQuality,
            item.MinifiedInnerHitPoints,
            item.WornByCorpse,
            item.Biocoded,
            item.BiocodedPawnLabel,
            item.BiocodedPawnGlobalId,
            item.DisplayLabel,
            marketValue: null,
            item.UniqueWeapon,
            item.UniqueWeaponTraits,
            pawnPackage: null,
            pawnPackageId: null,
            stuffDefName: item.StuffDefName,
            maxHitPoints: item.MaxHitPoints,
            minifiedInnerMaxHitPoints: item.MinifiedInnerMaxHitPoints,
            metadata: CopyMetadata(item.Metadata),
            thingPackage: item.ThingPackage,
            thingPackageId: item.ThingPackageId);
    }

    private static string NormalizeShopListingKind(string? listingKind)
    {
        return string.Equals(listingKind, ServerShopListingKinds.BuyFromPlayer, StringComparison.Ordinal)
            ? ServerShopListingKinds.BuyFromPlayer
            : ServerShopListingKinds.SellToPlayer;
    }

    private static ThingReferenceDto MultiplyShopRequirement(ThingReferenceDto item, int purchaseCount)
    {
        int count = Math.Max(1, item.StackCount) * Math.Max(1, purchaseCount);
        return new ThingReferenceDto(
            item.GlobalKey,
            item.DefName,
            count,
            item.Quality,
            item.HitPoints,
            item.MinifiedInnerDefName,
            item.MinifiedInnerStuffDefName,
            item.MinifiedInnerQuality,
            item.MinifiedInnerHitPoints,
            item.WornByCorpse,
            item.Biocoded,
            item.BiocodedPawnLabel,
            item.BiocodedPawnGlobalId,
            item.DisplayLabel,
            item.MarketValue,
            item.UniqueWeapon,
            item.UniqueWeaponTraits,
            item.PawnPackage,
            item.PawnPackageId,
            item.StuffDefName,
            item.MaxHitPoints,
            item.MinifiedInnerMaxHitPoints,
            metadata: CopyMetadata(item.Metadata),
            thingPackage: item.ThingPackage,
            thingPackageId: item.ThingPackageId);
    }

    private static ServerShopListingDto ToServerShopListingDto(
        ServerShopListingRecord listing,
        ServerShopRegistry shop,
        ProtocolIdentity? viewer)
    {
        int buyerPurchaseCount = shop.GetBuyerPurchaseCount(listing.ListingId, viewer);
        int currentPrice = ServerShopRegistry.CalculateCurrentUnitPrice(listing, buyerPurchaseCount);
        return new ServerShopListingDto(
            listing.ListingId,
            listing.ListingKind,
            listing.Item,
            currentPrice,
            listing.StockCount,
            listing.CreatedAtUtc,
            listing.UpdatedAtUtc,
            listing.UpdatedByUserId,
            listing.PriceSilver,
            listing.PriceIncreaseRatio,
            buyerPurchaseCount,
            listing.QualityRequirementMode,
            listing.HitPointsRequirementMode);
    }

}
