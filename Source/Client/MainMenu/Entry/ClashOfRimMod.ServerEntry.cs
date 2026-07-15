using System;
using System.Collections.Generic;
using System.Globalization;
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
        settings.AuthToken = string.Empty;
        settings.Write();

        if (string.IsNullOrWhiteSpace(settings.ServerBaseUrl)
            || string.IsNullOrWhiteSpace(settings.UserId))
        {
            loginStatus = ClashOfRimText.Key("ClashOfRim.WorldSessionConfigMissing");
            Messages.Message(loginStatus, MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }

        manualSyncInProgress = true;
        loginStatus = ClashOfRimText.Key(
            "ClashOfRim.RequestingWorldSession",
            settings.UserId.Named("USER"),
            settings.ColonyId.Named("COLONY"));
        ShowServerEntryProgressWindowNow(
            ClashOfRimText.Key("ClashOfRim.ServerEntry.Title"),
            loginStatus,
            -1f,
            canClose: false);
        Task.Run(async () =>
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
                    await client.ServerHelloAsync();
                if (!TryValidateServerHello(hello, out string helloFailure))
                {
                    ClashOfRimMainMenuPatches.EnqueueMainThreadAction(() =>
                    {
                        loginStatus = helloFailure;
                        Messages.Message(loginStatus, MessageTypeDefOf.RejectInput, historical: false);
                        ShowServerEntryFailureWindowNow(loginStatus);
                        manualSyncInProgress = false;
                    });
                    return;
                }

                ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto> result =
                    await client.PrepareWorldSessionAsync(createAccountIfMissing);
                if (result.Success
                    && result.Response?.Result?.Accepted == true
                    && result.Response.WorldConfigured
                    && result.Response.WorldConfiguration is null)
                {
                    ShowServerEntryProgressWindow(
                        ClashOfRimText.Key("ClashOfRim.ServerEntry.Title"),
                        ClashOfRimText.Key("ClashOfRim.ServerEntry.StageWorldConfiguration"),
                        -1f,
                        canClose: false);
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
                        await configurationClient.GetWorldConfigurationAsync(
                            includeGenerationBaseline: !result.Response.HasExistingColony,
                            includePlayerColonySites: true,
                            includeWorldExtensions: true);
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

                ClashOfRimMainMenuPatches.EnqueueMainThreadAction(() =>
                    FinishMainMenuServerEntryFlow(result));
            }
            catch (Exception ex)
            {
                ClashOfRimMainMenuPatches.EnqueueMainThreadAction(() =>
                {
                    loginStatus = ClashOfRimText.Key(
                        "ClashOfRim.ServerEntry.StatusFlowException",
                        ex.GetType().Name.Named("TYPE"),
                        ex.Message.Named("MESSAGE"));
                    Messages.Message(loginStatus, MessageTypeDefOf.RejectInput, historical: false);
                    ShowServerEntryFailureWindowNow(loginStatus);
                    manualSyncInProgress = false;
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
        presenceCancellation?.Cancel();
        presenceCancellation = null;
        presenceInProgress = false;
        lastSessionId = null;
        blockAutomaticMapSessionForServerEntrySourceGame = false;
        serverEntrySourceGame = null;
        CaptureServerCompatibilityManifest(null);
        RemoteColonyWorldIconCache.Clear();
        ApplyAdministratorFlag(false);
        lastNotificationVersion = 0;
        lastWorldConfigurationVersion = 0;
        sessionExpiredHandling = false;
        languageMismatchAcceptedForCurrentServerEntry = false;
        ClearLocalAtomicMutation();
        ClearPendingUnconfirmedSnapshotFailure();
        lastRegisteredPlayerColonySiteSignature = null;
        pendingInitialWorldConfigurationSubmit = false;
        pendingServerWorldConfiguration = null;
        pendingServerWorldSubstrate = null;
        lastBankStatus = null;
        lastAdminStatus = null;
        giftsEnabled = true;
        pvpEnabled = true;
        settings.CurrentSnapshotId = string.Empty;
        settings.CurrentLineageToken = string.Empty;
        settings.AuthToken = string.Empty;
        settings.TargetUserId = string.Empty;
        settings.TargetColonyId = string.Empty;
        settings.TargetSnapshotId = string.Empty;
        settings.CurrentWorldConfigurationId = string.Empty;
        bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.NoStatus");
        eventQueueStatus = ClashOfRimText.Key("ClashOfRim.Status.EventQueueNotPulled");
        eventDetailsStatus = ClashOfRimText.Key("ClashOfRim.Status.EventDetailsNotPulled");
        playerListStatus = ClashOfRimText.Key("ClashOfRim.Status.PlayerListNotPulled");
        tradeStatus = ClashOfRimText.Key("ClashOfRim.Status.TradeOrderNotCreated");
        chatStatus = ClashOfRimText.Key("ClashOfRim.Chat.StatusIdle");
        adminStatus = ClashOfRimText.Key("ClashOfRim.Admin.StatusIdle");
        pendingGiftConfirmationEventIds.Clear();
        postedEventLetterIds.Clear();
        appliedServerNotificationSideEffectIds.Clear();
        appliedDiplomacyEventSideEffectIds.Clear();
        lock (eventStateLock)
        {
            lastEventQueueEventIds.Clear();
            lastEventDetails.Clear();
            lastPlayers.Clear();
            playersSnapshotVersion++;
            lastTradeOrders.Clear();
            tradeOrdersSnapshotVersion++;
            lastServerShopListings.Clear();
            serverShopListingsSnapshotVersion++;
            tradeOrdersHasMore = false;
            tradeOrdersTotalCount = 0;
            tradeOrdersScope = "Open";
            lastEventReferences.Clear();
            lastEventReferenceGroups.Clear();
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

    private void FinishMainMenuServerEntryFlow(ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto> result)
    {
        bool continuingAsyncFlow = false;
        try
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
                continuingAsyncFlow = true;
                CloseServerEntryProgressWindowNow();
                ShowCompatibilityMismatchWindow(
                    response,
                    continueAnyway: () =>
                    {
                        languageMismatchAcceptedForCurrentServerEntry = true;
                        FinishMainMenuServerEntryFlow(result);
                    },
                    cancelContinuation: () => manualSyncInProgress = false);
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
                continuingAsyncFlow = StartRestoreExistingColonySnapshot(latestSnapshotId);
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
                continuingAsyncFlow = StartDownloadWorldSubstrateThenOpenScenario(response.WorldConfiguration);
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
        finally
        {
            if (!continuingAsyncFlow)
            {
                manualSyncInProgress = false;
            }
        }
    }

    private bool StartDownloadWorldSubstrateThenOpenScenario(ModWorldConfigurationDto configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.WorldConfigurationId))
        {
            loginStatus = ClashOfRimText.Key("ClashOfRim.WorldConfiguredButMissingConfiguration");
            ShowServerEntryFailureWindowNow(loginStatus);
            return false;
        }

        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<byte[]> result = await client
                    .DownloadWorldSubstrateAsync(configuration.WorldConfigurationId)
                    .ConfigureAwait(false);
                string? failure = null;
                WorldSubstratePackage? substrate = null;
                bool decoded = result.Success
                    && result.Response is not null
                    && WorldSubstratePackageCodec.TryDecode(result.Response, out substrate, out failure)
                    && substrate is not null;
                if (!decoded)
                {
                    string message = result.Message ?? result.ErrorCode ?? failure ?? "Unknown";
                    EnqueueClashOfRimMainThreadAction(() =>
                    {
                        pendingServerWorldConfiguration = null;
                        pendingServerWorldSubstrate = null;
                        loginStatus = ClashOfRimText.Key("ClashOfRim.WorldSessionRequestFailed", "WorldSubstrate".Named("CODE"), message.Named("MESSAGE"));
                        ShowServerEntryFailureWindowNow(loginStatus);
                        manualSyncInProgress = false;
                    });
                    return;
                }

                EnqueueClashOfRimMainThreadAction(() =>
                {
                    pendingServerWorldConfiguration = configuration;
                    pendingServerWorldSubstrate = substrate;
                    manualSyncInProgress = false;
                    CloseServerEntryProgressWindowNow();
                    Find.WindowStack.Add(new Page_SelectScenario());
                });
            }
            catch (Exception ex)
            {
                EnqueueClashOfRimMainThreadAction(() =>
                {
                    pendingServerWorldConfiguration = null;
                    pendingServerWorldSubstrate = null;
                    loginStatus = ClashOfRimText.Key("ClashOfRim.WorldSessionRequestFailed", ex.GetType().Name.Named("CODE"), ex.Message.Named("MESSAGE"));
                    ShowServerEntryFailureWindowNow(loginStatus);
                    manualSyncInProgress = false;
                });
            }
        });
        return true;
    }

    private bool StartRestoreExistingColonySnapshot(
        string? expectedSnapshotId,
        Action<string>? restoreFailureCallback = null,
        bool allowDifferentSnapshotId = false)
    {
        if (string.IsNullOrWhiteSpace(expectedSnapshotId))
        {
            loginStatus = ClashOfRimText.Key("ClashOfRim.ExistingColonyMissingSnapshot");
            ReportRestoreExistingColonySnapshotFailure(loginStatus, restoreFailureCallback);
            return false;
        }

        manualSyncInProgress = true;
        loginStatus = ClashOfRimText.Key(
            "ClashOfRim.DownloadingExistingSnapshot",
            expectedSnapshotId.Named("SNAPSHOT"));
        ShowServerEntryProgressWindowNow(
            ClashOfRimText.Key("ClashOfRim.ServerEntry.Title"),
            loginStatus,
            0.1f,
            canClose: false);
        string expected = expectedSnapshotId!;
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(60)
                };
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                if (string.IsNullOrWhiteSpace(lastSessionId))
                {
                    ShowServerEntryProgressWindow(
                        ClashOfRimText.Key("ClashOfRim.ServerEntry.Title"),
                        ClashOfRimText.Key("ClashOfRim.ServerEntry.StageLogin"),
                        0.2f,
                        canClose: false);
                    ClashOfRimClientNetworkResult<ModLoginResponseDto> login =
                        await client.LoginAsync("restore-existing-snapshot");
                    if (!login.Success || login.Response is null || login.Response.Result?.Accepted != true)
                    {
                        string message = login.Response?.Result?.Message
                            ?? login.Message
                            ?? ClashOfRimText.Key("ClashOfRim.ServerEntry.StatusLoginFallback");
                        ShowCompatibilityMismatchWindow(login.Response);
                        EnqueueClashOfRimMainThreadAction(() =>
                        {
                            loginStatus = ClashOfRimText.Key(
                                "ClashOfRim.DownloadExistingSnapshotRejected",
                                message.Named("REASON"));
                            ReportRestoreExistingColonySnapshotFailure(loginStatus, restoreFailureCallback);
                            manualSyncInProgress = false;
                        });
                        return;
                    }

                    settings.AuthToken = login.Response.AuthToken ?? string.Empty;
                    settings.Write();
                    CaptureServerCompatibilityManifest(login.Response.ServerCompatibilityManifestJson);
                    lastSessionId = login.Response.SessionId;
                    sessionExpiredHandling = false;
                    ApplyAdministratorFlag(login.Response.IsAdministrator);
                    client = new ClashOfRimModNetworkClient(
                        httpClient,
                        ClashOfRimClientNetworkContext.FromSettings(settings));
                }

                ShowServerEntryProgressWindow(
                    ClashOfRimText.Key("ClashOfRim.ServerEntry.Title"),
                    ClashOfRimText.Key("ClashOfRim.ServerEntry.StageMetadata"),
                    0.35f,
                    canClose: false);
                ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto> result =
                    await client.DownloadLatestSnapshotAsync();
                ClashOfRimClientNetworkResult<byte[]>? payloadResult = null;
                if (result.Success
                    && result.Response?.Result?.Accepted == true
                    && !string.IsNullOrWhiteSpace(result.Response.SnapshotId ?? result.Response.Package?.SnapshotId))
                {
                    string snapshotIdForProgress = result.Response.SnapshotId ?? result.Response.Package!.SnapshotId;
                    ShowServerEntryProgressWindow(
                        ClashOfRimText.Key("ClashOfRim.ServerEntry.Title"),
                        ClashOfRimText.Key(
                            "ClashOfRim.ServerEntry.StagePayload",
                            snapshotIdForProgress.Named("SNAPSHOT")),
                        0.58f,
                        canClose: false);
                    payloadResult = await client.DownloadLatestSnapshotPayloadAsync(
                        snapshotIdForProgress);
                }

                ShowServerEntryProgressWindow(
                    ClashOfRimText.Key("ClashOfRim.ServerEntry.Title"),
                    ClashOfRimText.Key("ClashOfRim.ServerEntry.StageValidate"),
                    0.82f,
                    canClose: false);
                EnqueueClashOfRimMainThreadAction(() =>
                    FinishRestoreExistingColonySnapshot(expected, result, payloadResult, restoreFailureCallback, allowDifferentSnapshotId));
            }
            catch (Exception ex)
            {
                EnqueueClashOfRimMainThreadAction(() =>
                {
                    loginStatus = ClashOfRimText.Key(
                        "ClashOfRim.DownloadExistingSnapshotException",
                        ex.GetType().Name.Named("TYPE"),
                        ex.Message.Named("MESSAGE"));
                    ReportRestoreExistingColonySnapshotFailure(loginStatus, restoreFailureCallback);
                    manualSyncInProgress = false;
                });
            }
        });

        return true;
    }

    private void FinishRestoreExistingColonySnapshot(
        string expectedSnapshotId,
        ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto> result,
        ClashOfRimClientNetworkResult<byte[]>? payloadResult,
        Action<string>? restoreFailureCallback,
        bool allowDifferentSnapshotId)
    {
        try
        {
            if (!result.Success || result.Response is null)
            {
                loginStatus = ClashOfRimText.Key(
                    "ClashOfRim.DownloadExistingSnapshotFailed",
                    result.ErrorCode.Named("CODE"),
                    result.Message.Named("MESSAGE"));
                ReportRestoreExistingColonySnapshotFailure(loginStatus, restoreFailureCallback);
                return;
            }

            ModDownloadLatestSnapshotResponseDto response = result.Response;
            if (response.Result?.Accepted != true)
            {
                loginStatus = ClashOfRimText.Key(
                    "ClashOfRim.DownloadExistingSnapshotRejected",
                    (response.Result?.Message ?? ClashOfRimText.Key("ClashOfRim.UnknownReason")).Named("REASON"));
                ReportRestoreExistingColonySnapshotFailure(loginStatus, restoreFailureCallback);
                return;
            }

            ModSnapshotPackageMetadataDto? package = response.Package;
            string? snapshotId = response.SnapshotId ?? package?.SnapshotId;
            if (package is null || string.IsNullOrWhiteSpace(snapshotId))
            {
                loginStatus = ClashOfRimText.Key("ClashOfRim.DownloadExistingSnapshotMissingPackage");
                ReportRestoreExistingColonySnapshotFailure(loginStatus, restoreFailureCallback);
                return;
            }

            if (!string.Equals(package.OwnerId, settings.UserId, StringComparison.Ordinal)
                || !string.Equals(package.ColonyId, settings.ColonyId, StringComparison.Ordinal))
            {
                loginStatus = ClashOfRimText.Key(
                    "ClashOfRim.DownloadExistingSnapshotMismatch",
                    $"{settings.UserId}/{settings.ColonyId}".Named("EXPECTED"),
                    $"{package.OwnerId}/{package.ColonyId}".Named("ACTUAL"));
                ReportRestoreExistingColonySnapshotFailure(loginStatus, restoreFailureCallback);
                return;
            }

            if (!allowDifferentSnapshotId && !string.Equals(snapshotId, expectedSnapshotId, StringComparison.Ordinal))
            {
                ClashLog.Message(
                    "[ClashOfRim] Server advanced latest snapshot during restore: expected="
                    + expectedSnapshotId
                    + ", actual="
                    + snapshotId
                    + ", user="
                    + settings.UserId
                    + ", colony="
                    + settings.ColonyId
                    + ". Accepting server-authoritative latest snapshot.");
            }

            if (payloadResult is null || !payloadResult.Success || payloadResult.Response is null)
            {
                loginStatus = ClashOfRimText.Key(
                    "ClashOfRim.DownloadExistingSnapshotPayloadFailed",
                    (payloadResult?.ErrorCode ?? "MissingPayload").Named("CODE"),
                    (payloadResult?.Message ?? ClashOfRimText.Key("ClashOfRim.UnknownReason")).Named("MESSAGE"));
                ReportRestoreExistingColonySnapshotFailure(loginStatus, restoreFailureCallback);
                return;
            }

            if (!ServerSnapshotRestoreService.TryBuildServerSessionSaveBytes(
                    package,
                    payloadResult.Response,
                    settings.UserId,
                    settings.ColonyId,
                    out string saveName,
                    out byte[] saveBytes,
                    out string failureReason))
            {
                loginStatus = failureReason;
                ReportRestoreExistingColonySnapshotFailure(loginStatus, restoreFailureCallback);
                return;
            }

            settings.CurrentSnapshotId = snapshotId!;
            settings.CurrentLineageToken = package.NextLineageToken ?? string.Empty;
            settings.Write();
            ClashLog.Message(
                "[ClashOfRim] Restored server snapshot to memory: snapshot="
                + snapshotId
                + ", saveName="
                + saveName);
            ModActiveRaidRecoveryDto? activeRaidRecovery = response.ActiveRaidRecovery;
            loginStatus = ClashOfRimText.Key(
                "ClashOfRim.ExistingSnapshotRestored",
                snapshotId.Named("SNAPSHOT"));
            ShowServerEntryProgressWindowNow(
                ClashOfRimText.Key("ClashOfRim.ServerEntry.Title"),
                ClashOfRimText.Key("ClashOfRim.ServerEntry.StageLoad"),
                0.95f,
                canClose: false);
            CloseUnconfirmedSnapshotFailureWindow();
            CloseServerEntryProgressWindowNow();
            ServerSessionGameDataLoader.LoadGame(saveBytes, saveName, () =>
            {
                ClearServerEntrySourceGameBlock("server-snapshot-loaded");
                if (!string.IsNullOrWhiteSpace(settings.CurrentLineageToken))
                {
                    ClashOfRimGameComponent.SetSnapshotLineage(
                        snapshotId,
                        settings.CurrentLineageToken);
                }

                StartAutomaticEventRefresh(ClashOfRimText.Key("ClashOfRim.ServerEntry.ReasonServerSnapshotLoaded"));
                StartRefreshPlayers(ClashOfRimText.Key("ClashOfRim.ServerEntry.ReasonServerSnapshotLoaded"), requireManualGate: false);
                StartRefreshChatMessages(initialLoad: true);
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    RequestPlayerColonySiteRegistration(ClashOfRimText.Key("ClashOfRim.ServerEntry.ReasonServerSnapshotLoaded")));
                if (!presenceInProgress)
                {
                    StartManualPresence();
                }

                ReconcileRestoredRaidBattleSession(activeRaidRecovery);
            });
        }
        finally
        {
            manualSyncInProgress = false;
        }
    }

    private void ReconcileRestoredRaidBattleSession(ModActiveRaidRecoveryDto? recovery)
    {
        if (Current.Game?.World?.worldObjects is null || Current.Game.Maps is null)
        {
            Log.Warning("[ClashOfRim][Raid] Server snapshot load callback fired before world/maps were ready; delaying raid recovery reconciliation.");
            ClashOfRimGameComponent.EnqueueMainThreadAction(() => ReconcileRestoredRaidBattleSession(recovery));
            return;
        }

        ActiveRaidBattleSession? session = ClashOfRimGameComponent.ActiveRaidBattleSession;
        if (recovery is null)
        {
            if (session is null)
            {
                return;
            }

            ClashLog.Message("[ClashOfRim][Raid] Restored snapshot still had raid battle session "
                + session.EventId
                + ", but the server has no active source raid for this colony. Refreshing events and reloading the server snapshot.");
            Messages.Message(
                ClashOfRimText.Key("ClashOfRim.Raid.RestoreServerAlreadySettled"),
                MessageTypeDefOf.NeutralEvent,
                historical: false);
            StartConfirmSettledRaidCleanupSnapshot(session.EventId);
            return;
        }

        if (session is null
            || !string.Equals(session.EventId, recovery.EventId, StringComparison.Ordinal)
            || RemoteSessionMapUtility.FindActiveSessionMap(ActiveRemoteMapSession.FromRaidBattle(session)) is null)
        {
            string message = ClashOfRimText.Key(
                "ClashOfRim.Raid.RestoreMissingLocalBattle",
                recovery.EventId.Named("EVENTID"));
            Log.Warning("[ClashOfRim][Raid] Server reports active raid "
                + recovery.EventId
                + " but the restored snapshot has no matching local battle map/session. Keeping local state untouched and requiring a server snapshot reload.");
            worldMapStatus = message;
            ShowUnconfirmedSnapshotFailure(
                ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationRaidRestore"),
                message,
                () => StartRestoreExistingColonySnapshot(settings.CurrentSnapshotId));
            return;
        }

        if (!TryParseServerUtc(recovery.ServerNowUtc, out DateTimeOffset serverNowUtc)
            || !TryParseServerUtc(recovery.StartedAtUtc, out DateTimeOffset startedAtUtc)
            || !TryParseServerUtc(recovery.DeadlineUtc, out DateTimeOffset deadlineUtc)
            || !TryParseServerUtc(recovery.FinalDeadlineUtc, out DateTimeOffset finalDeadlineUtc))
        {
            string message = ClashOfRimText.Key(
                "ClashOfRim.Raid.RestoreInvalidServerTime",
                recovery.EventId.Named("EVENTID"));
            Log.Warning("[ClashOfRim][Raid] Active raid recovery had invalid server times for event "
                + recovery.EventId
                + ".");
            worldMapStatus = message;
            ShowUnconfirmedSnapshotFailure(
                ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationRaidRestore"),
                message,
                () => StartRestoreExistingColonySnapshot(settings.CurrentSnapshotId));
            return;
        }

        session.StartedAtUtcTicks = startedAtUtc.UtcDateTime.Ticks;
        session.DeadlineUtcTicks = deadlineUtc.UtcDateTime.Ticks;
        session.FinalDeadlineUtcTicks = finalDeadlineUtc.UtcDateTime.Ticks;
        session.FinishInProgress = false;
        ClashOfRimGameComponent.SetActiveRaidBattleSession(session);

        if (serverNowUtc >= finalDeadlineUtc)
        {
            ClashLog.Message("[ClashOfRim][Raid] Restored active raid "
                + recovery.EventId
                + " after final deadline; closing the restored battle map and confirming timeout cleanup.");
            HandleRaidBattleFinalDeadlineExpired(session);
            return;
        }

        if (serverNowUtc >= deadlineUtc)
        {
            ClashLog.Message("[ClashOfRim][Raid] Restored active raid "
                + recovery.EventId
                + " after battle deadline; starting settlement upload.");
            StartFinishActiveRaidBattle(session, "RecoveredExpired");
            return;
        }

        TimeSpan remaining = deadlineUtc - serverNowUtc;
        ClashLog.Message("[ClashOfRim][Raid] Restored active raid "
            + recovery.EventId
            + " with remaining seconds="
            + Math.Max(0, (int)remaining.TotalSeconds)
            + ".");
        Messages.Message(
            ClashOfRimText.Key(
                "ClashOfRim.Raid.RestoreSucceeded",
                Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds)).Named("SECONDS")),
            MessageTypeDefOf.NeutralEvent,
            historical: false);
    }

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

    private void StartConfirmSettledRaidCleanupSnapshot(string eventId)
    {
        CloseUnconfirmedSnapshotFailureWindow();
        EndSnapshotUploadTransaction();
        CompleteLocalAtomicMutation();
        worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusCleanupSnapshotSucceeded", settings.CurrentSnapshotId.Named("SNAPSHOT"));
        Messages.Message(worldMapStatus, MessageTypeDefOf.NeutralEvent, historical: false);
        StartAutomaticEventRefresh(ClashOfRimText.Key("ClashOfRim.ServerEntry.ReasonRaidRecovery"));
        StartRestoreExistingColonySnapshot(
            settings.CurrentSnapshotId,
            restoreFailureCallback: message =>
            {
                worldMapStatus = message;
                ShowUnconfirmedSnapshotFailure(
                    ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationRaidRestore"),
                    message,
                    () => StartConfirmSettledRaidCleanupSnapshot(eventId));
            },
            allowDifferentSnapshotId: true);
    }

    private static bool TryParseServerUtc(string? value, out DateTimeOffset parsed)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed);
    }

    private static void EnqueueClashOfRimMainThreadAction(Action action)
    {
        if (action is null)
        {
            return;
        }

        if (Current.ProgramState == ProgramState.Playing)
        {
            ClashOfRimGameComponent.EnqueueMainThreadAction(action);
        }
        else
        {
            ClashOfRimMainMenuPatches.EnqueueMainThreadAction(action);
        }
    }

    private static void ShowServerEntryProgressWindow(string title, string message, float progress, bool canClose)
    {
        EnqueueClashOfRimMainThreadAction(() => ShowServerEntryProgressWindowNow(title, message, progress, canClose));
    }

    private static void ShowServerEntryProgressWindowNow(string title, string message, float progress, bool canClose)
    {
        ServerEntryProgressWindow? existing = Find.WindowStack.WindowOfType<ServerEntryProgressWindow>();
        if (existing is not null)
        {
            existing.UpdateStatus(title, message, progress, canClose);
            return;
        }

        Find.WindowStack.Add(new ServerEntryProgressWindow(title, message, progress, canClose));
    }

    private static void CloseServerEntryProgressWindowNow()
    {
        ServerEntryProgressWindow? existing = Find.WindowStack.WindowOfType<ServerEntryProgressWindow>();
        existing?.Close();
    }

    private static void ShowServerEntryFailureWindowNow(string message)
    {
        ShowServerEntryProgressWindowNow(
            ClashOfRimText.Key("ClashOfRim.ServerEntry.FailedTitle"),
            message,
            1f,
            canClose: true);
    }

    private static void ReportRestoreExistingColonySnapshotFailure(
        string message,
        Action<string>? restoreFailureCallback)
    {
        ShowServerEntryProgressWindowNow(
            ClashOfRimText.Key("ClashOfRim.ServerEntry.FailedTitle"),
            message,
            1f,
            canClose: true);
        restoreFailureCallback?.Invoke(message);
    }

}
