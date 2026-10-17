using System.Net.Http;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionLogoutTransport : IClientSessionLogoutTransport
{
    public async Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(ClashOfRimClientNetworkContext context, string sessionId)
    {
        using var http = new HttpClient();
        return await new ClashOfRimModNetworkClient(http, context).LogoutAsync(sessionId);
    }
}
