using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Packages;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Pawns;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientShopCatalogTransport : IClientShopCatalogTransport
{
    public async Task<ClashOfRimClientNetworkResult<ModListServerShopResponseDto>> ListAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
    { using var http = new HttpClient(); return await Create(http, context).ListServerShopAsync(token); }
    public async Task<ClashOfRimClientNetworkResult<ModUpsertServerShopListingResponseDto>> UpsertAsync(ClashOfRimClientNetworkContext context, ClientShopListingEdit edit, CancellationToken token)
    {
        using var http = new HttpClient();
        return await Create(http, context).UpsertServerShopListingAsync(edit.ListingId, edit.ListingKind, edit.Item,
            edit.PriceSilver, edit.StockCount, edit.PriceIncreaseRatio, edit.QualityRequirementMode, edit.HitPointsRequirementMode, token);
    }
    public async Task<ClashOfRimClientNetworkResult<ModRemoveServerShopListingResponseDto>> RemoveAsync(ClashOfRimClientNetworkContext context, string listingId, CancellationToken token)
    { using var http = new HttpClient(); return await Create(http, context).RemoveServerShopListingAsync(listingId, token); }
    public async Task HydrateAsync(ClashOfRimClientNetworkContext context, IReadOnlyList<ModServerShopListingDto> listings, CancellationToken token)
    {
        using var http = new HttpClient();
        var reader = new ClientPackageReader(Transport(http, context), context, NotConfigured);
        var items = listings.Where(l => l.Item is not null).Select(l => l.Item!).ToArray();
        await PawnPackageHydration.HydrateThingPawnPackagesAsync(reader, items, token).ConfigureAwait(false);
        await PawnPackageHydration.HydrateThingStatePackagesAsync(reader, items, token).ConfigureAwait(false);
    }
    private static string NotConfigured() => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured");
    private static ClientShopProtocol Create(HttpClient http, ClashOfRimClientNetworkContext context) => new(Transport(http, context), context, NotConfigured);
    private static ClientProtocolTransport Transport(HttpClient http, ClashOfRimClientNetworkContext context) => new(http, context,
        new ClientProtocolEnvironment((message, owner) => ClashOfRimModNetworkClient.SessionExpired?.Invoke(message, owner)));
}
