using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.Events;

internal static class EventReadEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, EventDeliveryService delivery)
    {
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.PullPendingEvents).Route,
            (PullPendingEventsRequest request) => Results.Ok(delivery.PullPendingEvents(request)))
            .WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.PullEventDetails).Route,
            (PullEventDetailsRequest request) => Results.Ok(delivery.PullEventDetails(request)))
            .WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.WaitForEvents).Route,
            async (WaitForEventsRequest request, CancellationToken cancellationToken) =>
                Results.Ok(await delivery.WaitForEvents(request, cancellationToken)))
            .WithMetadata(EndpointSnapshotRecovery.Instance);
    }
}
