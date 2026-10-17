using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionReconnectTransport : IClientSessionReconnectTransport
{
    public async Task<ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>> PrepareAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
    {
        using var http = CreateHttp();
        return await new ClashOfRimModNetworkClient(http, context).PrepareWorldSessionAsync(cancellationToken: token);
    }

    public async Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> LoginAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
    {
        using var http = CreateHttp();
        return await new ClashOfRimModNetworkClient(http, context).LoginAsync("session-reconnect", cancellationToken: token);
    }

    public async Task<ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>> ReadSnapshotAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
    {
        using var http = CreateHttp();
        return await new ClashOfRimModNetworkClient(http, context).DownloadLatestSnapshotAsync(cancellationToken: token);
    }

    private static HttpClient CreateHttp() => new() { Timeout = TimeSpan.FromSeconds(30) };
}
