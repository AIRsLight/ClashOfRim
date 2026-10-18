using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal static class WorldEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, WorldSessionPreparation preparation, WorldConfigurationRead read)
    {
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.PrepareWorldSession).Route,
            (PrepareWorldSessionRequest request) => Results.Ok(preparation.Prepare(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetWorldConfiguration).Route,
            (GetWorldConfigurationRequest request) => Results.Ok(read.Get(request)));
    }
}
