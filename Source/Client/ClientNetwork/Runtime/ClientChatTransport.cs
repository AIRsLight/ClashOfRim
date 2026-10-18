using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Chat;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientChatTransport : IClientChatTransport
{
    private readonly IClientProtocolEnvironment environment;
    internal ClientChatTransport(IClientProtocolEnvironment environment) => this.environment = environment;
    private ClientChatProtocol Protocol(HttpClient http, ClashOfRimClientNetworkContext context)
        => new(new ClientProtocolTransport(http, context, environment), () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
    public async Task<ClashOfRimClientNetworkResult<ModSendChatMessageResponseDto>> SendAsync(ClashOfRimClientNetworkContext context, string? target, string text, CancellationToken token)
    { using var http = new HttpClient(); return await Protocol(http, context).SendAsync(context, target, text, token).ConfigureAwait(false); }
    public async Task<ClashOfRimClientNetworkResult<ModListChatMessagesResponseDto>> ListAsync(ClashOfRimClientNetworkContext context, long after, int limit, CancellationToken token)
    { using var http = new HttpClient(); return await Protocol(http, context).ListAsync(context, after, limit, token).ConfigureAwait(false); }
}
