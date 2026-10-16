using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult PrepareAuthenticatedWorldSession(PrepareWorldSessionRequest request, ClashOfRimNetworkState state)
    {
        var prepared = PrepareEntryAuthentication(state, request.UserId, request.SteamAuthTicket, request.Password, request.CreateAccountIfMissing);
        if (prepared.Validate(false) is { } invalid) return RejectWorldSession(invalid);
        var auth = prepared.Result;
        string userId = auth.AuthenticatedUserId!;
        var input = PrepareCompatibilityHandshakeInput(request.CompatibilityManifestJson, request.CompatibilityManifestId, request.CompatibilityManifestSummaryJson);
        CompatibilityHandshakeResult? compatibility = null;
        ProtocolResponse? Authorize()
        {
            compatibility = null;
            if (prepared.Validate(false) is { } rejected) return rejected;
            if (state.AdminControl.IsBanned(userId)) return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Login.Banned"));
            var session = state.WorldConfiguration.GetSessionState(userId);
            if (state.AdminControl.MaintenanceLoginLocked && session.AdministratorUserId is not null && !session.IsAdministrator)
                return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected,
                    string.IsNullOrWhiteSpace(state.AdminControl.MaintenanceReason) ? T("Login.MaintenanceLocked") : state.AdminControl.MaintenanceReason!);
            compatibility = EvaluateCompatibilityHandshake(state, auth, input, DateTimeOffset.UtcNow, publishBaseline: false);
            return compatibility.Result.Accepted ? null : compatibility.Result;
        }

        using var mutation = EnterRecoveredSnapshotMutation(state, Authorize);
        if (mutation.Rejection is { } denied) return RejectWorldSession(denied, compatibility);
        if (prepared.Validate(true) is { } uncommitted) return RejectWorldSession(uncommitted);
        var nowUtc = DateTimeOffset.UtcNow;
        compatibility = EvaluateCompatibilityHandshake(state, auth, input, nowUtc, publishBaseline: true);
        if (!compatibility.Result.Accepted) return RejectWorldSession(compatibility.Result, compatibility);
        return PrepareWorldSessionLocked(request, state, userId, compatibility, nowUtc);
    }

    private static AuthenticationValidationResult ToAuthenticationValidation(OfflineAccountAuthenticationResult result)
        => result.Accepted && !string.IsNullOrWhiteSpace(result.UserId)
            ? AuthenticationValidationResult.Accept(result.UserId, result.DisplayName)
            : AuthenticationValidationResult.Reject(LocalizeOfflineAccountFailure(result.Message),
                result.Message == OfflineAccountRegistry.MissingUserKey ? ProtocolErrorCode.AccountNotFound : ProtocolErrorCode.Unauthorized);

    private static IResult RejectWorldSession(ProtocolResponse rejection, CompatibilityHandshakeResult? compatibility = null)
        => Results.Ok(new PrepareWorldSessionResponse(rejection, false, false, false, null, null,
            serverCompatibilityManifestJson: compatibility?.ServerCompatibilityManifestJson,
            compatibilityIssues: compatibility?.CompatibilityIssues,
            requiresFullCompatibilityManifest: compatibility?.RequiresFullCompatibilityManifest ?? false,
            requestedCompatibilityPackageIds: compatibility?.RequestedCompatibilityPackageIds));
}
