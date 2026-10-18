using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Support;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSupportTransport : IClientSupportSendingTransport
{
    public async Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> SendAsync(ClashOfRimClientNetworkContext context,
        ClientSupportCommand command, ModSnapshotPackageMetadataDto snapshot, byte[] payload, CancellationToken token)
    {
        using var http = new HttpClient();
        var protocol = new ClientSupportProtocol(new ClientProtocolTransport(http, context, new ClientProtocolEnvironment(
            (message, owner) => ClashOfRimModNetworkClient.SessionExpired?.Invoke(message, owner))), context,
            () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        return await protocol.CreateSupportPawnWithSnapshotAsync(command.IdempotencyKey, command.Target.UserId, command.Target.ColonyId, null,
            command.Pawn.GlobalKey, command.Pawn.Name, command.Pawn.Package.Reference!, command.Pawn.Package,
            new ModEventTargetContextDto { WorldObjectId = command.Target.WorldObjectId, MapUniqueId = command.Target.MapId,
                Tile = command.Target.Tile, LandingMode = "MapEdge" }, command.Pawn.SourceTile, command.Pawn.CaravanId,
            command.Selection.Permanent, command.Selection.Permanent ? null : command.Selection.Days, null,
            command.Target.AutoReturnOnSettlement, snapshot, payload, token);
    }
}
