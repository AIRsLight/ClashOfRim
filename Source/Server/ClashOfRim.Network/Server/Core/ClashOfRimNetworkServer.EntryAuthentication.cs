using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static CredentialAuthentication.PreparedCredentials PrepareEntryAuthentication(
        ClashOfRimNetworkState state, string userId, string? ticket, string? password,
        bool createAccount = false, DateTimeOffset? nowUtc = null)
        => new CredentialAuthentication(state.OfflineAccounts, state.SteamAuthTickets, () => state.ServerConfiguration)
            .Prepare(userId, ticket, password, nowUtc ?? DateTimeOffset.UtcNow, createAccount);

    // Immediate callers use the same credential proof; they do not acquire entry/recovery policy implicitly.
    private static AuthenticationValidationResult ValidateAuthentication(ClashOfRimNetworkState state,
        string userId, string? steamAuthTicket, string? password, DateTimeOffset nowUtc, bool createAccountIfMissing = false)
        => PrepareEntryAuthentication(state, userId, steamAuthTicket, password, createAccountIfMissing, nowUtc).Commit(nowUtc);

    private enum EntryPurpose { Login, WorldPreparation }

    // One authorization policy is re-run at every recovery boundary. The purpose only selects
    // the two real differences: initial administrator bootstrap and active login-session exclusion.
    private sealed class PreparedEntryHandshake(ClashOfRimNetworkState state,
        CredentialAuthentication.PreparedCredentials credentials, CompatibilityHandshakeInput input, EntryPurpose purpose)
    {
        public string UserId => credentials.Result.AuthenticatedUserId!;
        public string DisplayName => string.IsNullOrWhiteSpace(credentials.Result.DisplayName) ? UserId : credentials.Result.DisplayName!;
        public CompatibilityHandshakeResult? Compatibility { get; private set; }

        public ProtocolResponse? Authorize()
        {
            Compatibility = null;
            if (credentials.Revalidate() is { } rejected) return rejected;
            if (state.AdminControl.IsBanned(UserId)) return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Login.Banned"));
            var session = state.WorldConfiguration.GetSessionState(UserId);
            bool bootstrap = purpose == EntryPurpose.WorldPreparation && session.AdministratorUserId is null;
            if (state.AdminControl.MaintenanceLoginLocked && !session.IsAdministrator && !bootstrap)
                return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected,
                    string.IsNullOrWhiteSpace(state.AdminControl.MaintenanceReason) ? T("Login.MaintenanceLocked") : state.AdminControl.MaintenanceReason!);
            if (purpose == EntryPurpose.Login && !state.LoginSessions.CanCreate(UserId, DateTimeOffset.UtcNow))
                return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Login.AlreadyOnline"));
            return CompareCompatibility(DateTimeOffset.UtcNow, publish: false);
        }

        public ProtocolResponse? CommitAccount(DateTimeOffset at) => credentials.Commit(at).Rejection;
        public ProtocolResponse? PublishBaseline(DateTimeOffset at) => CompareCompatibility(at, publish: true);

        private ProtocolResponse? CompareCompatibility(DateTimeOffset at, bool publish)
        {
            Compatibility = EvaluateCompatibilityHandshake(state, credentials.Result, input, at, publish);
            return Compatibility.Result.Accepted ? null : Compatibility.Result;
        }
    }
}
