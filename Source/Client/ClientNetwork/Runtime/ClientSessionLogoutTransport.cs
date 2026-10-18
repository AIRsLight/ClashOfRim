using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionLogoutTransport : IClientSessionLogoutTransport
{
    public async Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(ClashOfRimClientNetworkContext context, string sessionId)
    {
        using var http = new HttpClient();
        // Cleanup must survive the session generation that it releases, but remain bounded.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var environment = new ClientAuthenticationEnvironment();
        var transport = new ClientProtocolTransport(http, context,
            new ClientProtocolEnvironment());
        return await new ClientSessionProtocol(transport, environment, environment).LogoutAsync(context, sessionId, cleanup.Token, bindToSession: false);
    }
}
