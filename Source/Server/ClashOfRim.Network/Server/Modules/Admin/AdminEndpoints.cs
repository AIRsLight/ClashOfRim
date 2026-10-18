using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.Admin;

internal static class AdminEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, AdministratorBaseline baseline)
    {
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetAdminBaselineRequirements).Route,
            (GetAdminBaselineRequirementsRequest request) => Results.Ok(baseline.Requirements(request)))
            .WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.SubmitAdminBaseline).Route,
            (SubmitAdminBaselineRequest request) => Results.Ok(baseline.Submit(request)))
            .WithMetadata(EndpointSnapshotRecovery.Instance);
    }
}
