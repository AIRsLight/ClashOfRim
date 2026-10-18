using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionReconnectTransport : IClientSessionReconnectTransport
{
    private readonly IClientProtocolEnvironment protocolEnvironment;
    internal ClientSessionReconnectTransport(IClientProtocolEnvironment environment) => protocolEnvironment = environment;

    public async Task<ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>> PrepareAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
    {
        using var http = CreateHttp();
        var environment = new ClientAuthenticationEnvironment();
        var transport = new ClientProtocolTransport(http, context, protocolEnvironment);
        var authentication = new ClientSessionProtocol(transport, environment, environment);
        var setup = new ClientSetupAuthenticationLease(authentication, transport, environment);
        return await new ClientWorldProtocol(transport, setup, environment, environment, environment.LogWorldEntry).PrepareAsync(context, false, token);
    }

    public async Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> LoginAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
    {
        using var http = CreateHttp();
        var environment = new ClientAuthenticationEnvironment();
        var transport = new ClientProtocolTransport(http, context,
            protocolEnvironment);
        return await new ClientSessionProtocol(transport, environment, environment).LoginAsync(context, "session-reconnect", null, token);
    }

    public async Task<ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>> ReadSnapshotAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
    {
        using var http = CreateHttp();
        var transport = new ClientProtocolTransport(http, context, protocolEnvironment);
        var protocol = new ClientSnapshotDownloadProtocol(transport, context, () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        return await protocol.MetadataAsync(new(context.UserId, context.ColonyId), token);
    }

    private static HttpClient CreateHttp() => new() { Timeout = TimeSpan.FromSeconds(30) };
}
