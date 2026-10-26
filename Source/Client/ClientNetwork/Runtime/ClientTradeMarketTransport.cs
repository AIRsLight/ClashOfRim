using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientTradeMarketTransport : IClientTradeMarketTransport
{
    public async Task<ClashOfRimClientNetworkResult<ModListTradeOrdersResponseDto>> ListAsync(ClashOfRimClientNetworkContext context, string scope, int offset, int limit, CancellationToken token)
    { using var http = new HttpClient(); return await Create(http, context).ListTradeOrdersAsync(context, scope, offset, limit, token); }
    public async Task<ClashOfRimClientNetworkResult<ModAcceptTradeOrderResponseDto>> AcceptAsync(ClashOfRimClientNetworkContext context, string key, string eventId, bool postage, CancellationToken token)
    { using var http = new HttpClient(); return await Create(http, context).AcceptTradeOrderAsync(context, key, eventId, postage, token); }
    public async Task<ClashOfRimClientNetworkResult<ModCloseTradeOrderResponseDto>> CancelAsync(ClashOfRimClientNetworkContext context, string key, string eventId, string reason, CancellationToken token)
    { using var http = new HttpClient(); return await Create(http, context).CancelTradeOrderAsync(context, key, eventId, reason, token); }
    public async Task<ClashOfRimClientNetworkResult<ModCloseTradeOrderResponseDto>> ClaimAsync(ClashOfRimClientNetworkContext context, string key, string eventId, CancellationToken token)
    { using var http = new HttpClient(); return await Create(http, context).ClaimTradeOrderAsync(context, key, eventId, token); }
    private static ClientTradeProtocol Create(HttpClient http, ClashOfRimClientNetworkContext context) => new(
        new ClientProtocolTransport(http, context, new ClientProtocolEnvironment()),
        () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
}
