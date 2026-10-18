using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Accounts;

// Account credentials do not recover snapshots, publish sessions or change world policy.
internal sealed class AccountService(OfflineAccountRegistry accounts, AuthTokenRegistry tokens,
    ISteamAuthTicketValidator steamTickets, Func<ClashOfRimServerConfiguration> configuration)
{
    private readonly CredentialAuthentication credentials = new(accounts, steamTickets, configuration);

    internal CredentialAuthentication.PreparedCredentials PrepareCredentials(string userId, string? ticket, string? password,
        DateTimeOffset nowUtc, bool createAccount = false) => credentials.Prepare(userId, ticket, password, nowUtc, createAccount);

    internal AuthenticationValidationResult Authenticate(string userId, string? ticket, string? password, DateTimeOffset nowUtc,
        bool createAccount = false) => PrepareCredentials(userId, ticket, password, nowUtc, createAccount).Commit(nowUtc);

    internal ChangeOfflinePasswordResponse ChangePassword(ChangeOfflinePasswordRequest request, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        if (!tokens.IsValid(request.AuthToken, request.UserId, request.ColonyId, at))
            return new(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, ServerLocalization.Text("OfflineAuth.PasswordChangeUnauthorized")));
        bool changed = accounts.ChangePassword(request.UserId, request.CurrentPassword, request.NewPassword, at, out string failure);
        return new(changed ? ProtocolResponse.Ok(ServerLocalization.Text("OfflineAuth.PasswordChanged"))
            : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, CredentialAuthentication.LocalizeOfflineFailure(failure)));
    }
}
