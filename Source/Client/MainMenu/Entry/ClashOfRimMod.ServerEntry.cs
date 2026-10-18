using System;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.CompatibilityClient;
using AIRsLight.ClashOfRim.MainMenu;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.WorldObjects;
using AIRsLight.ClashOfRim.Protocol;
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
                worldPreparation.Complete(prepared, outcome =>
                {
                    if (outcome.Failure is { } failure)
                    {
                        loginStatus = failure;
                        Messages.Message(loginStatus, MessageTypeDefOf.RejectInput, historical: false);
                        ShowServerEntryFailureWindowNow(loginStatus);
                    }
                    else FinishMainMenuServerEntryFlow(outcome.Result!, next);
                }), completed: () => worldPreparation.Discard(prepared));
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

    private void FinishMainMenuServerEntryFlow(
        ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto> result,
        ClientSynchronizationCoordinator.Lease operation)
    {
        DebugLogFlow("FinishMainMenuServerEntryFlow.Enter", $"success={result.Success}, response={result.Response is not null}");
        if (!result.Success || result.Response is null)
        {
            loginStatus = ClashOfRimText.Key(
                "ClashOfRim.WorldSessionRequestFailed",
                result.ErrorCode.Named("CODE"),
                result.Message.Named("MESSAGE"));
            Messages.Message(loginStatus, MessageTypeDefOf.RejectInput, historical: false);
            ShowServerEntryFailureWindowNow(loginStatus);
            return;
        }

        ModPrepareWorldSessionResponseDto response = result.Response;
        if (response.Result?.Accepted != true
            && response.Result?.ErrorCode == (int)ProtocolErrorCode.AccountNotFound)
        {
            CloseServerEntryProgressWindowNow();
            string server = settings.ServerBaseUrl;
            string account = settings.UserId;
            var registration = worldSetup.CaptureDecision();
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                ClashOfRimText.Key(
                    "ClashOfRim.ServerEntry.AccountMissingPrompt",
                    account.Named("USER")),
                () => worldSetup.Accept(registration, () => Find.WindowStack.Add(new ClashOfRimAccountRegistrationDialog(
                    this,
                    server,
                    account))),
                destructive: false,
                title: ClashOfRimText.Key("ClashOfRim.ServerEntry.AccountMissingTitle")));
            return;
        }

        CaptureServerCompatibilityManifest(response.ServerCompatibilityManifestJson);
        if (!string.IsNullOrWhiteSpace(response.AssignedColonyId)
            && !string.Equals(settings.ColonyId, response.AssignedColonyId, StringComparison.Ordinal))
        {
            DebugLogFlow(
                "FinishMainMenuServerEntryFlow.AssignedColonyId",
                $"requested={settings.ColonyId}, assigned={response.AssignedColonyId}");
            settings.ColonyId = response.AssignedColonyId!;
            settings.CurrentSnapshotId = string.Empty;
            settings.Write();
        }

        if (!string.IsNullOrWhiteSpace(response.LatestSnapshotId)
            && !string.Equals(settings.CurrentSnapshotId, response.LatestSnapshotId, StringComparison.Ordinal))
        {
            settings.CurrentSnapshotId = response.LatestSnapshotId!;
            settings.Write();
        }

        ApplyAdministratorFlag(response.IsAdministrator);

        if (!worldSetup.LanguageAccepted
            && CompatibilityLanguageMismatchPolicy.CanContinue(new ModLoginResponseDto
            {
                Result = response.Result,
                CompatibilityIssues = response.CompatibilityIssues ?? new List<ModCompatibilityIssueDto>()
            }))
        {
            CloseServerEntryProgressWindowNow();
            var decision = worldSetup.CaptureDecision();
            operation.QueueContinuation(
                (resume, cancel) => ShowCompatibilityMismatchWindow(response, resume, cancel),
                next => worldSetup.Accept(decision, () =>
                {
                    worldSetup.LanguageAccepted = true;
                    FinishMainMenuServerEntryFlow(result, next);
                }));
            return;
        }

        if (response.Result?.Accepted != true)
        {
            loginStatus = ClashOfRimText.Key(
                "ClashOfRim.WorldSessionRejected",
                (response.Result?.Message ?? ClashOfRimText.Key("ClashOfRim.UnknownReason")).Named("REASON"));
            Messages.Message(loginStatus, MessageTypeDefOf.RejectInput, historical: false);
            ShowServerEntryFailureWindowNow(loginStatus);
            ShowCompatibilityMismatchWindow(response);
            return;
        }

        ShowCompatibilityMismatchWindow(response);

        if (!response.WorldConfigured && !response.IsAdministrator)
        {
            loginStatus = response.Result?.Message ?? ClashOfRimText.Key("ClashOfRim.WaitingFirstAdminWorldConfiguration");
            Messages.Message(loginStatus, MessageTypeDefOf.RejectInput, historical: false);
            ShowServerEntryFailureWindowNow(loginStatus);
            return;
        }

        if (response.WorldConfigured && !response.HasExistingColony && response.WorldConfiguration is null)
        {
            loginStatus = ClashOfRimText.Key("ClashOfRim.WorldConfiguredButMissingConfiguration");
            Messages.Message(loginStatus, MessageTypeDefOf.RejectInput, historical: false);
            ShowServerEntryFailureWindowNow(loginStatus);
            return;
        }

        worldSetup.Prepare(response);
        UpdateOccupiedPlayerColonySites(response.WorldConfiguration);
        DebugLogFlow(
            "FinishMainMenuServerEntryFlow.Prepared",
            $"worldConfigured={response.WorldConfigured}, isAdmin={response.IsAdministrator}, requiresInitial={response.RequiresInitialWorldConfiguration}, hasExistingColony={response.HasExistingColony}, latestSnapshot={response.LatestSnapshotId}, hasConfiguration={response.WorldConfiguration is not null}, occupiedSites={occupiedPlayerColonySites.Count}, storyteller={response.WorldConfiguration?.StorytellerDefName}, difficulty={response.WorldConfiguration?.DifficultyDefName}, extensions={response.WorldConfiguration?.Extensions?.Count ?? 0}, {DescribeWorldConfiguration(response.WorldConfiguration)}");
        if (response.HasExistingColony)
        {
            ClearServerEntrySourceGameBlock("existing-colony-restore");
            string? latestSnapshotId = response.LatestSnapshotId;
            if (!string.IsNullOrWhiteSpace(latestSnapshotId))
            {
                settings.CurrentSnapshotId = latestSnapshotId!;
                settings.Write();
            }

            loginStatus = ClashOfRimText.Key(
                "ClashOfRim.ExistingColonyLoadFlow",
                settings.UserId.Named("USER"),
                settings.ColonyId.Named("COLONY"),
                (latestSnapshotId ?? ClashOfRimText.Key("ClashOfRim.None")).Named("SNAPSHOT"));
            ShowServerEntryProgressWindowNow(
                ClashOfRimText.Key("ClashOfRim.ServerEntry.Title"),
                loginStatus,
                0.05f,
                canClose: false);
            StartRestoreExistingColonySnapshot(
                latestSnapshotId,
                worldConfiguration: response.WorldConfiguration,
                continuation: operation);
            return;
        }

        if (response.WorldConfigured && response.WorldConfiguration is not null)
        {
            loginStatus = ClashOfRimText.Key("ClashOfRim.JoinExistingWorldFlow", settings.UserId.Named("USER"), settings.ColonyId.Named("COLONY"));
            ShowServerEntryProgressWindowNow(
                ClashOfRimText.Key("ClashOfRim.ServerEntry.Title"),
                loginStatus,
                0.35f,
                canClose: false);
            StartDownloadWorldSubstrateThenOpenScenario(operation);
            return;
        }

        loginStatus = response.RequiresInitialWorldConfiguration
            ? ClashOfRimText.Key(
                "ClashOfRim.FirstAdminWorldConfigurationFlow",
                settings.UserId.Named("USER"),
                settings.ColonyId.Named("COLONY"))
            : ClashOfRimText.Key(
                "ClashOfRim.JoinExistingWorldFlow",
                settings.UserId.Named("USER"),
                settings.ColonyId.Named("COLONY"));
        Messages.Message(loginStatus, MessageTypeDefOf.NeutralEvent, historical: false);
        CloseServerEntryProgressWindowNow();
        Page_SelectScenario selectScenario = new();
        DebugLogFlow("FinishMainMenuServerEntryFlow.AddPageSelectScenario.Before", DescribeWindowStack());
        Find.WindowStack.Add(selectScenario);
        DebugLogFlow("FinishMainMenuServerEntryFlow.AddPageSelectScenario.After", DescribeWindowStack());
    }

    private void StartDownloadWorldSubstrateThenOpenScenario(
        ClientSynchronizationCoordinator.Lease continuation)
    {
        var pending = worldSetup.CaptureGeneration();
        if (pending is null) return;
        var configuration = pending.Configuration;
        if (string.IsNullOrWhiteSpace(configuration.WorldConfigurationId))
        {
            worldSetup.RejectGeneration(pending);
            loginStatus = ClashOfRimText.Key("ClashOfRim.WorldConfiguredButMissingConfiguration");
            ShowServerEntryFailureWindowNow(loginStatus);
            return;
        }

        continuation.RunContinuation(async operation =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    pending.Context);
                ClashOfRimClientNetworkResult<byte[]> result = await ClientSessionWork.Observe(client
                    .DownloadWorldSubstrateAsync(configuration.WorldConfigurationId)
                    .ConfigureAwait(false));
                using var publication = ClientSessionWork.EnterPublication();
                string? failure = null;
                WorldSubstratePackage? substrate = null;
                bool decoded = result.Success
                    && result.Response is not null
                    && WorldSubstratePackageCodec.TryDecode(result.Response, out substrate, out failure)
                    && substrate is not null;
                if (!decoded)
                {
                    string message = result.Message ?? result.ErrorCode ?? failure ?? "Unknown";
                    operation.QueueContinuation(EnqueueClashOfRimMainThreadAction, _ =>
                    {
                        if (!worldSetup.RejectGeneration(pending)) return;
                        loginStatus = ClashOfRimText.Key("ClashOfRim.WorldSessionRequestFailed", "WorldSubstrate".Named("CODE"), message.Named("MESSAGE"));
                        ShowServerEntryFailureWindowNow(loginStatus);
                    });
                    return;
                }

                operation.QueueContinuation(EnqueueClashOfRimMainThreadAction, _ =>
                {
                    if (!worldSetup.AttachSubstrate(pending, substrate!)) return;
                    CloseServerEntryProgressWindowNow();
                    Find.WindowStack.Add(new Page_SelectScenario());
                });
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                operation.QueueContinuation(EnqueueClashOfRimMainThreadAction, _ =>
                {
                    if (!worldSetup.RejectGeneration(pending)) return;
                    loginStatus = ClashOfRimText.Key("ClashOfRim.WorldSessionRequestFailed", ex.GetType().Name.Named("CODE"), ex.Message.Named("MESSAGE"));
                    ShowServerEntryFailureWindowNow(loginStatus);
                });
            }
        });
    }

    private void StartRestoreExistingColonySnapshot(string? expectedSnapshotId,
        ModWorldConfigurationDto? worldConfiguration = null, ClientSynchronizationCoordinator.Lease? continuation = null)
        => _ = snapshotReload.StartAsync(new(expectedSnapshotId, ClientSnapshotReloadPurpose.Entry, worldConfiguration), continuation);

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
