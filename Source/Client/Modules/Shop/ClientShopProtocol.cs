using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Shop;

internal sealed class ClientShopProtocol
{
    private readonly ClientProtocolTransport transport;
    private readonly ClashOfRimClientNetworkContext context;
    private readonly Func<string> notConfigured;
    internal ClientShopProtocol(ClientProtocolTransport transport, ClashOfRimClientNetworkContext context, Func<string> notConfigured)
    { this.transport = transport.ForContext(context); this.context = context; this.notConfigured = notConfigured; }

    internal Task<ClashOfRimClientNetworkResult<ModListServerShopResponseDto>> ListServerShopAsync(
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModListServerShopResponseDto>();
        }

        var request = new ModListServerShopRequestDto
        {
            AuthToken = context.AuthToken,
            UserId = context.UserId,
            ColonyId = context.ColonyId
        };
        return transport.PostAsync<ModListServerShopRequestDto, ModListServerShopResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.ListServerShop).Route,
            request,
            cancellationToken);
    }

    internal Task<ClashOfRimClientNetworkResult<ModUpsertServerShopListingResponseDto>> UpsertServerShopListingAsync(
        string? listingId,
        string listingKind,
        ModThingReferenceDto item,
        int priceSilver,
        int stockCount,
        double priceIncreaseRatio,
        string qualityRequirementMode,
        string hitPointsRequirementMode,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModUpsertServerShopListingResponseDto>();
        }

        var request = new ModUpsertServerShopListingRequestDto
        {
            AuthToken = context.AuthToken,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            ListingId = listingId,
            ListingKind = listingKind,
            Item = item,
            PriceSilver = priceSilver,
            StockCount = stockCount,
            PriceIncreaseRatio = priceIncreaseRatio,
            QualityRequirementMode = ServerShopQualityRequirementModes.Normalize(qualityRequirementMode),
            HitPointsRequirementMode = ServerShopHitPointsRequirementModes.Normalize(hitPointsRequirementMode)
        };
        return transport.PostAsync<ModUpsertServerShopListingRequestDto, ModUpsertServerShopListingResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.UpsertServerShopListing).Route,
            request,
            cancellationToken);
    }

    internal Task<ClashOfRimClientNetworkResult<ModRemoveServerShopListingResponseDto>> RemoveServerShopListingAsync(
        string listingId,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModRemoveServerShopListingResponseDto>();
        }

        var request = new ModRemoveServerShopListingRequestDto
        {
            AuthToken = context.AuthToken,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            ListingId = listingId
        };
        return transport.PostAsync<ModRemoveServerShopListingRequestDto, ModRemoveServerShopListingResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.RemoveServerShopListing).Route,
            request,
            cancellationToken);
    }

    internal Task<ClashOfRimClientNetworkResult<ModPurchaseServerShopListingResponseDto>> PurchaseServerShopListingWithSnapshotAsync(
        string idempotencyKey,
        string listingId,
        string listingKind,
        int unitPriceSilver,
        int totalPriceSilver,
        int purchaseCount,
        IReadOnlyList<ModThingReferenceDto>? deliveredThings,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModPurchaseServerShopListingResponseDto>();
        }

        var purchase = new ModPurchaseServerShopListingRequestDto
        {
            IdempotencyKey = idempotencyKey,
            Buyer = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            ListingId = listingId,
            UnitPriceSilver = unitPriceSilver,
            TotalPriceSilver = totalPriceSilver,
            PurchaseCount = purchaseCount,
            ListingKind = listingKind,
            DeliveredThings = deliveredThings?.ToList() ?? new List<ModThingReferenceDto>()
        };
        confirmedSnapshot.SnapshotUploadKind = ModSnapshotUploadKinds.ServerShopPurchaseConfirmation;
        var request = new ModPurchaseServerShopListingWithSnapshotRequestDto
        {
            Purchase = purchase,
            ConfirmedSnapshot = confirmedSnapshot
        };
        return transport.PostSnapshotMultipartAsync<ModPurchaseServerShopListingWithSnapshotRequestDto, ModPurchaseServerShopListingResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.PurchaseServerShopListingWithSnapshot).Route,
            request,
            confirmedPayload,
            cancellationToken);
    }

    private Task<ClashOfRimClientNetworkResult<T>> NotConfigured<T>() =>
        Task.FromResult(ClashOfRimClientNetworkResult<T>.Failed("NotConfigured", notConfigured()));
}
