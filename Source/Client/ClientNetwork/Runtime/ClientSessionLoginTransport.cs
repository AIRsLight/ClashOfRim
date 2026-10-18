using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionLoginTransport : IClientSessionLoginTransport
{
    private readonly IClientProtocolEnvironment environment;
    internal ClientSessionLoginTransport(IClientProtocolEnvironment environment) => this.environment = environment;
    public async Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> LoginAsync(ClashOfRimClientNetworkContext context,
        string purpose, CancellationToken token, Action<ModLoginResponseDto> captureReceipt)
    {
        using var http = new HttpClient();
        var authentication = new ClientAuthenticationEnvironment();
        return await new ClientSessionProtocol(new ClientProtocolTransport(http, context, environment), authentication, authentication)
            .LoginAsync(context, purpose, null, token, captureReceipt).ConfigureAwait(false);
    }
}
