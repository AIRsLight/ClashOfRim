using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.Diplomacy;

internal static class DiplomacyEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, DiplomacyService diplomacy)
    {
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateDiplomacyEvent).Route,
            (CreateDiplomacyEventRequest request) => Results.Ok(diplomacy.Create(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RespondDiplomacyEvent).Route,
            (RespondDiplomacyEventRequest request) => Results.Ok(diplomacy.Respond(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
    }
}
