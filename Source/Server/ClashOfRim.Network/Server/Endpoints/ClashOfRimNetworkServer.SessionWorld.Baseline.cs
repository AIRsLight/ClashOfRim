using static AIRsLight.ClashOfRim.Network.Modules.Compatibility.CompatibilityHandshakeContract;
using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult OverrideCompatibilityBaseline(
        OverrideCompatibilityBaselineRequest request,
        ClashOfRimNetworkState state,
        ILoggerFactory loggerFactory)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        AuthenticationValidationResult auth = state.Accounts.Authenticate(request.UserId, request.SteamAuthTicket, request.Password, nowUtc);
        if (!auth.Accepted || string.IsNullOrWhiteSpace(auth.AuthenticatedUserId))
        {
            return Results.Ok(new OverrideCompatibilityBaselineResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, auth.Message ?? T("Steam.Failed")),
                state.CompatibilityBaseline.Current is null ? null : state.CompatibilityHandshake.Serialize(state.CompatibilityBaseline.Current),
                updatedAtUtc: null));
        }

        string authenticatedUserId = auth.AuthenticatedUserId!.Trim();
        if (!state.WorldConfiguration.IsAdministrator(authenticatedUserId))
        {
            return Results.Ok(new OverrideCompatibilityBaselineResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Compatibility.OverrideAdminOnly")),
                state.CompatibilityBaseline.Current is null ? null : state.CompatibilityHandshake.Serialize(state.CompatibilityBaseline.Current),
                updatedAtUtc: null));
        }

        if (!TryReadCompatibilityManifest(
                request.CompatibilityManifestJson,
                out CompatibilityManifest? manifest,
                out string failure)
            || manifest is null)
        {
            return Results.Ok(new OverrideCompatibilityBaselineResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, failure),
                state.CompatibilityBaseline.Current is null ? null : state.CompatibilityHandshake.Serialize(state.CompatibilityBaseline.Current),
                updatedAtUtc: null));
        }

        CompatibilityBaselineUpdateResult update = state.CompatibilityBaseline.ReplaceBaseline(
            manifest,
            authenticatedUserId,
            nowUtc);
        RuntimeLogger(loggerFactory).LogInformation(
            "兼容清单基线已覆盖：user={UserId} manifest={ManifestId} mods={ModCount} dlcs={DlcCount}",
            authenticatedUserId,
            manifest.ManifestId,
            manifest.Mods.Count,
            manifest.DlcIds.Count);
        return Results.Ok(new OverrideCompatibilityBaselineResponse(
            ProtocolResponse.Ok(T("Compatibility.OverrideSuccess")),
            update.Baseline is null ? null : state.CompatibilityHandshake.Serialize(update.Baseline),
            update.UpdatedAtUtc));
    }

}
