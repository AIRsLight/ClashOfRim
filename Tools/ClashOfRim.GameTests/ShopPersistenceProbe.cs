using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace ClashOfRim.GameTests;

internal sealed record ShopPersistenceEvidence(string ListingsJson, IReadOnlyList<int> BuyerPurchaseCounts);

// Owns the fresh container lifetime; scenarios receive detached persistence evidence.
internal static class ShopPersistenceProbe
{
    internal static async Task<ShopPersistenceEvidence> Read(string root, string listingId, IReadOnlyList<ProtocolIdentity> buyers)
    {
        await using var reopened = ClashOfRimNetworkServer.Build([
            "--contentRoot", Path.Combine(root, "Server"), "--urls", "http://127.0.0.1:0",
            "--Persistence:DataDirectory", Path.Combine(root, "Server", "Data"), "--Authentication:DebugMode", "false",
            "--Localization:Language", "English", "--Logging:LogLevel:Default", "Warning"]);
        var shop = reopened.Services.GetRequiredService<ClashOfRimNetworkState>().ServerShop;
        return new(JsonSerializer.Serialize(shop.List()), buyers.Select(b => shop.GetBuyerPurchaseCount(listingId, b)).ToArray());
    }
}
