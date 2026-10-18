using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionAuthenticationTransport : IClientSessionAuthentication
{
    public async Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> LoginAsync(ClashOfRimClientNetworkContext context,
        string purpose, string? manifest, CancellationToken token, Action<ModLoginResponseDto>? captureReceipt = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        return await Protocol(http, context).LoginAsync(context, purpose, manifest, token, captureReceipt).ConfigureAwait(false);
    }
    public async Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(ClashOfRimClientNetworkContext context,
        string? sessionId, CancellationToken token, bool bindToSession = true)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        return await Protocol(http, context).LogoutAsync(context, sessionId, token, bindToSession).ConfigureAwait(false);
    }
    private static ClientSessionProtocol Protocol(HttpClient http, ClashOfRimClientNetworkContext context)
    {
        var environment = new ClientAuthenticationEnvironment();
        return new(new ClientProtocolTransport(http, context,
            new ClientProtocolEnvironment((message, owner) => ClashOfRimModNetworkClient.SessionExpired?.Invoke(message, owner))), environment, environment);
    }
}
