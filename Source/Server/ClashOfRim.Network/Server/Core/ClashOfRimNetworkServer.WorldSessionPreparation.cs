using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult PrepareAuthenticatedWorldSession(PrepareWorldSessionRequest request, ClashOfRimNetworkState state)
    {
        var authenticationConfiguration = state.ServerConfiguration;
        OfflineAccountRegistry.PreparedAuthentication? offline = null;
        AuthenticationValidationResult auth;
        if (authenticationConfiguration.AuthenticationDebugMode || !string.IsNullOrWhiteSpace(authenticationConfiguration.SteamWebApiKey))
        {
            var steam = state.SteamAuthTickets.Validate(request.UserId, request.SteamAuthTicket);
            auth = steam.Accepted && !string.IsNullOrWhiteSpace(steam.SteamId)
                ? AuthenticationValidationResult.Accept(steam.SteamId.Trim(), steam.DisplayName)
                : AuthenticationValidationResult.Reject(steam.Message ?? T("Steam.Failed"));
        }
        else
        {
            offline = state.OfflineAccounts.PrepareAuthentication(request.UserId, request.Password, DateTimeOffset.UtcNow, request.CreateAccountIfMissing);
            auth = ToAuthenticationValidation(offline.Result);
        }
        if (!auth.Accepted) return RejectWorldSession(ProtocolResponse.Reject(auth.ErrorCode, auth.Message ?? T("Auth.Failed")));
        string userId = auth.AuthenticatedUserId!;
        if (!string.Equals(userId, request.UserId.Trim(), StringComparison.Ordinal))
            return RejectWorldSession(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")));

        var input = PrepareCompatibilityHandshakeInput(request.CompatibilityManifestJson, request.CompatibilityManifestId, request.CompatibilityManifestSummaryJson);
        CompatibilityHandshakeResult? compatibility = null;
        ProtocolResponse? Authorize()
        {
            compatibility = null;
            var current = state.ServerConfiguration;
            if (current.AuthenticationDebugMode != authenticationConfiguration.AuthenticationDebugMode
                || current.SteamWebApiKey != authenticationConfiguration.SteamWebApiKey || current.SteamAppId != authenticationConfiguration.SteamAppId)
                return ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed"));
            if (offline is not null)
            {
                var checkedAuth = ToAuthenticationValidation(offline.Validate(DateTimeOffset.UtcNow));
                if (!checkedAuth.Accepted) return ProtocolResponse.Reject(checkedAuth.ErrorCode, checkedAuth.Message ?? T("Auth.Failed"));
            }
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
        if (offline is not null)
        {
            var committed = ToAuthenticationValidation(offline.Validate(DateTimeOffset.UtcNow, commit: true));
            if (!committed.Accepted) return RejectWorldSession(ProtocolResponse.Reject(committed.ErrorCode, committed.Message ?? T("Auth.Failed")));
        }
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
