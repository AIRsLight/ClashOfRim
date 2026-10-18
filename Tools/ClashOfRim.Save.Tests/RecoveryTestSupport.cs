using System.Text;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;

// Shared test mechanics only. Each suite owns its business setup and assertions.
internal static class RecoveryTestSupport
{
    internal sealed class Slot(IJsonPersistenceSlot? inner = null) : IJsonPersistenceSlot
    {
        private string? value;
        public bool Fail;
        public int Writes;
        public Action<string>? Before;
        public string? Read() => inner is null ? value : inner.Read();
        public void Write(string json)
        {
            Before?.Invoke(json);
            if (Fail) throw new IOException("Injected persistence failure.");
            if (inner is null) value = json; else inner.Write(json);
            Writes++;
        }
    }

    internal sealed class SteamValidator : ISteamAuthTicketValidator
    {
        public string Identity = "receiver";
        public bool Deny;
        public int Calls;
        public Action? Before;
        public SteamAuthTicketValidationResult Validate(string userId, string? ticket)
        {
            Calls++; Before?.Invoke();
            return Deny ? SteamAuthTicketValidationResult.Reject("denied") : SteamAuthTicketValidationResult.Accept(Identity);
        }
    }

    internal static ServerPluginRegistry PublicationPlugins(string id, Action callback)
        => new ServerPluginRegistry().WithBuiltInPlugins([new(id, "test", "1", "test", "", [])
            { SnapshotPostUploadProcessors = [new PublicationProbe(id, callback)] }]);

    private sealed class PublicationProbe(string id, Action callback) : IInlineSnapshotPostUploadProcessor
    {
        public string Id => id;
        public int Order => 350;
        public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.EventReconciliation;
        public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline;
        public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Inline;
        public bool Supports(SnapshotPostUploadKind kind) => true;
        public void Process(SnapshotPostUploadContext context) => callback();
    }

    internal static HttpResponseMessage PostInvalidBody(HttpClient client, ProtocolMessageKind kind, string mode)
        => client.PostAsync(ProtocolContractManifest.Find(kind).Route,
            new StringContent(mode == "malformed" ? "{\"" : mode == "null" ? "null" : "", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
}

internal static class EntryTestSupport
{
    internal static CompatibilityManifest Manifest(string id = "base", string version = "1.7")
        => new() { ManifestId = id, ProtocolVersion = ProtocolApiVersion.Current, RimWorldVersion = version };

    internal static void ChangeEntryState(ClashOfRimNetworkState state, string change)
    {
        if (change is "password" or "same-password") state.OfflineAccounts.ResetPassword("receiver", change == "password" ? "changed" : "pw", DateTimeOffset.UtcNow, out _);
        if (change == "lockout") for (int i = 0; i < 5; i++) state.OfflineAccounts.Authenticate("receiver", "wrong", DateTimeOffset.UtcNow);
        if (change == "auth-mode") state.UpdateServerConfiguration(new(authenticationDebugMode: true));
        if (change == "steam-key") state.UpdateServerConfiguration(new(steamWebApiKey: "changed"));
        if (change == "app-id") state.UpdateServerConfiguration(new(steamAppId: 1));
        if (change == "ban") state.AdminControl.Ban("receiver");
        if (change is "maintenance" or "role") state.AdminControl.SetMaintenanceLoginLocked(true, "maintenance");
        if (change == "role") state.WorldConfiguration.RevokeAdministrator("receiver");
        if (change is "mismatch" or "compatible") state.CompatibilityBaseline.ReplaceBaseline(Manifest("updated", change == "mismatch" ? "different" : "1.7"), "admin", DateTimeOffset.UtcNow);
    }

    internal static void RestoreEntryState(HttpSnapshotRecoveryTests.Fixture fixture)
    {
        var state = fixture.State;
        fixture.Store.FailPreparation = fixture.Store.FailPublication = false;
        state.UpdateServerConfiguration(new()); state.AdminControl.Unban("receiver"); state.AdminControl.SetMaintenanceLoginLocked(false, null);
        state.OfflineAccounts.ResetPassword("receiver", "pw", DateTimeOffset.UtcNow, out _);
        // Password reset retains lockout; advance the fixture's authentication time past its expiry.
        state.OfflineAccounts.Authenticate("receiver", "pw", DateTimeOffset.UtcNow.AddHours(1));
        state.CompatibilityBaseline.ReplaceBaseline(Manifest(), "admin", DateTimeOffset.UtcNow);
    }
}
