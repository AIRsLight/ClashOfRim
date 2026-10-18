using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.Accounts;

internal static class AccountEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, AccountService accounts) =>
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ChangeOfflinePassword).Route,
            (ChangeOfflinePasswordRequest request) => Results.Ok(accounts.ChangePassword(request)));
}
