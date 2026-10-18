using System;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.MainMenu;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.WorldObjects;
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

    internal bool StartMainMenuServerEntryFlow(
        string? serverBaseUrl,
        string? userId,
        string? password,
        bool createAccountIfMissing = false)
    {
        EnsureStatusDefaultsInitialized();
        if (!ClashOfRimServerUrlUtility.TryNormalizeHttpBaseUrl(serverBaseUrl, out string normalizedServerBaseUrl))
        {
            Messages.Message(ClashOfRimText.Key("ClashOfRim.ServerEntry.StatusAddressMissing"), MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }

        string normalizedUserId = (userId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedUserId))
        {
            Messages.Message(ClashOfRimText.Key("ClashOfRim.EnterUserId"), MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }

        if (manualSyncInProgress)
        {
            Messages.Message(loginStatus, MessageTypeDefOf.NeutralEvent, historical: false);
            return false;
        }

        ResetRuntimeSessionStateForMainMenuLogin();
        MarkServerEntrySourceGame();
        settings.ServerBaseUrl = normalizedServerBaseUrl;
        settings.UserId = normalizedUserId;
        settings.ColonyId = string.Empty;
        settings.OfflinePassword = password ?? string.Empty;
        settings.CurrentSnapshotId = string.Empty;
        settings.CurrentLineageToken = string.Empty;
        settings.Write();

        if (string.IsNullOrWhiteSpace(settings.ServerBaseUrl)
            || string.IsNullOrWhiteSpace(settings.UserId))
        {
            loginStatus = ClashOfRimText.Key("ClashOfRim.WorldSessionConfigMissing");
            Messages.Message(loginStatus, MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }

        if (!TryBeginManualSynchronization(out var manualOperation, out string admissionFailure))
        {
            loginStatus = admissionFailure;
            Messages.Message(loginStatus, MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }
        loginStatus = ClashOfRimText.Key(
            "ClashOfRim.RequestingWorldSession",
            settings.UserId.Named("USER"),
            settings.ColonyId.Named("COLONY"));
        ShowServerEntryProgressWindowNow(
            ClashOfRimText.Key("ClashOfRim.ServerEntry.Title"),
            loginStatus,
            -1f,
            canClose: false);
        manualOperation.RunContinuation(async operation =>
        {
            var prepared = await worldPreparation.PrepareAsync(createAccountIfMissing);
            if (prepared is null) return;
            if (!operation.IsCurrent) { worldPreparation.Discard(prepared); return; }
            operation.QueueContinuation(EnqueueClashOfRimMainThreadAction, next =>
                worldPreparation.Complete(prepared, outcome => { _ = worldEntry.Handle(outcome.Result, outcome.Failure, next); }),
                completed: () => worldPreparation.Discard(prepared));
        });

        return true;
    }

    private void ResetRuntimeSessionStateForMainMenuLogin()
    {
        _ = sessionLifecycle.End(sendLogout: false);
        blockAutomaticMapSessionForServerEntrySourceGame = false;
        serverEntrySourceGame = null;
        RemoteColonyWorldIconCache.Clear();
        lastWorldConfigurationVersion = 0;
        lastRegisteredPlayerColonySiteSignature = null;
        worldSetup.Reset();
        bankState.View = null;
        lastAdminStatus = null;
        giftsEnabled = true;
        pvpEnabled = true;
        settings.CurrentSnapshotId = string.Empty;
        settings.CurrentLineageToken = string.Empty;
        playerDirectory.ClearSelection();
        playerDirectory.Reset();
        settings.CurrentWorldConfigurationId = string.Empty;
        bankState.Message = ClashOfRimText.Key("ClashOfRim.Bank.NoStatus");
        tradeState.Reset();
        shopState.Reset();
        chatStatus = ClashOfRimText.Key("ClashOfRim.Chat.StatusIdle");
        adminStatus = ClashOfRimText.Key("ClashOfRim.Admin.StatusIdle");
        lock (eventStateLock)
        {
            lastWorldMapMarkers.Clear();
        }

        lock (chatStateLock)
        {
            lastChatMessages.Clear();
            lastChatSequence = 0;
            lastReadPrivateChatSequence = 0;
            chatMessagesSnapshotVersion++;
            unreadPrivateChatCount = 0;
        }

        lock (colonySiteStateLock)
        {
            occupiedPlayerColonySites.Clear();
        }
    }

    // World-entry effects remain in M8; authentication, loading, publication and retries
    // are owned by ClientSnapshotReload before this completed-domain notification.
    private void OnServerSnapshotLoaded(ClientSnapshotReloadCompleted completed)
    {
        ClearServerEntrySourceGameBlock("server-snapshot-loaded");
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

    private void MarkServerEntrySourceGame()
    {
        serverEntrySourceGame = Current.Game;
        blockAutomaticMapSessionForServerEntrySourceGame = serverEntrySourceGame is not null;
        ClashLog.Message("[ClashOfRim] Server entry flow marked source game for automatic map-session blocking: hasGame="
            + (serverEntrySourceGame is not null)
            + ", programState="
            + Current.ProgramState
            + ".");
    }

    internal void MarkServerEntryNewGameInitialized()
    {
        if (!blockAutomaticMapSessionForServerEntrySourceGame)
        {
            return;
        }

        ClearServerEntrySourceGameBlock("new-game-initialized");
    }

    private bool IsBlockedServerEntrySourceGame()
    {
        if (!blockAutomaticMapSessionForServerEntrySourceGame)
        {
            return false;
        }

        if (serverEntrySourceGame is null)
        {
            blockAutomaticMapSessionForServerEntrySourceGame = false;
            return false;
        }

        bool blocked = ReferenceEquals(Current.Game, serverEntrySourceGame);
        if (blocked)
        {
            ClashLog.Message("[ClashOfRim] Automatic map-session start blocked for stale server-entry source game.");
        }

        return blocked;
    }

    private void ClearServerEntrySourceGameBlock(string reason)
    {
        if (!blockAutomaticMapSessionForServerEntrySourceGame && serverEntrySourceGame is null)
        {
            return;
        }

        blockAutomaticMapSessionForServerEntrySourceGame = false;
        serverEntrySourceGame = null;
        ClashLog.Message("[ClashOfRim] Cleared server-entry source game block: " + reason + ".");
    }

    private void StartRestoreAfterRaid(string eventId)
        => _ = snapshotReload.StartAsync(new(sessionLifecycle.ReadContext().CurrentSnapshotId, ClientSnapshotReloadPurpose.Raid));

    private static void EnqueueClashOfRimMainThreadAction(Action action, Action? onDiscard = null)
        => ClientMainThreadDispatcher.Enqueue(action, onDiscard);

    private static void ShowServerEntryProgressWindow(string title, string message, float progress, bool canClose)
    {
        EnqueueClashOfRimMainThreadAction(() => ShowServerEntryProgressWindowNow(title, message, progress, canClose));
    }

    private static void ShowServerEntryProgressWindowNow(string title, string message, float progress, bool canClose)
        => ServerEntryProgressWindow.Show(title, message, progress, canClose);

    private static void CloseServerEntryProgressWindowNow() => ServerEntryProgressWindow.CloseCurrent();

    private static void ShowServerEntryFailureWindowNow(string message)
    {
        ShowServerEntryProgressWindowNow(
            ClashOfRimText.Key("ClashOfRim.ServerEntry.FailedTitle"),
            message,
            1f,
            canClose: true);
    }

}
