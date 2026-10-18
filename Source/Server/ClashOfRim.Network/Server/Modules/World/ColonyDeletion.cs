using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal sealed class ColonyDeletion(IColonySnapshotIndexStore snapshots, IAuthoritativeEventLedger ledger,
    ColonyDeletionService journal, SnapshotCommitService commits, SnapshotMutationCoordinator mutations,
    WorldConfigurationRegistry world, ServerPluginRegistry plugins, CompatibilityBaselineRegistry baseline,
    RuntimeWorldObjectMarkerRegistry runtimeMarkers, DiplomacyRelationRegistry diplomacy, BankLoanRegistry loans,
    PlayerRegistry players, RaidPreparationRegistry raids, RaidProtectionActivationRegistry protection,
    MercenaryContractRegistry mercenaries, SessionConnections connections, EventNotificationHub notifications,
    WorldConfigurationChanges worldChanges)
{
    // Administrative callers have their own authorization. Player requests enter through ColonyAbandonment.
    internal ColonyDeletionResult Delete(string userId, string colonyId, DateTimeOffset nowUtc)
    {
        using var mutation = mutations.Enter();
        return DeleteUnderMutation(userId, colonyId, snapshots.GetLatest(userId, colonyId), nowUtc);
    }

    internal ColonyDeletionResult DeleteUnderMutation(string userId, string colonyId, LatestSnapshotRecord? latest, DateTimeOffset nowUtc)
    {
        if (commits.HasPending) throw new InvalidOperationException("Finish snapshot commits before beginning colony deletion.");
        var closure = ColonyEventClosure.Build(ledger, userId, colonyId, out var notifyUsers);
        var existing = ledger.FindByIdempotencyKey(ColonyTombstones.Key(userId, colonyId));
        if (existing is not null && latest is not null)
            throw new InvalidDataException("A deleted colony has a snapshot again; refusing to reuse its tombstone.");
        var tombstone = existing ?? ColonyTombstones.Create(userId, colonyId, latest?.Identity.SnapshotId, nowUtc);
        notifyUsers.Add(userId);
        var intent = new ColonyDeletionIntent(1, Guid.NewGuid().ToString("N"), userId, colonyId, latest?.Envelope,
            tombstone.CreatedAtUtc, closure with { Events = [tombstone] },
            closure.StatusChanges.Select(change => ledger.Find(change.EventId)!).ToArray(), notifyUsers.ToArray(),
            players.List().Select(player => player.UserId).Append(userId).Distinct(StringComparer.Ordinal).ToArray());
        intent.Preflight(snapshots, ledger);
        return journal.Commit(intent, Apply);
    }

    internal void Recover()
    {
        using var mutation = mutations.EnterUnrecovered();
        journal.Recover(intent => Apply(intent));
    }

    private ColonyDeletionResult Apply(ColonyDeletionIntent intent)
    {
        intent.Preflight(snapshots, ledger);
        string userId = intent.UserId, colonyId = intent.ColonyId;
        bool snapshotRemoved = snapshots.RemoveLatest(userId, colonyId);
        var removedSites = world.RemovePlayerColonySites(userId, colonyId, plugins.ActiveWorldConfigurationExtensions(baseline.Current));
        int removedRuntimeMarkers = runtimeMarkers.RemoveForOwner(userId, colonyId);
        diplomacy.RemoveForColony(userId, colonyId);
        loans.RemoveForColony(userId, colonyId);
        players.MarkDeleted(userId, colonyId, intent.Tombstone.AppliedSnapshotId, intent.DeletedAtUtc);
        raids.RemoveForColony(userId, colonyId);
        protection.RemoveForColony(userId, colonyId);
        mercenaries.CloseForColony(userId, colonyId);
        ledger.ApplyBatch(intent.Batch);
        connections.EndColonyUnderMutation(userId, colonyId);
        notifications.SignalUsers(intent.NotifyUsers);
        worldChanges.Signal(intent.WorldNotifyUsers.ToArray());
        // Ledger records remain for audit/replay; closure changes statuses rather than deleting events.
        return new(snapshotRemoved, removedSites, removedRuntimeMarkers);
    }
}

internal sealed record ColonyDeletionResult(bool SnapshotRemoved, RemovePlayerColonySitesResult RemovedSites, int RemovedRuntimeMarkers);
