using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Support;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSupportTransport : IClientSupportSendingTransport, IClientSupportReturnTransport
{
    public async Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> SendAsync(ClashOfRimClientNetworkContext context,
        ClientSupportCommand command, ModSnapshotPackageMetadataDto snapshot, byte[] payload, CancellationToken token)
    {
        using var http = new HttpClient();
        var protocol = Protocol(http, context);
        return await protocol.CreateSupportPawnWithSnapshotAsync(command.IdempotencyKey, command.Target.UserId, command.Target.ColonyId, null,
            command.Pawn.GlobalKey, command.Pawn.Name, command.Pawn.Package.Reference!, command.Pawn.Package,
            new ModEventTargetContextDto { WorldObjectId = command.Target.WorldObjectId, MapUniqueId = command.Target.MapId,
                Tile = command.Target.Tile, LandingMode = "MapEdge" }, command.Pawn.SourceTile, command.Pawn.CaravanId,
            command.Selection.Permanent, command.Selection.Permanent ? null : command.Selection.Days, null,
            command.Target.AutoReturnOnSettlement, snapshot, payload, token);
    }

    public async Task<ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>> FinishAsync(ClashOfRimClientNetworkContext context,
        ClientSupportReturnCommand command, CancellationToken token)
    {
        using var http = new HttpClient();
        var payload = command.Payload;
        return await Protocol(http, context).FinishSupportPawnAsync(command.IdempotencyKey, payload.EventId, payload.FinishReason,
            payload.GlobalKey, payload.PawnName, payload.Dead, payload.Package, token);
    }

    private static ClientSupportProtocol Protocol(HttpClient http, ClashOfRimClientNetworkContext context) =>
        new(new ClientProtocolTransport(http, context, new ClientProtocolEnvironment()), context,
            () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
}
