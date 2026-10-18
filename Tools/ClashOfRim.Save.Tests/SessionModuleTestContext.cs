using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Economy;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging.Abstractions;

// Shared session wiring only. Tests own their business setup, faults and assertions.
internal sealed class SessionModuleTestContext
{
    internal readonly object Gate = new();
    internal readonly EconomicModuleTestContext.RecoveryProbe Recovery = new();
    internal readonly RecoveryTestSupport.Slot PlayerSlot = new();
    internal readonly InMemoryColonySnapshotIndexStore Snapshots = new();
    internal readonly InMemoryAuthoritativeEventLedger Ledger = new();
    internal readonly LoginSessionRegistry Sessions = new();
    internal readonly AuthTokenRegistry Tokens = new();
    internal readonly OnlinePresenceRegistry Presence = new();
    internal readonly PlayerOnlineNotificationHub Notifications = new();
    internal readonly EventNotificationHub Events = new();
    internal readonly BankLoanRegistry Loans = new();
    internal readonly PlayerRegistry Players;
    internal readonly PlayerSessionActivity Activity;
    internal readonly SnapshotMutationCoordinator Mutations;
    internal readonly PendingConfirmationReconciler Pending;
    internal readonly RaidQueries Queries;
    internal readonly RaidTimeouts Timeouts;
    internal readonly SessionConnections Connections;
    internal ClashOfRimServerConfiguration Configuration = new();

    internal SessionModuleTestContext()
    {
        Players = new(PlayerSlot); Activity = new(Players, Snapshots);
        Mutations = new(Gate, Recovery);
        var jobs = new SnapshotPostUploadJobRegistry();
        var artifacts = new InMemorySnapshotPostUploadArtifactStore();
        Queries = new(Ledger, () => Configuration);
        var scheduling = new RaidEvidenceScheduling(Ledger, Snapshots, artifacts, jobs, Players, Mutations);
        var offline = new RaidOfflineEvidence(Ledger, Snapshots, jobs, artifacts, Mutations, Queries, scheduling, () => NullLogger.Instance);
        var cleanup = new RaidBattleCleanup(Ledger, Snapshots, jobs, Mutations, Queries, new SnapshotCommitService(), Loans,
            Players, Presence, Events, () => NullLogger.Instance);
        Timeouts = new(Ledger, Snapshots, jobs, Mutations, Presence, Events, offline, cleanup, () => Configuration);
        Pending = new(Loans, new MercenaryContractRegistry(), () => Configuration, Mutations);
        Connections = new(Sessions, Tokens, Presence, Notifications, Activity, Mutations, Pending, Timeouts);
    }
    internal (string Session, string Token) Issue()
    {
        if (!Sessions.TryCreate("user", "colony", DateTimeOffset.UtcNow, out var session)) throw new Exception("Fixture login failed.");
        return (session, Tokens.Issue("user", "user", "colony", session, DateTimeOffset.UtcNow));
    }
    internal bool Valid((string Session, string Token) owner) => Sessions.IsValid("user", "colony", owner.Session, DateTimeOffset.UtcNow)
        && Tokens.IsValid(owner.Token, "user", "colony", DateTimeOffset.UtcNow);
    internal bool Retired((string Session, string Token) owner) => !Sessions.IsValid("user", "colony", owner.Session, DateTimeOffset.UtcNow)
        && !Tokens.IsValid(owner.Token, "user", "colony", DateTimeOffset.UtcNow);
}
