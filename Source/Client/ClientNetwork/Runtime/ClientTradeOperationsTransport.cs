using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Packages;
using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Pawns;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientTradeOperationsTransport : IClientTradeOperationsTransport
{
    public async Task HydrateAsync(ClashOfRimClientNetworkContext context, IReadOnlyList<ModThingReferenceDto> things, CancellationToken token)
    {
        using var http = new HttpClient();
        var reader = new ClientPackageReader(Transport(http, context), context, NotConfigured);
        await PawnPackageHydration.HydrateThingPawnPackagesAsync(reader, things, token);
        await PawnPackageHydration.HydrateThingStatePackagesAsync(reader, things, token);
    }
    public async Task<ClashOfRimClientNetworkResult<ClientTradeReceipt>> SubmitAsync(ClashOfRimClientNetworkContext context, ClientTradeCommand command,
        ModSnapshotPackageMetadataDto package, byte[] payload, CancellationToken token)
    {
        using var http = new HttpClient();
        var wire = Transport(http, context);
        var writer = new ClientPackageWriter(wire, context, NotConfigured);
        var protocol = new ClientTradeProtocol(wire, NotConfigured);
        var order = command.Order;
        var things = command.Operation == ClientTradeOperation.Create ? order.OfferedThings.Concat(order.RequestedThings).ToList() : command.Delivered;
        // The same command key also owns external packages. Partial package uploads
        // retain returned IDs in the private command and resume with the same keys.
        await PawnPackageTransferService.StoreThingPawnPackagesAsync(writer, things, command.Key, token);
        await PawnPackageTransferService.StoreThingStatePackagesAsync(writer, things, command.Key, token);
        if (command.Operation == ClientTradeOperation.Create)
        {
            var result = await protocol.CreateTradeOrderWithSnapshotAsync(context, command.Key, order.OfferedThings, order.RequestedThings,
                order.FeeSilver, order.AllowSelfPickup, order.AllowServerDropPod, package, payload, token);
            return result.Success && result.Response is { } response
                ? ClashOfRimClientNetworkResult<ClientTradeReceipt>.Ok(new(response.Result, response.EventId, response.AppliedSnapshotId, response.NextLineageToken))
                : ClashOfRimClientNetworkResult<ClientTradeReceipt>.Failed(result.ErrorCode ?? "", result.Message ?? "");
        }
        else
        {
            var result = await protocol.FulfillTradeOrderWithSnapshotAsync(context, command.Key, order.EventId, order.ViewerAcceptedMemoEventId ?? "",
                command.Delivered, command.Operation == ClientTradeOperation.SelfDelivery ? "SelfDelivery" : "ServerDropPod", package, payload, token);
            return result.Success && result.Response is { } response
                ? ClashOfRimClientNetworkResult<ClientTradeReceipt>.Ok(new(response.Result, response.ExchangeEventId, response.AppliedSnapshotId, response.NextLineageToken,
                    response.AcceptorDeliveryEventId, response.OwnerDeliveryEventId, response.MissingRequirements))
                : ClashOfRimClientNetworkResult<ClientTradeReceipt>.Failed(result.ErrorCode ?? "", result.Message ?? "");
        }
    }
    public async Task<ClashOfRimClientNetworkResult<ModTradeOrderFeeQuoteResponseDto>> QuoteAsync(ClashOfRimClientNetworkContext context,
        IReadOnlyList<ModThingReferenceDto> offered, IReadOnlyList<ModThingReferenceDto> requested, CancellationToken token)
    {
        using var http = new HttpClient();
        return await new ClientTradeProtocol(Transport(http, context), NotConfigured).QuoteTradeOrderFeeAsync(context, offered, requested, token);
    }
    private static ClientProtocolTransport Transport(HttpClient http, ClashOfRimClientNetworkContext context) => new(http, context,
        new ClientProtocolEnvironment());
    private static string NotConfigured() => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured");
}
