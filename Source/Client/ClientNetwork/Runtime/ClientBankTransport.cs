using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Bank;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientBankTransport : IClientBankTransport
{
    public async Task<ClashOfRimClientNetworkResult<ModBankStatusResponseDto>> ReadAsync(
        ClashOfRimClientNetworkContext context, long ticks, int wealth, CancellationToken token)
    {
        using var http = new HttpClient();
        return await Create(http, context).GetBankStatusAsync(ticks, wealth, token);
    }

    public async Task<ClashOfRimClientNetworkResult<ClientBankReply>> SubmitAsync(ClashOfRimClientNetworkContext context,
        ClientBankCommand command, ModSnapshotPackageMetadataDto snapshot, byte[] payload, CancellationToken token)
    {
        using var http = new HttpClient();
        return await Create(http, context).SubmitAsync(command, snapshot, payload, token);
    }

    private static ClientBankProtocol Create(HttpClient http, ClashOfRimClientNetworkContext context) => new(context,
        new ClientProtocolTransport(http, context, new ClientProtocolEnvironment()),
        () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
}
