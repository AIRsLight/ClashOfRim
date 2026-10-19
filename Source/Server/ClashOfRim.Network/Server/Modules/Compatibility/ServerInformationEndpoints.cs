using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.Compatibility;

internal static class ServerInformationEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, ServerInformation information)
    {
        endpoints.MapGet("/server/plugins", () => Results.Ok(information.ListPlugins()));
        endpoints.MapGet(ProtocolContractManifest.Find(ProtocolMessageKind.ServerHello).Route, () => Results.Ok(information.Hello()));
    }
}
