using System.Runtime.Serialization;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.ClientNetwork;

[DataContract]
public sealed class ModListServerShopRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;
}

[DataContract]
public sealed class ModServerShopListingDto
{
    [DataMember(Name = "listingId")]
    public string ListingId { get; set; } = string.Empty;

    [DataMember(Name = "listingKind")]
    public string ListingKind { get; set; } = "SellToPlayer";

    [DataMember(Name = "item")]
    public ModThingReferenceDto? Item { get; set; }

    [DataMember(Name = "priceSilver")]
    public int PriceSilver { get; set; }

    [DataMember(Name = "basePriceSilver")]
    public int BasePriceSilver { get; set; }

    [DataMember(Name = "priceIncreaseRatio")]
    public double PriceIncreaseRatio { get; set; }

    [DataMember(Name = "buyerPurchaseCount")]
    public int BuyerPurchaseCount { get; set; }

    [DataMember(Name = "stockCount")]
    public int StockCount { get; set; }

    [DataMember(Name = "qualityRequirementMode")]
    public string QualityRequirementMode { get; set; } = "AtLeast";

    [DataMember(Name = "hitPointsRequirementMode")]
    public string HitPointsRequirementMode { get; set; } = "AtLeast";

    [DataMember(Name = "createdAtUtc")]
    public string? CreatedAtUtc { get; set; }

    [DataMember(Name = "updatedAtUtc")]
    public string? UpdatedAtUtc { get; set; }

    [DataMember(Name = "updatedByUserId")]
    public string? UpdatedByUserId { get; set; }
}

[DataContract]
public sealed class ModListServerShopResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "listings")]
    public List<ModServerShopListingDto> Listings { get; set; } = new();
}

[DataContract]
public sealed class ModUpsertServerShopListingRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "listingId")]
    public string? ListingId { get; set; }

    [DataMember(Name = "listingKind")]
    public string ListingKind { get; set; } = "SellToPlayer";

    [DataMember(Name = "item")]
    public ModThingReferenceDto? Item { get; set; }

    [DataMember(Name = "priceSilver")]
    public int PriceSilver { get; set; }

    [DataMember(Name = "stockCount")]
    public int StockCount { get; set; }

    [DataMember(Name = "priceIncreaseRatio")]
    public double PriceIncreaseRatio { get; set; }

    [DataMember(Name = "qualityRequirementMode")]
    public string QualityRequirementMode { get; set; } = "AtLeast";

    [DataMember(Name = "hitPointsRequirementMode")]
    public string HitPointsRequirementMode { get; set; } = "AtLeast";
}

[DataContract]
public sealed class ModUpsertServerShopListingResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "listing")]
    public ModServerShopListingDto? Listing { get; set; }
}

[DataContract]
public sealed class ModRemoveServerShopListingRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "listingId")]
    public string ListingId { get; set; } = string.Empty;
}

[DataContract]
public sealed class ModRemoveServerShopListingResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "listingId")]
    public string ListingId { get; set; } = string.Empty;

    [DataMember(Name = "removed")]
    public bool Removed { get; set; }
}

[DataContract]
public sealed class ModPurchaseServerShopListingRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "buyer")]
    public ModProtocolIdentityDto? Buyer { get; set; }

    [DataMember(Name = "listingId")]
    public string ListingId { get; set; } = string.Empty;

    [DataMember(Name = "unitPriceSilver")]
    public int UnitPriceSilver { get; set; }

    [DataMember(Name = "totalPriceSilver")]
    public int TotalPriceSilver { get; set; }

    [DataMember(Name = "purchaseCount")]
    public int PurchaseCount { get; set; }

    [DataMember(Name = "listingKind")]
    public string ListingKind { get; set; } = "SellToPlayer";

    [DataMember(Name = "deliveredThings")]
    public List<ModThingReferenceDto> DeliveredThings { get; set; } = new();
}

[DataContract]
public sealed class ModPurchaseServerShopListingWithSnapshotRequestDto
{
    [DataMember(Name = "purchase")]
    public ModPurchaseServerShopListingRequestDto? Purchase { get; set; }

    [DataMember(Name = "confirmedSnapshot")]
    public ModSnapshotPackageMetadataDto? ConfirmedSnapshot { get; set; }
}

[DataContract]
public sealed class ModPurchaseServerShopListingResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "listingId")]
    public string? ListingId { get; set; }

    [DataMember(Name = "remainingStockCount")]
    public int RemainingStockCount { get; set; }

    [DataMember(Name = "appliedSnapshotId")]
    public string? AppliedSnapshotId { get; set; }

    [DataMember(Name = "nextLineageToken")]
    public string? NextLineageToken { get; set; }
}
