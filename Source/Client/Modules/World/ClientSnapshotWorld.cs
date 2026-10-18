using System;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.Modules.World;

internal interface IClientSnapshotWorldPresentation
{
    string RaidRestored(string snapshotId);
    void Status(string message);
    void Message(string message);
}

// Loading/publication own the completion receipt. This workflow connects the
// loaded world to existing domains without rebinding deferred work to a new login.
internal sealed class ClientSnapshotWorld
{
    private readonly ClientWorldSourceGame source;
    private readonly ClientWorldConfiguration configuration;
    private readonly IClientWorldIdentity identity;
    private readonly IClientWorldConfigurationRuntime runtime;
    private readonly ClientSessionConnection connection;
    private readonly IClientSessionFollowups followups;
    private readonly ClientRaidRecovery raids;
    private readonly IClientRaidRecoveryRuntime raidRuntime;
    private readonly IClientSnapshotWorldPresentation view;

    internal ClientSnapshotWorld(ClientWorldSourceGame source, ClientWorldConfiguration configuration,
        IClientWorldIdentity identity, IClientWorldConfigurationRuntime runtime, ClientSessionConnection connection,
        IClientSessionFollowups followups, ClientRaidRecovery raids, IClientRaidRecoveryRuntime raidRuntime,
        IClientSnapshotWorldPresentation view)
    {
        this.source = source; this.configuration = configuration; this.identity = identity; this.runtime = runtime;
        this.connection = connection; this.followups = followups; this.raids = raids; this.raidRuntime = raidRuntime; this.view = view;
    }
    internal void Continue(ClientSnapshotReloadCompleted completed)
    {
        if (!completed.Step(static () => { })) return;
        var world = runtime.Bind();
        string worldId = identity.WorldId;
        var catalog = completed.Request.WorldConfiguration;
        string expectedWorld = string.IsNullOrWhiteSpace(catalog?.WorldConfigurationId) ? worldId : catalog!.WorldConfigurationId.Trim();
        bool applyingConfiguration = false;
        bool OwnsWorld() => world.IsCurrent && (identity.WorldId == worldId || applyingConfiguration && identity.WorldId == expectedWorld);
        bool Current() => completed.Step(static () => { }) && OwnsWorld();
        bool Step(Action action)
        {
            bool applied = false;
            bool current = completed.Step(() => { if (OwnsWorld()) { action(); applied = true; } });
            return current && applied && OwnsWorld();
        }
        void Dispatch(ClientSessionFollowup effect)
            => Step(() => _ = followups.Dispatch(effect, ClientSessionFollowupSource.SnapshotReload, Step));

        if (!Step(source.Loaded)) return;
        if (completed.Request.Purpose == ClientSnapshotReloadPurpose.Raid)
        {
            string message = "";
            if (!Step(() => message = view.RaidRestored(completed.Snapshot.SnapshotId))
                || !Step(() => view.Status(message)) || !Step(() => view.Message(message))) return;
        }
        if (catalog is not null)
        {
            // Remember deliberately changes the world ID. Relax only that change
            // during configuration; keep the original loaded world and receipt.
            applyingConfiguration = true;
            if (!Step(() => configuration.ApplyCatalog(catalog, applyStoryteller: true, current: Current))) return;
            worldId = expectedWorld; applyingConfiguration = false;
        }
        Dispatch(ClientSessionFollowup.Events);
        Dispatch(ClientSessionFollowup.Players);
        Dispatch(ClientSessionFollowup.Chat);
        Step(() => completed.Game.Enqueue(() => Dispatch(ClientSessionFollowup.ColonySites), static () => { }));
        if (!connection.IsRunning) Dispatch(ClientSessionFollowup.Presence);
        Step(() => raids.Reconcile(raidRuntime, completed.Snapshot.ActiveRaidRecovery, authoritativeReload: true));
    }
}
