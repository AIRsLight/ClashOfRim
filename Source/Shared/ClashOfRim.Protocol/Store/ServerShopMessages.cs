using System;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Protocol;

public sealed class ListServerShopRequest
{
    public ListServerShopRequest(string userId, string colonyId)
    {
        UserId = userId;
        ColonyId = colonyId;
    }

    public string UserId { get; }

    public string ColonyId { get; }
}

public sealed class ServerShopListingDto
{
    public ServerShopListingDto(
        string listingId,
        string listingKind,
        ThingReferenceDto item,
        int priceSilver,
        int stockCount,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        string? updatedByUserId,
        int? basePriceSilver = null,
        double priceIncreaseRatio = 1d,
        int buyerPurchaseCount = 0,
        string? qualityRequirementMode = null,
        string? hitPointsRequirementMode = null)
    {
        ListingId = listingId;
        ListingKind = listingKind;
        Item = item;
        PriceSilver = priceSilver;
        BasePriceSilver = basePriceSilver ?? priceSilver;
        PriceIncreaseRatio = priceIncreaseRatio;
        BuyerPurchaseCount = buyerPurchaseCount;
        StockCount = stockCount;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        UpdatedByUserId = updatedByUserId;
        QualityRequirementMode = ServerShopQualityRequirementModes.Normalize(qualityRequirementMode);
        HitPointsRequirementMode = ServerShopHitPointsRequirementModes.Normalize(hitPointsRequirementMode);
    }

    public string ListingId { get; }

    public string ListingKind { get; }

    public ThingReferenceDto Item { get; }

    public int PriceSilver { get; }

    public int BasePriceSilver { get; }

    public double PriceIncreaseRatio { get; }

    public int BuyerPurchaseCount { get; }

    public int StockCount { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public string? UpdatedByUserId { get; }

    public string QualityRequirementMode { get; }

    public string HitPointsRequirementMode { get; }
}

public sealed class ListServerShopResponse
{
    public ListServerShopResponse(ProtocolResponse result, IReadOnlyList<ServerShopListingDto> listings)
    {
        Result = result;
        Listings = listings;
    }

    public ProtocolResponse Result { get; }

    public IReadOnlyList<ServerShopListingDto> Listings { get; }
}

public sealed class UpsertServerShopListingRequest
{
    public UpsertServerShopListingRequest(
        string userId,
        string colonyId,
        string? listingId,
        string listingKind,
        ThingReferenceDto item,
        int priceSilver,
        int stockCount,
        double priceIncreaseRatio = 1d,
        string? qualityRequirementMode = null,
        string? hitPointsRequirementMode = null)
    {
        UserId = userId;
        ColonyId = colonyId;
        ListingId = listingId;
        ListingKind = listingKind;
        Item = item;
        PriceSilver = priceSilver;
        StockCount = stockCount;
        PriceIncreaseRatio = priceIncreaseRatio;
        QualityRequirementMode = ServerShopQualityRequirementModes.Normalize(qualityRequirementMode);
        HitPointsRequirementMode = ServerShopHitPointsRequirementModes.Normalize(hitPointsRequirementMode);
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? ListingId { get; }

    public string ListingKind { get; }

    public ThingReferenceDto Item { get; }

    public int PriceSilver { get; }

    public int StockCount { get; }

    public double PriceIncreaseRatio { get; }

    public string QualityRequirementMode { get; }

    public string HitPointsRequirementMode { get; }
}

public sealed class UpsertServerShopListingResponse
{
    public UpsertServerShopListingResponse(ProtocolResponse result, ServerShopListingDto? listing)
    {
        Result = result;
        Listing = listing;
    }

    public ProtocolResponse Result { get; }

    public ServerShopListingDto? Listing { get; }
}

public sealed class RemoveServerShopListingRequest
{
    public RemoveServerShopListingRequest(string userId, string colonyId, string listingId)
    {
        UserId = userId;
        ColonyId = colonyId;
        ListingId = listingId;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string ListingId { get; }
}

public sealed class RemoveServerShopListingResponse
{
    public RemoveServerShopListingResponse(ProtocolResponse result, string listingId, bool removed)
    {
        Result = result;
        ListingId = listingId;
        Removed = removed;
    }

