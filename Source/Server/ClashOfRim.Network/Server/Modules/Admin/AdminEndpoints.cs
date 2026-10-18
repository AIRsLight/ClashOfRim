using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.Admin;

internal static class AdminEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, AdministratorBaseline baseline,
        AdministratorConfiguration configuration, CompatibilityBaselineOverride compatibility)
    {
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.AdminStatus).Route,
            (AdminStatusRequest request) => Results.Ok(configuration.Status(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.AdminUpdateConfiguration).Route,
            (AdminUpdateConfigurationRequest request) => Results.Ok(configuration.Update(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.OverrideCompatibilityBaseline).Route,
            (OverrideCompatibilityBaselineRequest request) => Results.Ok(compatibility.Replace(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetAdminBaselineRequirements).Route,
            (GetAdminBaselineRequirementsRequest request) => Results.Ok(baseline.Requirements(request)))
            .WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.SubmitAdminBaseline).Route,
            (SubmitAdminBaselineRequest request) => Results.Ok(baseline.Submit(request)))
            .WithMetadata(EndpointSnapshotRecovery.Instance);
    }
}
