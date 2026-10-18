using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Achievements;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientAchievementTransport : IClientAchievementTransport
{
    private readonly IClientProtocolEnvironment environment;
    internal ClientAchievementTransport(IClientProtocolEnvironment environment) => this.environment = environment;
    public async Task<ClashOfRimClientNetworkResult<ModListAchievementsResponseDto>> ListAsync(
        ClashOfRimClientNetworkContext context, ClientAchievementTarget target, CancellationToken token)
    {
        using var http = new HttpClient();
        var protocol = new ClientAchievementProtocol(new ClientProtocolTransport(http, context, environment),
            () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        return await protocol.ListAsync(context, target, token);
    }
}
