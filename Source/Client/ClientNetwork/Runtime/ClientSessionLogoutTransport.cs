using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionLogoutTransport : IClientSessionLogoutTransport
{
    public async Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(ClashOfRimClientNetworkContext context, string sessionId)
    {
        using var http = new HttpClient();
        // Cleanup must survive the session generation that it releases, but remain bounded.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return await new ClashOfRimModNetworkClient(http, context).LogoutDetachedAsync(sessionId, cleanup.Token);
    }
}
