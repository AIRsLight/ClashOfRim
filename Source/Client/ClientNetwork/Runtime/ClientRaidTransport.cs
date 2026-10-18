using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientRaidTransport : IClientRaidPreparationTransport
{
    public async Task<ClashOfRimClientNetworkResult<ModPrepareRaidResponseDto>> PrepareAsync(ClashOfRimClientNetworkContext context,
        ClientRaidPreparationCommand command, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var transport = new ClientProtocolTransport(http, context, new ClientProtocolEnvironment(
            (message, owner) => ClashOfRimModNetworkClient.SessionExpired?.Invoke(message, owner)));
        var protocol = new ClientRaidProtocol(transport, context, () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        var target = command.Target;
        return await protocol.PrepareRaidAsync(command.IdempotencyKey, target.UserId, target.ColonyId, target.WorldObjectId, target.MapId,
            target.Tile, command.Hostile, command.OpponentKind, token).ConfigureAwait(false);
    }
}
