using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Network.Modules.Compatibility;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal sealed class WorldSessionPreparation(AccountService accounts, EntryHandshakePolicy handshakes,
    SnapshotMutationCoordinator mutations, RaidTimeouts timeouts, RaidQueries raids, WorldConfigurationRegistry world,
    WorldColonySelection colonies, PlayerSessionActivity activity, ClientEntryLifecycle lifecycle)
{
    internal PrepareWorldSessionResponse Prepare(PrepareWorldSessionRequest request)
    {
        if (request.ProtocolVersion != ProtocolApiVersion.Current)
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, ServerLocalization.Text("Protocol.IncompatibleVersion")));
        if (string.IsNullOrWhiteSpace(request.UserId))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("WorldSession.MissingIdentity")));
        var credentials = accounts.PrepareCredentials(request.UserId, request.SteamAuthTicket, request.Password, DateTimeOffset.UtcNow, request.CreateAccountIfMissing);
        if (credentials.Revalidate() is { } invalid) return Reject(invalid);
        var input = CompatibilityHandshakeInput.Prepare(request.CompatibilityManifestJson, request.CompatibilityManifestId, request.CompatibilityManifestSummaryJson);
        var entry = handshakes.Prepare(credentials, input, EntryPurpose.WorldPreparation);
        using var mutation = mutations.Enter(entry.Authorize);
        if (mutation.Rejection is { } denied) return Reject(denied, entry.Compatibility);
        var now = DateTimeOffset.UtcNow;
        if (entry.CommitAccount(now) is { } uncommitted) return Reject(uncommitted);
        if (entry.PublishBaseline(now) is { } incompatible) return Reject(incompatible, entry.Compatibility);
        timeouts.Reconcile(now);
        // Recovery and persistence callbacks can change account policy, including after initial authentication.
        if (entry.Authorize() is { } revoked) return Reject(revoked, entry.Compatibility);
        string userId = entry.UserId;
        var session = world.Prepare(userId);
        var selected = colonies.Resolve(userId, request.ColonyId, session.WorldConfiguration, now);
        if (IsDefending(userId, selected)) return DefenseRejected(session, selected);
        activity.Record(userId, selected.ColonyId, null, now);
        lifecycle.Apply(ClientLifecycleEvent.InitialWorldSessionPrepared(userId, selected.ColonyId, selected.Snapshot?.Identity.SnapshotId, now));
        if (entry.Authorize() is { } finalDenied) return Reject(finalDenied, entry.Compatibility);
        session = world.GetSessionState(userId);
        if (IsDefending(userId, selected)) return DefenseRejected(session, selected);
        var compatibility = entry.Compatibility!;
        return new(ProtocolResponse.Ok(ServerLocalization.Text(session.WorldConfigured ? "WorldSession.ExistingConfiguration"
                : session.IsAdministrator ? "WorldSession.FirstAdminSetup" : "WorldSession.WaitingForAdmin")),
            session.IsAdministrator, session.WorldConfigured, session.RequiresInitialWorldConfiguration, session.AdministratorUserId,
            worldConfiguration: null, selected.HasExistingColony, selected.Snapshot?.Identity.SnapshotId,
            compatibility.ServerCompatibilityManifestJson, compatibility.CompatibilityIssues, compatibility.CanOverrideCompatibilityBaseline,
            selected.ColonyId, compatibility.RequiresFullCompatibilityManifest, compatibility.RequestedCompatibilityPackageIds);
    }

    private bool IsDefending(string userId, WorldColonySelectionResult colony)
        => colony.HasExistingColony && raids.BlockingDefender(userId, colony.ColonyId) is not null;

    private static PrepareWorldSessionResponse DefenseRejected(WorldSessionState session, WorldColonySelectionResult colony)
        => new(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("WorldSession.BlockedByDefenseRaid")),
            session.IsAdministrator, session.WorldConfigured, session.RequiresInitialWorldConfiguration, session.AdministratorUserId,
            worldConfiguration: null, colony.HasExistingColony, colony.Snapshot?.Identity.SnapshotId, assignedColonyId: colony.ColonyId);

    private static PrepareWorldSessionResponse Reject(ProtocolResponse rejection, CompatibilityHandshakeResult? compatibility = null)
        => new(rejection, false, false, false, null, null,
            serverCompatibilityManifestJson: compatibility?.ServerCompatibilityManifestJson,
            compatibilityIssues: compatibility?.CompatibilityIssues,
            requiresFullCompatibilityManifest: compatibility?.RequiresFullCompatibilityManifest ?? false,
            requestedCompatibilityPackageIds: compatibility?.RequestedCompatibilityPackageIds);
}
