using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Shop;

internal sealed record ClientShopListingEdit(string? ListingId, string ListingKind, ModThingReferenceDto Item,
    int PriceSilver, int StockCount, double PriceIncreaseRatio = 1d,
    string QualityRequirementMode = "AtLeast", string HitPointsRequirementMode = "AtLeast");

internal interface IClientShopCatalogTransport : IClientShopPackageHydration
{
    Task<ClashOfRimClientNetworkResult<ModListServerShopResponseDto>> ListAsync(ClashOfRimClientNetworkContext context, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModUpsertServerShopListingResponseDto>> UpsertAsync(ClashOfRimClientNetworkContext context, ClientShopListingEdit edit, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModRemoveServerShopListingResponseDto>> RemoveAsync(ClashOfRimClientNetworkContext context, string listingId, CancellationToken token);
}

internal interface IClientShopPresentation
{
    string NotAdministrator { get; }
    string Text(string key, params (string Name, object? Value)[] arguments);
    string Admission(LocalMutationAdmission admission, LocalMutationState? pending);
    void PreparePreviews(IReadOnlyList<ModServerShopListingDto> listings);
    void Diagnose(Exception exception);
}
