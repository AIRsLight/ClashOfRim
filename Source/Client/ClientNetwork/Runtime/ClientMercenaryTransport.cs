using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientMercenaryTransport : IClientMercenaryQuoteTransport
{
    public async Task<ClashOfRimClientNetworkResult<ModMercenaryQuoteResponseDto>> QuoteAsync(ClashOfRimClientNetworkContext context,
        long ticks, string skill, int level, int days, CancellationToken token)
    { using var http = new HttpClient(); return await Create(http, context).QuoteMercenaryAsync(ticks, skill, level, days, token); }

    public async Task<ClashOfRimClientNetworkResult<ModMercenaryGuardQuoteResponseDto>> QuoteGuardAsync(ClashOfRimClientNetworkContext context,
        long ticks, string tier, CancellationToken token)
    { using var http = new HttpClient(); return await Create(http, context).QuoteMercenaryGuardAsync(ticks, tier, token); }

    private static ClientMercenaryProtocol Create(HttpClient http, ClashOfRimClientNetworkContext context) => new(
        new ClientProtocolTransport(http, context, new ClientProtocolEnvironment(
            (message, owner) => ClashOfRimModNetworkClient.SessionExpired?.Invoke(message, owner))), context,
        () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
}
