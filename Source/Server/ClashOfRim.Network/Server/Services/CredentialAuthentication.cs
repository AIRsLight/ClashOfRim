using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

// Credential verification has no dependency on HTTP, recovery, world setup or session publication.
internal sealed class CredentialAuthentication(
    OfflineAccountRegistry accounts,
    ISteamAuthTicketValidator steamTickets,
    Func<ClashOfRimServerConfiguration> readConfiguration)
{
    internal PreparedCredentials Prepare(string userId, string? ticket, string? password,
        DateTimeOffset nowUtc, bool createAccount = false)
    {
        var configuration = readConfiguration();
        OfflineAccountRegistry.PreparedAuthentication? offline = null;
        AuthenticationValidationResult auth;
        if (configuration.AuthenticationDebugMode || !string.IsNullOrWhiteSpace(configuration.SteamWebApiKey))
        {
            var steam = steamTickets.Validate(userId, ticket);
            auth = steam.Accepted && !string.IsNullOrWhiteSpace(steam.SteamId)
                ? AuthenticationValidationResult.Accept(steam.SteamId.Trim(), steam.DisplayName)
                : AuthenticationValidationResult.Reject(steam.Message ?? ServerLocalization.Text("Steam.Failed"));
        }
        else
        {
            offline = accounts.PrepareAuthentication(userId, password, nowUtc, createAccount);
            auth = FromOffline(offline.Result);
        }
        if (auth.Accepted && !string.Equals(auth.AuthenticatedUserId, userId?.Trim(), StringComparison.Ordinal))
            auth = AuthenticationValidationResult.Reject(ServerLocalization.Text("Auth.Failed"));

        AuthenticationValidationResult Validate(DateTimeOffset at, bool commit)
        {
            if (!auth.Accepted) return auth;
            var current = readConfiguration();
            if (current.AuthenticationDebugMode != configuration.AuthenticationDebugMode
                || current.SteamWebApiKey != configuration.SteamWebApiKey || current.SteamAppId != configuration.SteamAppId)
                return AuthenticationValidationResult.Reject(ServerLocalization.Text("Auth.Failed"));
            return offline is null ? auth : FromOffline(offline.Validate(at, commit));
        }
        return new(auth, Validate);
    }

    internal sealed record PreparedCredentials(AuthenticationValidationResult Result,
        Func<DateTimeOffset, bool, AuthenticationValidationResult> Validate)
    {
        internal ProtocolResponse? Revalidate() => Validate(DateTimeOffset.UtcNow, false).Rejection;
        internal AuthenticationValidationResult Commit(DateTimeOffset at) => Validate(at, true);
    }

    private static AuthenticationValidationResult FromOffline(OfflineAccountAuthenticationResult result)
        => result.Accepted && !string.IsNullOrWhiteSpace(result.UserId)
            ? AuthenticationValidationResult.Accept(result.UserId, result.DisplayName)
            : AuthenticationValidationResult.Reject(LocalizeOfflineFailure(result.Message),
                result.Message == OfflineAccountRegistry.MissingUserKey ? ProtocolErrorCode.AccountNotFound : ProtocolErrorCode.Unauthorized);

    internal static string LocalizeOfflineFailure(string? failure) => failure switch
    {
        OfflineAccountRegistry.MissingUserKey => ServerLocalization.Text("OfflineAuth.MissingUser"),
        OfflineAccountRegistry.InvalidPasswordKey => ServerLocalization.Text("OfflineAuth.InvalidPassword"),
        OfflineAccountRegistry.RateLimitedKey => ServerLocalization.Text("OfflineAuth.RateLimited"),
        null => ServerLocalization.Text("OfflineAuth.Failed"),
        string value when string.IsNullOrWhiteSpace(value) => ServerLocalization.Text("OfflineAuth.Failed"),
        _ => failure
    };
}

internal sealed record AuthenticationValidationResult(bool Accepted, string? AuthenticatedUserId,
    string? DisplayName, string? Message, ProtocolErrorCode ErrorCode)
{
    internal ProtocolResponse? Rejection => Accepted ? null
        : ProtocolResponse.Reject(ErrorCode, Message ?? ServerLocalization.Text("Auth.Failed"));
    internal static AuthenticationValidationResult Accept(string userId, string? displayName = null)
        => new(true, userId, displayName, null, ProtocolErrorCode.None);
    internal static AuthenticationValidationResult Reject(string message, ProtocolErrorCode errorCode = ProtocolErrorCode.Unauthorized)
        => new(false, null, null, message, errorCode);
}
