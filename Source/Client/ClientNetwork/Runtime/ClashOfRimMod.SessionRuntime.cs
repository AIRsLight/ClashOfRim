using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    private static readonly DataContractJsonSerializerSettings StreamJsonSerializerSettings = new()
    {
        UseSimpleDictionaryFormat = true
    };

    internal void StartRefreshEventsForTest()
    {
        if (!CanRunManualSync(out string failureReason))
        {
            eventQueueStatus = failureReason;
            return;
        }

        manualSyncInProgress = true;
        eventQueueStatus = ClashOfRimText.Key("ClashOfRim.Events.StatusRefreshing");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));

                ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto> queue =
                    await client.PullPendingEventsAsync();
                if (!queue.Success || queue.Response is null)
                {
                    eventQueueStatus = ClashOfRimText.Key(
                        "ClashOfRim.Events.StatusQueueFailed",
                        (queue.ErrorCode ?? string.Empty).Named("CODE"),
                        (queue.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? queueResult = queue.Response.Result;
                if (queueResult is not null && !queueResult.Accepted)
                {
                    eventQueueStatus = ClashOfRimText.Key(
                        "ClashOfRim.Events.StatusQueueRejected",
                        queueResult.ErrorCode.Named("CODE"),
                        (queueResult.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                eventQueueStatus = FormatEventQueue(queue.Response.EventQueue);
                CaptureEventIds(queue.Response.EventQueue);
                IReadOnlyList<string> eventIds = CopyLastEventQueueIds();
                ClashLog.Message($"[ClashOfRim][Events] refreshed queue ids={eventIds.Count}.");
                if (eventIds.Count == 0)
                {
                    eventDetailsStatus = ClashOfRimText.Key("ClashOfRim.Events.StatusNoDetails");
                    return;
                }

                ClashOfRimClientNetworkResult<ModPullEventDetailsResponseDto> details =
                    await client.PullEventDetailsAsync(eventIds);
                if (!details.Success || details.Response is null)
                {
                    eventDetailsStatus = ClashOfRimText.Key(
                        "ClashOfRim.Events.StatusDetailsFailed",
                        (details.ErrorCode ?? string.Empty).Named("CODE"),
                        (details.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? detailsResult = details.Response.Result;
                if (detailsResult is not null && !detailsResult.Accepted)
                {
                    eventDetailsStatus = ClashOfRimText.Key(
                        "ClashOfRim.Events.StatusDetailsRejected",
                        detailsResult.ErrorCode.Named("CODE"),
                        (detailsResult.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                eventDetailsStatus = FormatEventDetails(details.Response.Events);
                LogGiftDetails("RefreshDetails", details.Response.Events);
                CaptureEventDetails(details.Response.Events);
                IReadOnlyCollection<ModEventDetailDto> events = details.Response.Events.ToList();
                ClashOfRimGameComponent.EnqueueMainThreadAction(() => PostEventLetters(events, "manual-refresh"));
            }
            catch (Exception ex)
            {
                eventQueueStatus = ClashOfRimText.Key(
                    "ClashOfRim.Events.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Event refresh failed: " + ex);
            }
            finally
            {
                manualSyncInProgress = false;
            }
        });
    }

    internal void StartManualLogin()
    {
        if (!CanRunManualSync(out string failureReason))
        {
            loginStatus = failureReason;
            return;
        }

        manualSyncInProgress = true;
        loginStatus = ClashOfRimText.Key("ClashOfRim.Login.StatusLoggingIn");

        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));

                ClashOfRimClientNetworkResult<ModLoginResponseDto> result =
                    await client.LoginAsync("manual-sync");

                if (!result.Success || result.Response is null)
                {
                    loginStatus = ClashOfRimText.Key(
                        "ClashOfRim.Login.StatusFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? serverResult = result.Response.Result;
                if (serverResult is not null && !serverResult.Accepted)
                {
                    loginStatus = ClashOfRimText.Key(
                        "ClashOfRim.Login.StatusRejected",
                        serverResult.ErrorCode.Named("CODE"),
                        (serverResult.Message ?? string.Empty).Named("MESSAGE"));
                    ShowCompatibilityMismatchWindow(result.Response);
                    return;
                }

                loginStatus = ClashOfRimText.Key(
                    "ClashOfRim.Login.StatusSucceeded",
                    result.Response.ServerProtocolVersion.Named("PROTOCOL"),
                    result.Response.SessionId.Named("SESSION"));
                CaptureServerCompatibilityManifest(result.Response.ServerCompatibilityManifestJson);
                lastSessionId = result.Response.SessionId;
                sessionExpiredHandling = false;
                ApplyAdministratorFlag(result.Response.IsAdministrator);
                if (!string.IsNullOrWhiteSpace(result.Response.AuthenticatedUserId))
                {
                    settings.UserId = result.Response.AuthenticatedUserId!.Trim();
                }

                if (!string.IsNullOrWhiteSpace(result.Response.DisplayName))
                {
                    settings.DisplayName = result.Response.DisplayName!.Trim();
                }

                settings.AuthToken = result.Response.AuthToken ?? string.Empty;
                settings.Write();
                eventQueueStatus = FormatEventQueue(result.Response.EventQueue);
                CaptureEventIds(result.Response.EventQueue);
                CaptureWorldMapMarkers(result.Response.WorldMapMarkers, source: "manual-login");
                StartAutomaticEventRefresh(ClashOfRimText.Key("ClashOfRim.Login.ReasonManual"));
                StartRefreshPlayers(ClashOfRimText.Key("ClashOfRim.Login.ReasonManual"), requireManualGate: false);
            }
            catch (Exception ex)
            {
                loginStatus = ClashOfRimText.Key(
                    "ClashOfRim.Login.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Manual login failed: " + ex);
            }
            finally
            {
                manualSyncInProgress = false;
            }
        });
    }

    internal void StartManualPresence()
    {
        if (!settings.IsConfigured)
        {
            presenceStatus = ClashOfRimText.Key("ClashOfRim.Presence.StatusNotConfigured");
            return;
        }

        if (string.IsNullOrWhiteSpace(lastSessionId))
        {
            presenceStatus = ClashOfRimText.Key("ClashOfRim.Presence.StatusSessionMissing");
            Log.Warning("[ClashOfRim] Cannot start session stream without session id.");
            return;
        }

        presenceCancellation?.Cancel();
        var streamCancellation = new CancellationTokenSource();
        presenceCancellation = streamCancellation;
        CancellationToken cancellationToken = streamCancellation.Token;
        presenceInProgress = true;
        string sessionId = lastSessionId!;
        presenceStatus = ClashOfRimText.Key("ClashOfRim.Presence.StatusConnecting");
        ClashLog.Message("[ClashOfRim] Starting WebSocket session stream: user="
            + settings.UserId
            + ", colony="
            + settings.ColonyId
            + ", session="
            + sessionId);

        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));

                ClashOfRimClientNetworkResult<ModSessionStreamClosedDto> result =
                    await client.StreamSessionAsync(
                        sessionId,
                        lastNotificationVersion,
                        lastWorldConfigurationVersion,
                        streamEvent => HandleSessionStreamEventSafely(streamEvent, sessionId),
                        cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    presenceStatus = ClashOfRimText.Key("ClashOfRim.Presence.StatusDisconnectedManual");
                    return;
                }

                if (!result.Success || result.Response is null)
                {
                    presenceStatus = ClashOfRimText.Key(
                        "ClashOfRim.Presence.StatusFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                presenceStatus = result.Response.Cancelled
                    ? ClashOfRimText.Key("ClashOfRim.Presence.StatusDisconnectedManual")
                    : ClashOfRimText.Key("ClashOfRim.Presence.StatusClosed");
            }
            catch (Exception ex)
            {
                presenceStatus = ClashOfRimText.Key(
                    "ClashOfRim.Presence.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Session stream failed: " + ex);
            }
            finally
            {
                if (ReferenceEquals(presenceCancellation, streamCancellation))
                {
                    presenceInProgress = false;
                    presenceCancellation = null;
                }

                streamCancellation.Dispose();
            }
        });
    }

    internal void StopManualPresence()
    {
        presenceCancellation?.Cancel();
        presenceCancellation = null;
        presenceInProgress = false;
        presenceStatus = ClashOfRimText.Key("ClashOfRim.Presence.StatusDisconnecting");
    }

    internal void DisconnectMultiplayerSessionForExit()
    {
        string? sessionId = lastSessionId;
        if (!string.IsNullOrWhiteSpace(sessionId) && settings.IsConfigured)
        {
            var context = ClashOfRimClientNetworkContext.FromSettings(settings);
            Task.Run(async () =>
            {
                try
                {
                    using var httpClient = new HttpClient();
                    var client = new ClashOfRimModNetworkClient(httpClient, context);
                    ClashOfRimClientNetworkResult<ModLogoutResponseDto> result = await client.LogoutAsync(sessionId);
                    if (!result.Success || result.Response?.Result?.Accepted != true)
                    {
                        Log.Warning("[ClashOfRim] Explicit logout failed: "
                            + (result.Message ?? result.Response?.Result?.Message ?? result.ErrorCode ?? "unknown"));
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[ClashOfRim] Explicit logout exception: " + ex);
                }
            });
        }

        presenceCancellation?.Cancel();
        presenceCancellation = null;
        presenceInProgress = false;
        lastSessionId = null;
        ClearTransientMultiplayerSessionStateForExit();
        CaptureServerCompatibilityManifest(null);
        ApplyAdministratorFlag(false);
        settings.AuthToken = string.Empty;
        settings.Write();
        presenceStatus = ClashOfRimText.Key("ClashOfRim.Presence.StatusSessionDisconnected");
    }

    private void ClearTransientMultiplayerSessionStateForExit()
    {
        ClearLocalAtomicMutation();
        ClearPendingUnconfirmedSnapshotFailure();
        lock (automaticEventRefreshLock)
        {
            automaticEventRefreshInProgress = false;
            automaticEventRefreshQueued = false;
        }

        manualSyncInProgress = false;
        snapshotUploadInProgress = false;
        worldMapMarkerSyncInProgress = false;
        runtimeWorldObjectSyncInProgress = false;
        playerColonySiteRegistrationInProgress = false;
        worldConfigurationExtensionSyncInProgress = false;
        abandonPlayerColonyInProgress = false;
        caravanArrivalTargetRefreshInProgress = false;
        bankInProgress = false;
        mercenaryInProgress = false;
        serverShopInProgress = false;
        adminInProgress = false;
        tradeOrdersPageLoadInProgress = false;
        chatRefreshInProgress = false;
        chatSendInProgress = false;
        sessionExpiredHandling = false;
        blockAutomaticMapSessionForServerEntrySourceGame = false;
        serverEntrySourceGame = null;
        lastNotificationVersion = 0;
        lastWorldConfigurationVersion = 0;
        pendingInitialWorldConfigurationSubmit = false;
        pendingServerWorldConfiguration = null;
        pendingServerWorldSubstrate = null;
        pendingGiftConfirmationEventIds.Clear();
        postedEventLetterIds.Clear();
        appliedServerNotificationSideEffectIds.Clear();
        appliedDiplomacyEventSideEffectIds.Clear();
        playerColonySiteRegistrationSuppressed = false;
        lastRegisteredPlayerColonySiteSignature = null;
        settings.CurrentSnapshotId = string.Empty;
        settings.CurrentLineageToken = string.Empty;
        settings.TargetUserId = string.Empty;
        settings.TargetColonyId = string.Empty;
        settings.TargetSnapshotId = string.Empty;

        lock (eventStateLock)
        {
            lastEventQueueEventIds.Clear();
            lastEventDetails.Clear();
            lastEventReferences.Clear();
            lastEventReferenceGroups.Clear();
            lastPlayers.Clear();
            lastWorldMapMarkers.Clear();
            playersSnapshotVersion++;
            lastTradeOrders.Clear();
            tradeOrdersSnapshotVersion++;
            lastServerShopListings.Clear();
            serverShopListingsSnapshotVersion++;
            tradeOrdersHasMore = false;
            tradeOrdersTotalCount = 0;
            tradeOrdersScope = "Open";
        }

        lock (chatStateLock)
        {
            lastChatMessages.Clear();
            lastChatSequence = 0;
            lastReadPrivateChatSequence = 0;
            unreadPrivateChatCount = 0;
            chatMessagesSnapshotVersion++;
        }

        lock (colonySiteStateLock)
        {
            occupiedPlayerColonySites.Clear();
        }
    }

    internal void StartRefreshChatMessages(bool initialLoad = false)
    {
        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(settings.AuthToken))
        {
            chatStatus = ClashOfRimText.Key("ClashOfRim.Chat.StatusNotConnected");
            return;
        }

        if (chatRefreshInProgress)
        {
            return;
        }

        chatRefreshInProgress = true;
        long afterSequence;
        int limit = initialLoad ? 20 : 100;
        lock (chatStateLock)
        {
            afterSequence = initialLoad ? 0 : lastChatSequence;
        }

        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModListChatMessagesResponseDto> result =
                    await client.ListChatMessagesAsync(afterSequence, limit);
                if (!result.Success || result.Response is null)
                {
                    chatStatus = ClashOfRimText.Key("ClashOfRim.Chat.StatusRefreshFailed", result.ErrorCode.Named("CODE"), result.Message.Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    chatStatus = ClashOfRimText.Key("ClashOfRim.Chat.StatusRefreshRejected", response.ErrorCode.Named("CODE"), response.Message.Named("MESSAGE"));
                    return;
                }

                MergeChatMessages(result.Response.Messages);
                chatStatus = ClashOfRimText.Key(
                    "ClashOfRim.Chat.StatusRefreshed",
                    new NamedArgument(result.Response.Messages.Count, "COUNT"));
            }
            catch (Exception ex)
            {
                chatStatus = ClashOfRimText.Key("ClashOfRim.Chat.StatusException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Chat refresh failed: " + ex);
            }
            finally
            {
                chatRefreshInProgress = false;
            }
        });
    }

    internal void StartSendChatMessage(string rawText)
    {
        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(settings.AuthToken))
        {
            chatStatus = ClashOfRimText.Key("ClashOfRim.Chat.StatusNotConnected");
            return;
        }

        if (chatSendInProgress)
        {
            return;
        }

        if (!TryParseChatInput(rawText, out string? targetUserId, out string text, out string failure))
        {
            chatStatus = failure;
            Messages.Message(failure, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        chatSendInProgress = true;
        chatStatus = ClashOfRimText.Key("ClashOfRim.Chat.StatusSending");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModSendChatMessageResponseDto> result =
                    await client.SendChatMessageAsync(targetUserId, text);
                if (!result.Success || result.Response is null)
                {
                    chatStatus = ClashOfRimText.Key("ClashOfRim.Chat.StatusSendFailed", result.ErrorCode.Named("CODE"), result.Message.Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    chatStatus = ClashOfRimText.Key("ClashOfRim.Chat.StatusSendRejected", response.ErrorCode.Named("CODE"), response.Message.Named("MESSAGE"));
                    return;
                }

                if (result.Response.Message is not null)
                {
                    MergeChatMessages(new[] { result.Response.Message });
                }

                chatStatus = string.IsNullOrWhiteSpace(targetUserId)
                    ? ClashOfRimText.Key("ClashOfRim.Chat.StatusSentPublic")
                    : ClashOfRimText.Key("ClashOfRim.Chat.StatusSentPrivate", targetUserId.Named("TARGET"));
            }
            catch (Exception ex)
            {
                chatStatus = ClashOfRimText.Key("ClashOfRim.Chat.StatusException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Chat send failed: " + ex);
            }
            finally
            {
                chatSendInProgress = false;
            }
        });
    }

    internal void MarkPrivateChatRead()
    {
        lock (chatStateLock)
        {
            if (unreadPrivateChatCount <= 0)
            {
                return;
            }

            long latestPrivate = lastChatMessages
                .Where(message => string.Equals(message.Channel, "Private", StringComparison.Ordinal)
                    && string.Equals(message.TargetUserId, settings.UserId, StringComparison.Ordinal))
                .Select(message => message.Sequence)
                .DefaultIfEmpty(lastReadPrivateChatSequence)
                .Max();
            lastReadPrivateChatSequence = Math.Max(lastReadPrivateChatSequence, latestPrivate);
            RefreshUnreadPrivateChatCountLocked();
        }
    }

    private void MergeChatMessages(IEnumerable<ModChatMessageDto>? messages)
    {
        if (messages is null)
        {
            return;
        }

        lock (chatStateLock)
        {
            var knownIds = new HashSet<string>(lastChatMessages
                .Where(message => !string.IsNullOrWhiteSpace(message.MessageId))
                .Select(message => message.MessageId), StringComparer.Ordinal);
            bool changed = false;
            foreach (ModChatMessageDto message in messages)
            {
                if (message is null || string.IsNullOrWhiteSpace(message.MessageId) || knownIds.Contains(message.MessageId))
                {
                    continue;
                }

                lastChatMessages.Add(message);
                knownIds.Add(message.MessageId);
                lastChatSequence = Math.Max(lastChatSequence, message.Sequence);
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            lastChatMessages.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            if (lastChatMessages.Count > 120)
            {
                lastChatMessages.RemoveRange(0, lastChatMessages.Count - 120);
            }

            RefreshUnreadPrivateChatCountLocked();
            chatMessagesSnapshotVersion++;
        }
    }

    private void RefreshUnreadPrivateChatCountLocked()
    {
        unreadPrivateChatCount = lastChatMessages.Count(message =>
            string.Equals(message.Channel, "Private", StringComparison.Ordinal)
            && string.Equals(message.TargetUserId, settings.UserId, StringComparison.Ordinal)
            && message.Sequence > lastReadPrivateChatSequence);
    }

    private static bool TryParseChatInput(
        string rawText,
        out string? targetUserId,
        out string text,
        out string failure)
    {
        targetUserId = null;
        text = string.Empty;
        failure = string.Empty;
        string normalized = (rawText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            failure = ClashOfRimText.Key("ClashOfRim.Chat.EmptyMessage");
            return false;
        }

        if (normalized.StartsWith("@", StringComparison.Ordinal))
        {
            int separator = normalized.IndexOf(' ');
            if (separator <= 1)
            {
                failure = ClashOfRimText.Key("ClashOfRim.Chat.InvalidPrivateFormat");
                return false;
            }

            targetUserId = normalized.Substring(1, separator - 1).Trim();
            text = normalized.Substring(separator + 1).Trim();
            if (string.IsNullOrWhiteSpace(targetUserId) || string.IsNullOrWhiteSpace(text))
            {
                failure = ClashOfRimText.Key("ClashOfRim.Chat.InvalidPrivateFormat");
                return false;
            }
        }
        else
        {
            text = normalized;
        }

        if (text.Length > 500)
        {
            text = text.Substring(0, 500);
        }

        return true;
    }

    internal void StartSyncWorldMapMarkers()
    {
        RequestWorldMapMarkerRefresh("manual-refresh-world-map");
    }

    internal void RequestWorldMapMarkerRefresh(string reason)
    {
        ClashLog.Message("[ClashOfRim] Requesting world map marker refresh: "
            + reason
            + $", configured={settings.IsConfigured}, user={settings.UserId}, colony={settings.ColonyId}, hasWorldObjects={Find.WorldObjects is not null}, inProgress={worldMapMarkerSyncInProgress}");
        if (!settings.IsConfigured)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.WorldMap.StatusNotConfigured");
            Log.Warning("[ClashOfRim] World map marker sync skipped: " + worldMapStatus);
            return;
        }

        if (Find.WorldObjects is null)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.WorldMap.StatusWorldObjectsMissing");
            Log.Warning("[ClashOfRim] World map marker sync skipped: " + worldMapStatus);
            return;
        }

        if (worldMapMarkerSyncInProgress)
        {
            ClashLog.Message("[ClashOfRim] World map marker refresh merged because a request is already running: " + reason);
            return;
        }

        worldMapMarkerSyncInProgress = true;
        worldMapStatus = ClashOfRimText.Key("ClashOfRim.WorldMap.StatusSyncing");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModWorldMapMarkerDeliveryDto> result =
                    await client.SyncWorldMapMarkersAsync();
                if (!result.Success || result.Response is null)
                {
                    worldMapStatus = ClashOfRimText.Key(
                        "ClashOfRim.WorldMap.StatusSyncFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    Log.Warning("[ClashOfRim] World map marker sync failed: " + result.ErrorCode + " " + result.Message);
                    return;
                }

                ClashLog.Message("[ClashOfRim] World map marker sync response: "
                    + DescribeWorldMapMarkerDelivery(result.Response));
                CaptureWorldMapMarkers(result.Response, source: reason);
            }
            catch (Exception ex)
            {
                worldMapStatus = ClashOfRimText.Key(
                    "ClashOfRim.WorldMap.StatusSyncException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] World map marker sync failed: " + ex);
            }
            finally
            {
                worldMapMarkerSyncInProgress = false;
            }
        });
    }

    internal void StartRefreshCaravanArrivalTargets(string caravanTileSignature)
    {
        if (!CanRefreshCaravanArrivalTargets || string.IsNullOrWhiteSpace(caravanTileSignature))
        {
            return;
        }

        caravanArrivalTargetRefreshInProgress = true;
        worldMapStatus = ClashOfRimText.Key("ClashOfRim.WorldMap.StatusCaravanTargetsRefreshing");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));

                ClashOfRimClientNetworkResult<ModWorldMapMarkerDeliveryDto> markers =
                    await client.SyncWorldMapMarkersAsync();
                if (!markers.Success || markers.Response is null)
                {
                    worldMapStatus = ClashOfRimText.Key(
                        "ClashOfRim.WorldMap.StatusCaravanTargetsFailed",
                        (markers.ErrorCode ?? string.Empty).Named("CODE"),
                        (markers.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                CaptureWorldMapMarkers(markers.Response, source: "caravan-target-refresh");

                ClashOfRimClientNetworkResult<ModListTradeOrdersResponseDto> trades =
                    await client.ListTradeOrdersAsync("Open");
                if (!trades.Success || trades.Response is null)
                {
                    tradeStatus = ClashOfRimText.Key(
                        "ClashOfRim.Trade.StatusMarketRefreshFailed",
                        trades.ErrorCode.Named("CODE"),
                        trades.Message.Named("MESSAGE"));
                    return;
                }

                if (trades.Response.Result is not null && !trades.Response.Result.Accepted)
                {
                    tradeStatus = ClashOfRimText.Key(
                        "ClashOfRim.Trade.StatusMarketRejected",
                        trades.Response.Result.ErrorCode.Named("CODE"),
                        trades.Response.Result.Message.Named("MESSAGE"));
                    return;
                }

                IReadOnlyList<ModTradeOrderSummaryDto> tradeOrders =
                    trades.Response.Orders ?? new List<ModTradeOrderSummaryDto>();
                await HydrateTradeOrderPawnPackagesAsync(client, tradeOrders);
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    TradeUiUtility.PreparePawnPreviewsForTradeOrders(tradeOrders));
                lock (eventStateLock)
                {
                    tradeMarketplaceEnabled = trades.Response.TradeMarketplaceEnabled;
                    tradeOrdersScope = "Open";
                    tradeOrdersHasMore = trades.Response.HasMore;
                    tradeOrdersTotalCount = Math.Max(0, trades.Response.TotalCount);
                    lastTradeOrders.Clear();
                    lastTradeOrders.AddRange(tradeOrders);
                    tradeOrdersSnapshotVersion++;
                }

                tradeStatus = FormatPagedTradeOrdersStatus();
            }
            catch (Exception ex)
            {
                worldMapStatus = ClashOfRimText.Key(
                    "ClashOfRim.WorldMap.StatusCaravanTargetsException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Caravan arrival target refresh failed: " + ex);
            }
            finally
            {
                caravanArrivalTargetRefreshInProgress = false;
            }
        });
    }

    internal void StartSyncRuntimeWorldObjects()
    {
        if (!CanSyncRuntimeWorldObjects)
        {
            return;
        }

        List<ModRuntimeWorldObjectMarkerDto> objects = ReadCurrentRuntimeWorldObjects();
        runtimeWorldObjectSyncInProgress = true;
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModSyncRuntimeWorldObjectsResponseDto> result =
                    await client.SyncRuntimeWorldObjectsAsync(objects);
                if (!result.Success || result.Response is null)
                {
                    Log.Warning("[ClashOfRim] Runtime world object sync failed: " + result.ErrorCode + " " + result.Message);
                    return;
                }

                if (result.Response.Result is not null && !result.Response.Result.Accepted)
                {
                    Log.Warning("[ClashOfRim] Runtime world object sync rejected: "
                        + result.Response.Result.ErrorCode
                        + " "
                        + result.Response.Result.Message);
                    return;
                }

                CaptureWorldMapMarkers(result.Response.WorldMapMarkers, source: "runtime-world-object-sync");
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim] Runtime world object sync exception: " + ex);
            }
            finally
            {
                runtimeWorldObjectSyncInProgress = false;
            }
        });
    }

    internal void StartRegisterPlayerColonySites()
    {
        if (!CanRegisterPlayerColonySites)
        {
            return;
        }

        List<ModPlayerColonySiteDto> sites = ReadCurrentPlayerColonySites(
            settings.UserId,
            settings.ColonyId,
            BuildCurrentColonyAppearanceDto(settings));
        List<ModWorldConfigurationExtensionDto> extensions = ClashOfRimCompatibilityApi.CollectCurrentWorldConfigurationExtensions(
            settings.UserId,
            settings.ColonyId,
            settings.CurrentSnapshotId ?? "unknown").ToList();
        string signature = BuildPlayerColonySiteSignature(sites, extensions);
        ClashLog.Message("[ClashOfRim] Checking player colony site registration: sites="
            + sites.Count
            + ", worldExtensions="
            + extensions.Count
            + ", signatureHash="
            + ShortHash(signature)
            + ", signatureBytes="
            + Encoding.UTF8.GetByteCount(signature).ToString()
            + ", lastSignatureHash="
            + (lastRegisteredPlayerColonySiteSignature is null ? "<null>" : ShortHash(lastRegisteredPlayerColonySiteSignature))
            + ", sample="
            + DescribePlayerColonySiteSample(sites));
        if (sites.Count == 0 && extensions.Count == 0)
        {
            lastRegisteredPlayerColonySiteSignature = signature;
            ClashLog.Message("[ClashOfRim] Player colony site registration skipped because no local player colony site or world extension is visible.");
            return;
        }

        if (sites.Count == 0)
        {
            ClashLog.Message("[ClashOfRim] No local player colony site is visible; syncing local ideos without replacing server colony sites. "
                + DescribePlayerColonyScanContext());
        }

        if (lastRegisteredPlayerColonySiteSignature is not null
            && string.Equals(signature, lastRegisteredPlayerColonySiteSignature, StringComparison.Ordinal))
        {
            return;
        }

        bool suppressWorldConfigurationNotification = suppressNextPlayerColonySiteRegistrationNotification;
        suppressNextPlayerColonySiteRegistrationNotification = false;
        playerColonySiteRegistrationInProgress = true;
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModRegisterPlayerColonySitesResponseDto> result =
                    await client.RegisterPlayerColonySitesAsync(
                        sites,
                        extensions: extensions,
                        suppressWorldConfigurationNotification: suppressWorldConfigurationNotification);
                if (!result.Success || result.Response is null)
                {
                    Log.Warning("[ClashOfRim] Player colony site registration failed: " + result.ErrorCode + " " + result.Message);
                    return;
                }

                if (result.Response.Result?.Accepted != true)
                {
                    Log.Warning("[ClashOfRim] Player colony site registration rejected: " + result.Response.Result?.ErrorCode + " " + result.Response.Result?.Message);
                    return;
                }

                lastRegisteredPlayerColonySiteSignature = signature;
                ModWorldConfigurationDto? worldConfiguration = result.Response.WorldConfiguration;
                ModWorldMapMarkerDeliveryDto? worldMapMarkers = result.Response.WorldMapMarkers;
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    ApplyServerWorldConfigurationExtensionCatalog(worldConfiguration);
                    UpdateOccupiedPlayerColonySites(worldConfiguration);
                    CaptureWorldMapMarkers(worldMapMarkers, source: "player-colony-site-registration");
                });
                ClashLog.Message("[ClashOfRim] Player colony sites registered: " + result.Response.AcceptedCount);
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim] Player colony site registration exception: " + ex);
            }
            finally
            {
                playerColonySiteRegistrationInProgress = false;
            }
        });
    }

    internal void RequestPlayerColonySiteRegistration(string reason, bool suppressWorldConfigurationNotification = false)
    {
        lastRegisteredPlayerColonySiteSignature = null;
        suppressNextPlayerColonySiteRegistrationNotification |= suppressWorldConfigurationNotification;
        ClashLog.Message("[ClashOfRim] Player colony site registration requested: " + reason);
        ClashOfRimGameComponent.EnqueueMainThreadAction(StartRegisterPlayerColonySites);
    }

    internal void StartSyncWorldConfigurationExtensions()
    {
        if (!CanSyncWorldConfigurationExtensions)
        {
            return;
        }

        List<ModWorldConfigurationExtensionDto> extensions = ClashOfRimCompatibilityApi.CollectCurrentWorldConfigurationExtensions(
            settings.UserId,
            settings.ColonyId,
            settings.CurrentSnapshotId ?? "unknown").ToList();
        if (extensions.Count == 0)
        {
            return;
        }

        string signature = BuildWorldConfigurationExtensionSignature(extensions);
        if (lastSyncedWorldConfigurationExtensionSignature is not null
            && string.Equals(signature, lastSyncedWorldConfigurationExtensionSignature, StringComparison.Ordinal))
        {
            return;
        }

        worldConfigurationExtensionSyncInProgress = true;
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModRegisterPlayerColonySitesResponseDto> result =
                    await client.RegisterPlayerColonySitesAsync(
                        Array.Empty<ModPlayerColonySiteDto>(),
                        extensions: extensions);
                if (!result.Success || result.Response is null)
                {
                    Log.Warning("[ClashOfRim][WorldExtensions] World configuration extension sync failed: " + result.ErrorCode + " " + result.Message);
                    return;
                }

                if (result.Response.Result?.Accepted != true)
                {
                    Log.Warning("[ClashOfRim][WorldExtensions] World configuration extension sync rejected: " + result.Response.Result?.ErrorCode + " " + result.Response.Result?.Message);
                    return;
                }

                lastSyncedWorldConfigurationExtensionSignature = signature;
                ModWorldConfigurationDto? worldConfiguration = result.Response.WorldConfiguration;
                ModWorldMapMarkerDeliveryDto? worldMapMarkers = result.Response.WorldMapMarkers;
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    ApplyServerWorldConfigurationExtensionCatalog(worldConfiguration);
                    UpdateOccupiedPlayerColonySites(worldConfiguration);
                    CaptureWorldMapMarkers(worldMapMarkers, source: "world-configuration-extension-sync");
                });
                ClashLog.Message("[ClashOfRim] World configuration extensions synced: " + extensions.Count);
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim] World configuration extension sync exception: " + ex);
            }
            finally
            {
                worldConfigurationExtensionSyncInProgress = false;
            }
        });
    }

    internal void RequestWorldConfigurationExtensionSync(string reason)
    {
        lastSyncedWorldConfigurationExtensionSignature = null;
        ClashLog.Message("[ClashOfRim][WorldExtensions] World configuration extension sync requested: " + reason);
        ClashOfRimGameComponent.EnqueueMainThreadAction(StartSyncWorldConfigurationExtensions);
    }

    internal void OpenAbandonPlayerColonyConfirmation(MapParent colony)
    {
        if (!settings.IsConfigured)
        {
            Messages.Message(ClashOfRimText.Key("ClashOfRim.AbandonColony.StatusNotConfigured"), MessageTypeDefOf.RejectInput, false);
            return;
        }

        if (abandonPlayerColonyInProgress)
        {
            Messages.Message(ClashOfRimText.Key("ClashOfRim.AbandonColony.StatusSubmitting"), MessageTypeDefOf.RejectInput, false);
            return;
        }

        Find.WindowStack.Add(new AbandonPlayerColonyConfirmationWindow(this, colony));
    }

    internal void StartAbandonPlayerColony(MapParent colony)
    {
        if (!settings.IsConfigured)
        {
            Messages.Message(ClashOfRimText.Key("ClashOfRim.AbandonColony.StatusNotConfigured"), MessageTypeDefOf.RejectInput, false);
            return;
        }

        if (abandonPlayerColonyInProgress)
        {
            return;
        }

        string idempotencyKey = $"abandon-colony:{settings.UserId}:{settings.ColonyId}:{settings.CurrentSnapshotId}:{DateTime.UtcNow.Ticks}";
        abandonPlayerColonyInProgress = true;
        worldMapStatus = ClashOfRimText.Key("ClashOfRim.AbandonColony.StatusSubmitting");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModAbandonPlayerColonyResponseDto> result =
                    await client.AbandonPlayerColonyAsync(idempotencyKey);
                if (!result.Success || result.Response is null)
                {
                    worldMapStatus = ClashOfRimText.Key(
                        "ClashOfRim.AbandonColony.StatusFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                        Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, false));
                    return;
                }

                if (result.Response.Result?.Accepted != true)
                {
                    worldMapStatus = ClashOfRimText.Key(
                        "ClashOfRim.AbandonColony.StatusRejected",
                        (result.Response.Result?.ErrorCode.ToString() ?? string.Empty).Named("CODE"),
                        (result.Response.Result?.Message ?? string.Empty).Named("MESSAGE"));
                    ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                        Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, false));
                    return;
                }

                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    presenceCancellation?.Cancel();
                    lastSessionId = null;
                    CaptureServerCompatibilityManifest(null);
                    settings.CurrentSnapshotId = string.Empty;
                    settings.CurrentLineageToken = string.Empty;
                    settings.AuthToken = string.Empty;
                    lastRegisteredPlayerColonySiteSignature = null;
                    pendingGiftConfirmationEventIds.Clear();
                    postedEventLetterIds.Clear();
                    appliedServerNotificationSideEffectIds.Clear();
                    appliedDiplomacyEventSideEffectIds.Clear();
                    lock (eventStateLock)
                    {
                        lastEventQueueEventIds.Clear();
                        lastEventDetails.Clear();
                        lastTradeOrders.Clear();
                        tradeOrdersSnapshotVersion++;
                        tradeOrdersHasMore = false;
                        tradeOrdersTotalCount = 0;
                        tradeOrdersScope = "Open";
                    }

                    UpdateOccupiedPlayerColonySites(result.Response.WorldConfiguration);
                    CaptureWorldMapMarkers(result.Response.WorldMapMarkers, source: "abandon-colony");
                    settings.Write();
                    Messages.Message(ClashOfRimText.Key("ClashOfRim.AbandonColony.StatusSucceeded"), MessageTypeDefOf.NeutralEvent, false);
                    GenScene.GoToMainMenu();
                });
            }
            catch (Exception ex)
            {
                worldMapStatus = ClashOfRimText.Key(
                    "ClashOfRim.AbandonColony.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Player colony abandon exception: " + ex);
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, false));
            }
            finally
            {
                abandonPlayerColonyInProgress = false;
            }
        });
    }

    private void HandleSessionStreamEvent(ModSessionStreamEvent serverEvent, string? streamSessionId)
    {
        long? notificationVersion = ExtractNotificationVersion(serverEvent.Data);
        if (notificationVersion.HasValue)
        {
            lastNotificationVersion = Math.Max(lastNotificationVersion, notificationVersion.Value);
        }

        long? worldConfigurationVersion = ExtractWorldConfigurationVersion(serverEvent.Data);
        if (worldConfigurationVersion.HasValue)
        {
            lastWorldConfigurationVersion = Math.Max(lastWorldConfigurationVersion, worldConfigurationVersion.Value);
        }

        if (string.Equals(serverEvent.EventName, "presence", StringComparison.Ordinal))
        {
            presenceStatus = ClashOfRimText.Key("ClashOfRim.Presence.StatusConnected", lastNotificationVersion.Named("VERSION"));
            StartRefreshChatMessages(initialLoad: true);
            return;
        }

        if (string.Equals(serverEvent.EventName, "ledgerChanged", StringComparison.Ordinal))
        {
            presenceStatus = ClashOfRimText.Key("ClashOfRim.Presence.StatusLedgerChanged", lastNotificationVersion.Named("VERSION"));
            eventQueueStatus = ClashOfRimText.Key("ClashOfRim.Presence.StatusLedgerChangedSyncing");
            StartAutomaticEventRefresh(ClashOfRimText.Key("ClashOfRim.Presence.ReasonLedgerChanged"));
            return;
        }

        if (string.Equals(serverEvent.EventName, "chatChanged", StringComparison.Ordinal))
        {
            presenceStatus = ClashOfRimText.Key("ClashOfRim.Presence.StatusChatChanged");
            StartRefreshChatMessages();
            return;
        }

        if (string.Equals(serverEvent.EventName, "worldConfigurationChanged", StringComparison.Ordinal))
        {
            presenceStatus = ClashOfRimText.Key("ClashOfRim.Presence.StatusWorldConfigurationChanged", lastWorldConfigurationVersion.Named("VERSION"));
            StartRefreshWorldConfigurationCatalog(
                ClashOfRimText.Key("ClashOfRim.Presence.ReasonWorldConfigurationChanged"),
                includeGenerationBaseline: true);
            return;
        }

        if (string.Equals(serverEvent.EventName, "playerOnline", StringComparison.Ordinal))
        {
            HandlePlayerOnlineStreamEvent(serverEvent.Data);
            return;
        }

        if (string.Equals(serverEvent.EventName, "error", StringComparison.Ordinal))
        {
            presenceStatus = ClashOfRimText.Key(
                "ClashOfRim.Presence.StatusStreamError",
                ClashOfRimText.SafeArgument(serverEvent.Data).Named("MESSAGE"));
            if (IsSessionExpiredMessage(serverEvent.Data))
            {
                HandleSessionExpired(
                    ClashOfRimText.Key("ClashOfRim.SessionExpired.Message"),
                    observedAuthToken: null,
                    observedSessionId: streamSessionId);
            }
        }
    }

    private void HandleSessionStreamEventSafely(ModSessionStreamEvent serverEvent, string? streamSessionId)
    {
        try
        {
            HandleSessionStreamEvent(serverEvent, streamSessionId);
        }
        catch (Exception ex)
        {
            presenceStatus = "Online event handling failed: " + ex.Message;
            Log.Warning("[ClashOfRim] Session stream event failed: event="
                + (serverEvent.EventName ?? "<null>")
                + ", session="
                + (streamSessionId ?? "<null>")
                + ", error="
                + ex);
        }
    }

    private void HandlePlayerOnlineStreamEvent(string data)
    {
        ModPlayerOnlineStreamEventDto? payload = TryDeserializeStreamPayload<ModPlayerOnlineStreamEventDto>(data);
        string? onlineUserId = payload?.OnlineUserId;
        if (string.IsNullOrWhiteSpace(onlineUserId)
            || string.Equals(onlineUserId, settings.UserId, StringComparison.Ordinal))
        {
            return;
        }

        string message = ClashOfRimText.Key(
            "ClashOfRim.Presence.PlayerOnline",
            ClashOfRimText.SafeArgument(onlineUserId).Named("PLAYER"));
        ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
            Messages.Message(message, MessageTypeDefOf.NeutralEvent, historical: false));
    }

    private static T? TryDeserializeStreamPayload<T>(string data)
        where T : class
    {
        try
        {
            var serializer = new DataContractJsonSerializer(typeof(T), StreamJsonSerializerSettings);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(data ?? string.Empty));
            return serializer.ReadObject(stream) as T;
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim] Failed to parse session stream payload " + typeof(T).Name + ": " + ex.Message);
            return null;
        }
    }

    internal void RequestServerWorldBaselineRefresh(string reason)
    {
        StartRefreshWorldConfigurationCatalog(reason, includeGenerationBaseline: true);
    }

    private void StartRefreshWorldConfigurationCatalog(string reason, bool includeGenerationBaseline = false)
    {
        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(settings.AuthToken))
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.WorldCatalog.StatusNotConnected");
            return;
        }

        worldMapStatus = ClashOfRimText.Key("ClashOfRim.WorldCatalog.StatusRefreshing", reason.Named("REASON"));
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
                ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto> result =
                    await client.PrepareWorldSessionAsync();
                if (!result.Success || result.Response is null)
                {
                    worldMapStatus = ClashOfRimText.Key(
                        "ClashOfRim.WorldCatalog.StatusFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    if (!IsNetworkCancellationFailure(result))
                    {
                        Log.Warning("[ClashOfRim] World configuration refresh failed: " + result.ErrorCode + " " + result.Message);
                    }

                    return;
                }

                ModPrepareWorldSessionResponseDto response = result.Response;
                CaptureServerCompatibilityManifest(response.ServerCompatibilityManifestJson);
                if (response.Result?.Accepted != true)
                {
                    worldMapStatus = ClashOfRimText.Key(
                        "ClashOfRim.WorldCatalog.StatusRejected",
                        (response.Result is null ? string.Empty : response.Result.ErrorCode.ToString()).Named("CODE"),
                        (response.Result?.Message ?? string.Empty).Named("MESSAGE"));
                    Log.Warning("[ClashOfRim] World configuration refresh rejected: "
                        + response.Result?.ErrorCode
                        + " "
                        + response.Result?.Message);
                    return;
                }

                ModWorldConfigurationDto? configuration = response.WorldConfiguration;
                if (response.WorldConfigured
                    && (configuration is null
                        || includeGenerationBaseline && !HasGenerationBaseline(configuration)))
                {
                    ClashOfRimClientNetworkResult<ModGetWorldConfigurationResponseDto> configurationResult =
                        await client.GetWorldConfigurationAsync(
                            includeGenerationBaseline: includeGenerationBaseline,
                            includePlayerColonySites: true,
                            includeWorldExtensions: true);
                    if (!configurationResult.Success || configurationResult.Response is null)
                    {
                        worldMapStatus = ClashOfRimText.Key(
                            "ClashOfRim.WorldCatalog.StatusFailed",
                            (configurationResult.ErrorCode ?? string.Empty).Named("CODE"),
                            (configurationResult.Message ?? string.Empty).Named("MESSAGE"));
                        return;
                    }

                    if (configurationResult.Response.Result?.Accepted != true)
                    {
                        worldMapStatus = ClashOfRimText.Key(
                            "ClashOfRim.WorldCatalog.StatusRejected",
                            (configurationResult.Response.Result is null ? string.Empty : configurationResult.Response.Result.ErrorCode.ToString()).Named("CODE"),
                            (configurationResult.Response.Result?.Message ?? string.Empty).Named("MESSAGE"));
                        return;
                    }

                    configuration = configurationResult.Response.WorldConfiguration;
                }

                bool wasAdministrator = isAdministrator;
                ApplyAdministratorFlag(response.IsAdministrator);
                if (!response.IsAdministrator)
                {
                    lastAdminStatus = null;
                }
                else if (!wasAdministrator)
                {
                    StartRefreshAdminStatus();
                }

                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (includeGenerationBaseline && configuration is not null)
                    {
                        ApplyWorldBaseline(configuration);
                    }
                    else
                    {
                        ApplyServerWorldConfigurationExtensionCatalog(configuration);
                    }

                    UpdateOccupiedPlayerColonySites(configuration);
                    worldMapStatus = configuration is null
                        ? ClashOfRimText.Key("ClashOfRim.WorldCatalog.StatusNoConfiguration")
                        : ClashOfRimText.Key(
                            "ClashOfRim.WorldCatalog.StatusSucceeded",
                            FormatWorldConfigurationExtensionSummary(configuration).Named("SUMMARY"),
                            configuration.PlayerColonySites.Count.Named("SITES"));
                });
            }
            catch (Exception ex)
            {
                worldMapStatus = ClashOfRimText.Key(
                    "ClashOfRim.WorldCatalog.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] World configuration refresh exception: " + ex);
            }
        });
    }

    private static bool HasGenerationBaseline(ModWorldConfigurationDto configuration)
    {
        return configuration.FactionDefNames.Count > 0
            || configuration.Factions.Count > 0
            || configuration.WorldObjects.Count > 0;
    }

    private static string FormatWorldConfigurationExtensionSummary(ModWorldConfigurationDto configuration)
    {
        IReadOnlyList<WorldConfigurationExtensionSummaryItem> items =
            ClashOfRimCompatibilityApi.GetWorldConfigurationExtensionSummary(configuration);
        if (items.Count == 0)
        {
            return ClashOfRimText.Key("ClashOfRim.None");
        }

        return string.Join(
            ", ",
            items.Select(item => string.IsNullOrWhiteSpace(item.Label)
                ? item.Value
                : item.Label + " " + item.Value));
    }

    private static bool IsNetworkCancellationFailure<T>(ClashOfRimClientNetworkResult<T> result)
    {
        return string.Equals(result.ErrorCode, nameof(TaskCanceledException), StringComparison.Ordinal);
    }

    private void HandleSessionExpired(string? message, string? observedAuthToken = null, string? observedSessionId = null)
    {
        if (!IsCurrentSessionExpiredSignal(observedAuthToken, observedSessionId))
        {
            ClashLog.Message(
                "[ClashOfRim] Ignored stale session-expired signal: tokenMatches="
                + (string.IsNullOrWhiteSpace(observedAuthToken) || string.Equals(observedAuthToken, settings.AuthToken, StringComparison.Ordinal))
                + ", sessionMatches="
                + (string.IsNullOrWhiteSpace(observedSessionId) || string.Equals(observedSessionId, lastSessionId, StringComparison.Ordinal)));
            return;
        }

        if (sessionExpiredHandling)
        {
            return;
        }

        sessionExpiredHandling = true;
        presenceCancellation?.Cancel();
        presenceCancellation = null;
        presenceInProgress = false;
        string dialogMessage = ClashOfRimText.Key("ClashOfRim.SessionExpired.Message");
        ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
        {
            lastSessionId = null;
            CaptureServerCompatibilityManifest(null);
            settings.AuthToken = string.Empty;
            settings.Write();
            loginStatus = ClashOfRimText.Key("ClashOfRim.SessionExpired.Status");
            presenceStatus = loginStatus;
            Find.MainTabsRoot?.SetCurrentTab(null);
            if (Find.WindowStack.WindowOfType<SessionExpiredWindow>() is null)
            {
                Find.WindowStack.Add(new SessionExpiredWindow(dialogMessage, GenScene.GoToMainMenu));
            }
        });
    }

    private bool IsCurrentSessionExpiredSignal(string? observedAuthToken, string? observedSessionId)
    {
        if (!string.IsNullOrWhiteSpace(observedAuthToken)
            && !string.Equals(observedAuthToken, settings.AuthToken, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(observedSessionId)
            && !string.Equals(observedSessionId, lastSessionId, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool IsSessionExpiredMessage(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && (message!.IndexOf("\u4f1a\u8bdd\u5df2\u8fc7\u671f", StringComparison.Ordinal) >= 0
                || message.IndexOf("session has expired", StringComparison.OrdinalIgnoreCase) >= 0);
    }


    internal void StartMapServerSession()
    {
        if (Current.ProgramState != ProgramState.Playing)
        {
            loginStatus = ClashOfRimText.Key("ClashOfRim.MapSession.StatusNoMap");
            Log.Warning("[ClashOfRim] Refused to start map server session outside Playing state: programState=" + Current.ProgramState + ".");
            return;
        }

        if (Find.CurrentMap is null)
        {
            loginStatus = ClashOfRimText.Key("ClashOfRim.MapSession.StatusNoMap");
            return;
        }

        if (!settings.IsConfigured)
        {
            loginStatus = ClashOfRimText.Key("ClashOfRim.MapSession.StatusNotConfigured");
            return;
        }

        if (!TryBeginSnapshotUploadTransaction())
        {
            loginStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            snapshotUploadStatus = loginStatus;
            return;
        }

        loginStatus = ClashOfRimText.Key("ClashOfRim.MapSession.StatusUploadingInitialSnapshot");
        snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.MapSession.StatusPackagingCurrentMap");

        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                loginStatus = ClashOfRimText.Key("ClashOfRim.Login.StatusLoggingIn");
                ClashOfRimClientNetworkResult<ModLoginResponseDto> login =
                    await client.LoginAsync("map-session");

                if (!login.Success || login.Response is null)
                {
                    loginStatus = ClashOfRimText.Key(
                        "ClashOfRim.MapSession.StatusLoginFailed",
                        (login.ErrorCode ?? string.Empty).Named("CODE"),
                        (login.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? serverResult = login.Response.Result;
                if (serverResult is not null && !serverResult.Accepted)
                {
                    loginStatus = ClashOfRimText.Key(
                        "ClashOfRim.MapSession.StatusLoginRejected",
                        serverResult.ErrorCode.Named("CODE"),
                        (serverResult.Message ?? string.Empty).Named("MESSAGE"));
                    ShowCompatibilityMismatchWindow(login.Response);
                    return;
                }

                CaptureServerCompatibilityManifest(login.Response.ServerCompatibilityManifestJson);
                lastSessionId = login.Response.SessionId;
                sessionExpiredHandling = false;
                ApplyAdministratorFlag(login.Response.IsAdministrator);
                if (!string.IsNullOrWhiteSpace(login.Response.AuthenticatedUserId))
                {
                    settings.UserId = login.Response.AuthenticatedUserId!.Trim();
                }

                if (!string.IsNullOrWhiteSpace(login.Response.DisplayName))
                {
                    settings.DisplayName = login.Response.DisplayName!.Trim();
                }

                settings.AuthToken = login.Response.AuthToken ?? string.Empty;
                settings.Write();

                var uploadService = new ModSnapshotUploadService(settings);
                ModSnapshotUploadResult upload = await uploadService.UploadConfiguredSnapshotAsync(
                    snapshotUploadKind: ModSnapshotUploadKinds.MapSessionSynchronization);
                if (!upload.Success)
                {
                    snapshotUploadStatus = ClashOfRimText.Key(
                        "ClashOfRim.MapSession.StatusUploadFailed",
                        (upload.ErrorCode ?? string.Empty).Named("CODE"),
                        (upload.Message ?? string.Empty).Named("MESSAGE"));
                    loginStatus = ClashOfRimText.Key("ClashOfRim.MapSession.StatusUploadAborted");
                    return;
                }

                snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.MapSession.StatusUploadSucceeded", (upload.AcceptedSnapshotId ?? string.Empty).Named("SNAPSHOT"));
                loginStatus = ClashOfRimText.Key(
                    "ClashOfRim.MapSession.StatusSucceeded",
                    (upload.AcceptedSnapshotId ?? string.Empty).Named("SNAPSHOT"),
                    login.Response.SessionId.Named("SESSION"));
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    Messages.Message(
                        ClashOfRimText.Key(
                            "ClashOfRim.ServerSessionConnectedMessage",
                            upload.AcceptedSnapshotId.Named("SNAPSHOT"),
                            login.Response.SessionId.Named("SESSION")),
                        MessageTypeDefOf.PositiveEvent,
                        historical: false));
                eventQueueStatus = FormatEventQueue(login.Response.EventQueue);
                CaptureEventIds(login.Response.EventQueue);
                CaptureWorldMapMarkers(login.Response.WorldMapMarkers, source: "map-session-login");
                StartAutomaticEventRefresh(ClashOfRimText.Key("ClashOfRim.MapSession.ReasonLogin"));
                StartRefreshPlayers(ClashOfRimText.Key("ClashOfRim.MapSession.ReasonLogin"), requireManualGate: false);
                StartRefreshChatMessages(initialLoad: true);
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    RequestPlayerColonySiteRegistration(ClashOfRimText.Key("ClashOfRim.MapSession.ReasonLoginSucceeded")));

                if (!presenceInProgress)
                {
                    StartManualPresence();
                }
            }
            catch (Exception ex)
            {
                loginStatus = ClashOfRimText.Key(
                    "ClashOfRim.MapSession.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Map server session failed: " + ex);
            }
            finally
            {
                EndSnapshotUploadTransaction();
            }
        });
    }
}
