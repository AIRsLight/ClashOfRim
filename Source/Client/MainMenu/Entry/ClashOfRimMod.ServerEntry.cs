using System;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Raids;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal string FormatCurrentMapRuntimeSummary()
    {
        Map? map = Find.CurrentMap;
        string mapText = map is null
            ? ClashOfRimText.Key("ClashOfRim.CurrentMapMissing")
            : ClashOfRimText.Key("ClashOfRim.CurrentMapSummary", map.uniqueID.Named("MAPID"));
        string configuration = settings.IsConfigured
            ? ClashOfRimText.Key(
                "ClashOfRim.RuntimeConfigurationSummary",
                settings.ServerBaseUrl.Named("SERVER"),
                settings.UserId.Named("USER"),
                settings.ColonyId.Named("COLONY"))
            : ClashOfRimText.Key("ClashOfRim.RuntimeConfigurationMissing");
        string snapshot = string.IsNullOrWhiteSpace(settings.CurrentSnapshotId)
            ? ClashOfRimText.Key("ClashOfRim.CurrentSnapshotMissing")
            : ClashOfRimText.Key("ClashOfRim.CurrentSnapshotSummary", settings.CurrentSnapshotId.Named("SNAPSHOT"));

        return $"{mapText} {configuration} {snapshot}";
    }

    internal bool StartMainMenuServerEntryFlow(string? serverBaseUrl, string? userId, string? password, bool createAccountIfMissing = false)
        => worldEntryStart.TryStart(serverBaseUrl, userId, password, createAccountIfMissing);

    // World-entry effects remain in M8; authentication, loading, publication and retries
    // are owned by ClientSnapshotReload before this completed-domain notification.
    private void OnServerSnapshotLoaded(ClientSnapshotReloadCompleted completed)
    {
        sourceGame.Loaded();
        if (completed.Request.Purpose == ClientSnapshotReloadPurpose.Raid)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusCleanupSnapshotSucceeded", completed.Snapshot.SnapshotId.Named("SNAPSHOT"));
            Messages.Message(worldMapStatus, MessageTypeDefOf.NeutralEvent, historical: false);
        }
        if (completed.Request.WorldConfiguration is { } configuration) ApplyRuntimeWorldConfigurationUpdate(configuration);
        string reason = ClashOfRimText.Key("ClashOfRim.ServerEntry.ReasonServerSnapshotLoaded");
        StartAutomaticEventRefresh(reason);
        _ = playerDirectory.RefreshAsync(manual: false);
        StartRefreshChatMessages(initialLoad: true);
        ClashOfRimGameComponent.EnqueueMainThreadAction(() => RequestPlayerColonySiteRegistration(reason));
        if (!sessionConnection.IsRunning) StartManualPresence();
        ReconcileRestoredRaidBattleSession(completed.Snapshot.ActiveRaidRecovery, authoritativeReload: true);
    }

    private void ReconcileRestoredRaidBattleSession(ModActiveRaidRecoveryDto? recovery, bool authoritativeReload = false)
        => raidRecovery.Reconcile(new ClientRaidRecoveryRuntime(), recovery, authoritativeReload);

    internal bool MarkServerEntryNewGameInitialized(object game) => sourceGame.Initialized(game);

    private void StartRestoreAfterRaid(string eventId)
        => _ = snapshotReload.StartAsync(new(sessionLifecycle.ReadContext().CurrentSnapshotId, ClientSnapshotReloadPurpose.Raid));

    private static void EnqueueClashOfRimMainThreadAction(Action action, Action? onDiscard = null)
        => ClientMainThreadDispatcher.Enqueue(action, onDiscard);

    private static void CloseServerEntryProgressWindowNow() => ServerEntryProgressWindow.CloseCurrent();
}
