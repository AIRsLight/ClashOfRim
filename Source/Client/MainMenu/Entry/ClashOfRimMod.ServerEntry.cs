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
            try
            {
                ShowServerEntryProgressWindow(
                    ClashOfRimText.Key("ClashOfRim.ServerEntry.Title"),
                    ClashOfRimText.Key("ClashOfRim.ServerEntry.StageCompatibilityManifest"),
                    -1f,
                    canClose: false);
                using var httpClient = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(120)
                };
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ShowServerEntryProgressWindow(
                    ClashOfRimText.Key("ClashOfRim.ServerEntry.Title"),
                    ClashOfRimText.Key("ClashOfRim.ServerEntry.StageServerHello"),
                    -1f,
                    canClose: false);
                ClashOfRimClientNetworkResult<ModServerHelloResponseDto> hello =
                    await ClientSessionWork.Observe(client.ServerHelloAsync());
                using (ClientSessionWork.EnterPublication())
                {
                    if (!TryValidateServerHello(hello, out string helloFailure))
                    {
                        operation.QueueContinuation(EnqueueClashOfRimMainThreadAction, next =>
                        {
                            loginStatus = helloFailure;
                            Messages.Message(loginStatus, MessageTypeDefOf.RejectInput, historical: false);
                            ShowServerEntryFailureWindowNow(loginStatus);
                        });
                        return;
                    }
                }

                ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto> result =
                    await ClientSessionWork.Observe(client.PrepareWorldSessionAsync(createAccountIfMissing));
                if (result.Success
                    && result.Response?.Result?.Accepted == true
                    && result.Response.WorldConfigured
                    && result.Response.WorldConfiguration is null)
                {
                    using (ClientSessionWork.EnterPublication())
                    {
                        ShowServerEntryProgressWindow(ClashOfRimText.Key("ClashOfRim.ServerEntry.Title"), ClashOfRimText.Key("ClashOfRim.ServerEntry.StageWorldConfiguration"), -1f, canClose: false);
                    }
                    var configurationClient = new ClashOfRimModNetworkClient(
                        httpClient,
                        new ClashOfRimClientNetworkContext(
                            settings.ServerBaseUrl,
                            settings.UserId,
                            result.Response.AssignedColonyId ?? settings.ColonyId,
                            settings.CurrentSnapshotId,
                            settings.SteamAuthTicket,
                            settings.OfflinePassword,
                            settings.AuthToken));
                    ClashOfRimClientNetworkResult<ModGetWorldConfigurationResponseDto> worldConfiguration =
                        await ClientSessionWork.Observe(configurationClient.GetWorldConfigurationAsync(
                            includeGenerationBaseline: !result.Response.HasExistingColony,
                            includePlayerColonySites: true,
                            includeWorldExtensions: true));
                    using var branchPublication = ClientSessionWork.EnterPublication();
                    if (!worldConfiguration.Success || worldConfiguration.Response is null)
                    {
                        if (!result.Response.HasExistingColony)
                        {
                            result = ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>.Failed(
                                worldConfiguration.ErrorCode ?? "WorldConfigurationFailed",
                                worldConfiguration.Message ?? ClashOfRimText.Key("ClashOfRim.WorldConfiguredButMissingConfiguration"));
                        }
                        else
                        {
                            Log.Warning("[ClashOfRim] Existing-colony world configuration refresh failed during entry: "
                                + worldConfiguration.ErrorCode
                                + " "
                                + worldConfiguration.Message);
                        }
                    }
                    else if (worldConfiguration.Response.Result?.Accepted != true)
                    {
                        if (!result.Response.HasExistingColony)
                        {
                            result.Response.Result = worldConfiguration.Response.Result;
                        }
                        else
                        {
                            Log.Warning("[ClashOfRim] Existing-colony world configuration refresh rejected during entry: "
                                + worldConfiguration.Response.Result?.ErrorCode
                                + " "
                                + worldConfiguration.Response.Result?.Message);
                        }
                    }
                    else
                    {
                        result.Response.WorldConfiguration = worldConfiguration.Response.WorldConfiguration;
                    }
                }

                using var publication = ClientSessionWork.EnterPublication();
                operation.QueueContinuation(EnqueueClashOfRimMainThreadAction, next =>
                    FinishMainMenuServerEntryFlow(result, next));
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                operation.QueueContinuation(EnqueueClashOfRimMainThreadAction, next =>
                {
                    loginStatus = ClashOfRimText.Key(
                        "ClashOfRim.ServerEntry.StatusFlowException",
                        ex.GetType().Name.Named("TYPE"),
                        ex.Message.Named("MESSAGE"));
                    Messages.Message(loginStatus, MessageTypeDefOf.RejectInput, historical: false);
                    ShowServerEntryFailureWindowNow(loginStatus);
                });
            }
        });

        return true;
    }

    private static bool TryValidateServerHello(
        ClashOfRimClientNetworkResult<ModServerHelloResponseDto> result,
        out string failureReason)
    {
        if (!result.Success || result.Response is null)
        {
            failureReason = ClashOfRimText.Key(
                "ClashOfRim.ServerEntry.ServerHelloFailed",
                (result.ErrorCode ?? "ServerHelloFailed").Named("CODE"),
                (result.Message ?? ClashOfRimText.Key("ClashOfRim.UnknownReason")).Named("MESSAGE"));
            return false;
        }

        ModServerHelloResponseDto response = result.Response;
        ClashLog.Message(
            "[ClashOfRim] Server hello: product="
            + response.ProductName
            + " "
            + response.ProductVersion
            + ", protocol="
            + response.ProtocolVersion
            + " ("
            + response.ProtocolMajor
            + "."
            + response.ProtocolMinor
            + "), plugins="
            + (response.Plugins?.Count ?? 0)
            + ".");
        if (response.Result?.Accepted != true)
        {
            failureReason = response.Result?.Message
                ?? ClashOfRimText.Key("ClashOfRim.ServerEntry.ServerHelloRejected");
            return false;
        }

        bool protocolCompatible = response.ProtocolMajor == ClashOfRimVersion.ProtocolMajor
            && response.MinimumSupportedProtocolMajor == ClashOfRimVersion.ProtocolMajor
            && ClashOfRimVersion.ProtocolMinor >= response.MinimumSupportedProtocolMinor
            && ClashOfRimVersion.ProtocolMinor <= response.ProtocolMinor
            && string.Equals(response.ProtocolVersion, ClashOfRimVersion.ProtocolVersion, StringComparison.Ordinal);
        if (protocolCompatible)
        {
            failureReason = string.Empty;
            return true;
        }

        failureReason = ClashOfRimText.Key(
            "ClashOfRim.ServerEntry.ProtocolMismatch",
            $"{ClashOfRimVersion.ProductVersion} / {ClashOfRimVersion.ProtocolDisplayVersion}".Named("CLIENT"),
            $"{response.ProductVersion} / {response.ProtocolMajor}.{response.ProtocolMinor}".Named("SERVER"));
        return false;
    }

    private void ResetRuntimeSessionStateForMainMenuLogin()
    {
        _ = sessionLifecycle.End(sendLogout: false);
        blockAutomaticMapSessionForServerEntrySourceGame = false;
        serverEntrySourceGame = null;
        RemoteColonyWorldIconCache.Clear();
        lastWorldConfigurationVersion = 0;
        languageMismatchAcceptedForCurrentServerEntry = false;
        lastRegisteredPlayerColonySiteSignature = null;
        pendingInitialWorldConfigurationSubmit = false;
        pendingServerWorldConfiguration = null;
        pendingServerWorldSubstrate = null;
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
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                ClashOfRimText.Key(
                    "ClashOfRim.ServerEntry.AccountMissingPrompt",
                    account.Named("USER")),
                () => Find.WindowStack.Add(new ClashOfRimAccountRegistrationDialog(
                    this,
                    server,
                    account)),
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

        if (!languageMismatchAcceptedForCurrentServerEntry
            && CompatibilityLanguageMismatchPolicy.CanContinue(new ModLoginResponseDto
            {
                Result = response.Result,
                CompatibilityIssues = response.CompatibilityIssues ?? new List<ModCompatibilityIssueDto>()
            }))
        {
            CloseServerEntryProgressWindowNow();
            operation.QueueContinuation(
                (resume, cancel) => ShowCompatibilityMismatchWindow(response, resume, cancel),
                next =>
                {
                    languageMismatchAcceptedForCurrentServerEntry = true;
                    FinishMainMenuServerEntryFlow(result, next);
                });
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

        pendingInitialWorldConfigurationSubmit = response.RequiresInitialWorldConfiguration;
        pendingServerWorldConfiguration = response.WorldConfigured ? response.WorldConfiguration : null;
        UpdateOccupiedPlayerColonySites(response.WorldConfiguration);
        DebugLogFlow(
            "FinishMainMenuServerEntryFlow.Prepared",
            $"worldConfigured={response.WorldConfigured}, isAdmin={response.IsAdministrator}, requiresInitial={response.RequiresInitialWorldConfiguration}, hasExistingColony={response.HasExistingColony}, latestSnapshot={response.LatestSnapshotId}, hasConfiguration={response.WorldConfiguration is not null}, occupiedSites={occupiedPlayerColonySites.Count}, storyteller={response.WorldConfiguration?.StorytellerDefName}, difficulty={response.WorldConfiguration?.DifficultyDefName}, extensions={response.WorldConfiguration?.Extensions?.Count ?? 0}, {DescribeWorldConfiguration(response.WorldConfiguration)}");
        if (response.HasExistingColony)
        {
            pendingInitialWorldConfigurationSubmit = false;
            pendingServerWorldConfiguration = null;
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
            StartDownloadWorldSubstrateThenOpenScenario(response.WorldConfiguration, operation);
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
        ModWorldConfigurationDto configuration,
        ClientSynchronizationCoordinator.Lease continuation)
    {
        if (string.IsNullOrWhiteSpace(configuration.WorldConfigurationId))
        {
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
                    ClashOfRimClientNetworkContext.FromSettings(settings));
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
                        pendingServerWorldConfiguration = null;
                        pendingServerWorldSubstrate = null;
                        loginStatus = ClashOfRimText.Key("ClashOfRim.WorldSessionRequestFailed", "WorldSubstrate".Named("CODE"), message.Named("MESSAGE"));
                        ShowServerEntryFailureWindowNow(loginStatus);
                    });
                    return;
                }

                operation.QueueContinuation(EnqueueClashOfRimMainThreadAction, _ =>
                {
                    pendingServerWorldConfiguration = configuration;
                    pendingServerWorldSubstrate = substrate;
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
                    pendingServerWorldConfiguration = null;
                    pendingServerWorldSubstrate = null;
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
