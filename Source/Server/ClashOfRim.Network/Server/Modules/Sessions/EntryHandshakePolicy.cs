using AIRsLight.ClashOfRim.Network.Modules.Compatibility;
using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Sessions;

internal enum EntryPurpose { Login, WorldPreparation }

internal sealed class EntryHandshakePolicy(AdminControlRegistry admin, WorldConfigurationRegistry world,
    LoginSessionRegistry sessions, CompatibilityHandshakeService compatibility)
{
    internal PreparedEntryHandshake Prepare(CredentialAuthentication.PreparedCredentials credentials,
        CompatibilityHandshakeInput input, EntryPurpose purpose) => new(this, credentials, input, purpose);

    private AdminControlRegistry Admin => admin;
    private WorldConfigurationRegistry World => world;
    private LoginSessionRegistry Sessions => sessions;
    private CompatibilityHandshakeService Compatibility => compatibility;

    // One authorization policy is re-run at every recovery boundary. The purpose only selects
    // the two real differences: initial administrator bootstrap and active login-session exclusion.
    internal sealed class PreparedEntryHandshake(EntryHandshakePolicy policy,
        CredentialAuthentication.PreparedCredentials credentials, CompatibilityHandshakeInput input, EntryPurpose purpose)
    {
        public string UserId => credentials.Result.AuthenticatedUserId!;
        public string DisplayName => string.IsNullOrWhiteSpace(credentials.Result.DisplayName) ? UserId : credentials.Result.DisplayName!;
        public CompatibilityHandshakeResult? Compatibility { get; private set; }

        public ProtocolResponse? Authorize()
        {
            Compatibility = null;
            if (credentials.Revalidate() is { } rejected) return rejected;
            if (policy.Admin.IsBanned(UserId)) return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Login.Banned"));
            var session = policy.World.GetSessionState(UserId);
            bool bootstrap = purpose == EntryPurpose.WorldPreparation && session.AdministratorUserId is null;
            if (policy.Admin.MaintenanceLoginLocked && !session.IsAdministrator && !bootstrap)
                return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected,
                    string.IsNullOrWhiteSpace(policy.Admin.MaintenanceReason) ? ServerLocalization.Text("Login.MaintenanceLocked") : policy.Admin.MaintenanceReason!);
            if (purpose == EntryPurpose.Login && !policy.Sessions.CanCreate(UserId, DateTimeOffset.UtcNow))
                return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Login.AlreadyOnline"));
            return CompareCompatibility(DateTimeOffset.UtcNow, publish: false);
        }

        public ProtocolResponse? CommitAccount(DateTimeOffset at) => credentials.Commit(at).Rejection;
        public ProtocolResponse? PublishBaseline(DateTimeOffset at) => CompareCompatibility(at, publish: true);

        private ProtocolResponse? CompareCompatibility(DateTimeOffset at, bool publish)
        {
            Compatibility = policy.Compatibility.Evaluate(credentials.Result, input, at, publish);
            return Compatibility.Result.Accepted ? null : Compatibility.Result;
        }
    }
}