    public ProtocolResponse Result { get; }

    public string ListingId { get; }

    public bool Removed { get; }
}

public sealed class PurchaseServerShopListingRequest
{
    public PurchaseServerShopListingRequest(
        string idempotencyKey,
        ProtocolIdentity buyer,
        string listingId,
        int unitPriceSilver,
        int totalPriceSilver,
        int purchaseCount,
        string listingKind,
        IReadOnlyList<ThingReferenceDto>? deliveredThings = null)
    {
        IdempotencyKey = idempotencyKey;
        Buyer = buyer;
        ListingId = listingId;
        UnitPriceSilver = unitPriceSilver;
        TotalPriceSilver = totalPriceSilver;
        PurchaseCount = purchaseCount;
        ListingKind = listingKind;
        DeliveredThings = deliveredThings ?? Array.Empty<ThingReferenceDto>();
    }

    public string IdempotencyKey { get; }

    public ProtocolIdentity Buyer { get; }

    public string ListingId { get; }

    public int UnitPriceSilver { get; }

    public int TotalPriceSilver { get; }

    public int PurchaseCount { get; }

    public string ListingKind { get; }

    public IReadOnlyList<ThingReferenceDto> DeliveredThings { get; }
}

public static class ServerShopListingKinds
{
    public const string SellToPlayer = "SellToPlayer";
    public const string BuyFromPlayer = "BuyFromPlayer";

    public static bool IsKnown(string? kind)
    {
        return string.Equals(kind, SellToPlayer, StringComparison.Ordinal)
            || string.Equals(kind, BuyFromPlayer, StringComparison.Ordinal);
    }
}

public static class ServerShopQualityRequirementModes
{
    public const string AtLeast = "AtLeast";
    public const string AtMost = "AtMost";

    public static bool IsKnown(string? mode)
    {
        return string.Equals(mode, AtLeast, StringComparison.Ordinal)
            || string.Equals(mode, AtMost, StringComparison.Ordinal);
    }

    public static string Normalize(string? mode)
    {
        return string.Equals(mode, AtMost, StringComparison.Ordinal)
            ? AtMost
            : AtLeast;
    }
}

public static class ServerShopHitPointsRequirementModes
{
    public const string AtLeast = "AtLeast";
    public const string AtMost = "AtMost";

    public static bool IsKnown(string? mode)
    {
        return string.Equals(mode, AtLeast, StringComparison.Ordinal)
            || string.Equals(mode, AtMost, StringComparison.Ordinal);
    }

    public static string Normalize(string? mode)
    {
        return string.Equals(mode, AtMost, StringComparison.Ordinal)
            ? AtMost
            : AtLeast;
    }
}

public sealed class PurchaseServerShopListingWithSnapshotRequest
{
    public PurchaseServerShopListingWithSnapshotRequest(
        PurchaseServerShopListingRequest purchase,
        SnapshotPackageMetadataDto confirmedSnapshot)
    {
        Purchase = purchase;
        ConfirmedSnapshot = confirmedSnapshot;
    }

    public PurchaseServerShopListingRequest Purchase { get; }

    public SnapshotPackageMetadataDto ConfirmedSnapshot { get; }
}

public sealed class PurchaseServerShopListingResponse
{
    public PurchaseServerShopListingResponse(
        ProtocolResponse result,
        string? listingId,
        int remainingStockCount,
        string? appliedSnapshotId = null,
        string? nextLineageToken = null)
    {
        Result = result;
        ListingId = listingId;
        RemainingStockCount = remainingStockCount;
        AppliedSnapshotId = appliedSnapshotId;
        NextLineageToken = nextLineageToken;
    }

    public ProtocolResponse Result { get; }

    public string? ListingId { get; }

    public int RemainingStockCount { get; }

    public string? AppliedSnapshotId { get; }

    public string? NextLineageToken { get; }
}
