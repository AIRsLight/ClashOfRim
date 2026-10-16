using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    // Expensive credential verification runs before taking the shared mutation gate.
    // The proof is valid only for this identity and the captured account/auth policy.
    private static PreparedEntryAuthentication PrepareEntryAuthentication(
        ClashOfRimNetworkState state, string userId, string? ticket, string? password, bool createAccount = false)
    {
        var configuration = state.ServerConfiguration;
        OfflineAccountRegistry.PreparedAuthentication? offline = null;
        AuthenticationValidationResult auth;
        if (configuration.AuthenticationDebugMode || !string.IsNullOrWhiteSpace(configuration.SteamWebApiKey))
        {
            var steam = state.SteamAuthTickets.Validate(userId, ticket);
            auth = steam.Accepted && !string.IsNullOrWhiteSpace(steam.SteamId)
                ? AuthenticationValidationResult.Accept(steam.SteamId.Trim(), steam.DisplayName)
                : AuthenticationValidationResult.Reject(steam.Message ?? T("Steam.Failed"));
        }
        else
        {
            offline = state.OfflineAccounts.PrepareAuthentication(userId, password, DateTimeOffset.UtcNow, createAccount);
            auth = ToAuthenticationValidation(offline.Result);
        }
        if (auth.Accepted && !string.Equals(auth.AuthenticatedUserId, userId.Trim(), StringComparison.Ordinal))
            auth = AuthenticationValidationResult.Reject(T("Auth.Failed"));

        ProtocolResponse? Validate(bool commit)
        {
            if (!auth.Accepted) return ProtocolResponse.Reject(auth.ErrorCode, auth.Message ?? T("Auth.Failed"));
            var current = state.ServerConfiguration;
            if (current.AuthenticationDebugMode != configuration.AuthenticationDebugMode
                || current.SteamWebApiKey != configuration.SteamWebApiKey || current.SteamAppId != configuration.SteamAppId)
                return ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed"));
            if (offline is not null)
            {
                var check = ToAuthenticationValidation(offline.Validate(DateTimeOffset.UtcNow, commit));
                if (!check.Accepted) return ProtocolResponse.Reject(check.ErrorCode, check.Message ?? T("Auth.Failed"));
            }
            return null;
        }
        return new(auth, Validate);
    }

    private sealed record PreparedEntryAuthentication(AuthenticationValidationResult Result, Func<bool, ProtocolResponse?> Validate);
}
