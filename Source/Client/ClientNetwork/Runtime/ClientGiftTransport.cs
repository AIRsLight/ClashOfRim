using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Gifts;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientGiftTransport : IClientGiftTransport
{
    public async Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> SubmitAsync(
        ClashOfRimClientNetworkContext context, ClientGiftCommand command, ModSnapshotPackageMetadataDto snapshot,
        byte[] payload, CancellationToken token)
    {
        using var http = new HttpClient();
        var protocol = new ClientGiftProtocol(new ClientProtocolTransport(http, context, new ClientProtocolEnvironment(
            (message, owner) => ClashOfRimModNetworkClient.SessionExpired?.Invoke(message, owner))),
            () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        return await protocol.CreateGiftWithSnapshotAsync(context, command.IdempotencyKey,
            command.Target.UserId, command.Target.ColonyId, null, command.Things, command.Message,
            new ModEventTargetContextDto { WorldObjectId = command.Target.WorldObjectId, MapUniqueId = command.Target.MapId,
                Tile = command.Target.Tile, LandingMode = command.LandingMode }, command.Forced ? "Forced" : null,
            snapshot, payload, token);
    }
}
