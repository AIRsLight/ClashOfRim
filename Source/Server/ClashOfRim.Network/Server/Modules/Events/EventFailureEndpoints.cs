using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.Events;

internal static class EventFailureEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, EventFailureReports reports) =>
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ReportEventApplicationFailure).Route,
            (ReportEventApplicationFailureRequest request) => Results.Ok(reports.Report(request)))
            .WithMetadata(EndpointSnapshotRecovery.Instance);
}
