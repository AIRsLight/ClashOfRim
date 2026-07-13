using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.EventLetters;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.Support;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    private void StartAutomaticEventRefresh(string reason)
    {
        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            QueueAutomaticEventRefreshAfterLocalAtomicMutation(reason, atomicMessage);
            eventQueueStatus = atomicMessage;
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            eventQueueStatus = failureReason;
            return;
        }

        lock (automaticEventRefreshLock)
        {
            if (automaticEventRefreshInProgress)
            {
                automaticEventRefreshQueued = true;
                eventQueueStatus = ClashOfRimText.Key("ClashOfRim.Events.StatusAutoMerged", reason.Named("REASON"));
                return;
            }

            automaticEventRefreshInProgress = true;
        }

        eventQueueStatus = ClashOfRimText.Key("ClashOfRim.Events.StatusAutoRefreshing", reason.Named("REASON"));
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
                        "ClashOfRim.Events.StatusAutoQueueFailed",
                        (queue.ErrorCode ?? string.Empty).Named("CODE"),
                        (queue.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? queueResult = queue.Response.Result;
                if (queueResult is not null && !queueResult.Accepted)
                {
                    eventQueueStatus = ClashOfRimText.Key(
                        "ClashOfRim.Events.StatusAutoQueueRejected",
                        queueResult.ErrorCode.Named("CODE"),
                        (queueResult.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                eventQueueStatus = FormatEventQueue(queue.Response.EventQueue);
                CaptureEventIds(queue.Response.EventQueue);
                IReadOnlyList<string> eventIds = CopyLastEventQueueIds();
                if (eventIds.Count == 0)
                {
                    eventDetailsStatus = ClashOfRimText.Key("ClashOfRim.Events.StatusAutoNoEvents");
                    return;
                }

                ClashOfRimClientNetworkResult<ModPullEventDetailsResponseDto> details =
                    await client.PullEventDetailsAsync(eventIds);
                if (!details.Success || details.Response is null)
                {
                    eventDetailsStatus = ClashOfRimText.Key(
                        "ClashOfRim.Events.StatusAutoDetailsFailed",
                        (details.ErrorCode ?? string.Empty).Named("CODE"),
                        (details.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? detailsResult = details.Response.Result;
                if (detailsResult is not null && !detailsResult.Accepted)
                {
                    eventDetailsStatus = ClashOfRimText.Key(
                        "ClashOfRim.Events.StatusAutoDetailsRejected",
                        detailsResult.ErrorCode.Named("CODE"),
                        (detailsResult.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                eventDetailsStatus = FormatEventDetails(details.Response.Events);
                LogGiftDetails("AutoEventDetails", details.Response.Events);
                CaptureEventDetails(details.Response.Events);
                IReadOnlyCollection<ModEventDetailDto> events = details.Response.Events.ToList();
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (TryRejectBlockedByLocalAtomicMutation(out string applyBlockedMessage))
                    {
                        eventDetailsStatus = applyBlockedMessage;
                        return;
                    }

                    string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationAutomaticEvents");
                    BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Events.StatusAutoApplying"));
                    IReadOnlyList<ModConfirmEventApplicationEntryDto> automaticApplications;
                    try
                    {
                        automaticApplications = ApplyDirectlyProcessableEvents(events);
                    }
                    catch (Exception ex)
                    {
                        eventDetailsStatus = ClashOfRimText.Key(
                            "ClashOfRim.Events.StatusAutoApplyException",
                            ex.GetType().Name.Named("TYPE"),
                            ex.Message.Named("MESSAGE"));
                        ClearLocalAtomicMutation();
                        Log.Warning("[ClashOfRim][AutoEventApply] automatic application failed: " + ex);
                        return;
                    }

                    if (automaticApplications.Count > 0)
                    {
                        StartConfirmAutomaticEventApplications(automaticApplications);
                    }
                    else
                    {
                        CompleteLocalAtomicMutation();
                    }

                    PostEventLetters(events, reason);
                });
            }
            catch (Exception ex)
            {
                eventQueueStatus = ClashOfRimText.Key(
                    "ClashOfRim.Events.StatusAutoException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Automatic event refresh failed: " + ex);
            }
            finally
            {
                bool runAgain;
                lock (automaticEventRefreshLock)
                {
                    runAgain = automaticEventRefreshQueued;
                    automaticEventRefreshQueued = false;
                    automaticEventRefreshInProgress = false;
                }

                if (runAgain)
                {
                    StartAutomaticEventRefresh(ClashOfRimText.Key("ClashOfRim.Events.ReasonMergedNotifications"));
                }
            }
        });
    }

    private void QueueAutomaticEventRefreshAfterLocalAtomicMutation(string reason, string status)
    {
        lock (automaticEventRefreshLock)
        {
            automaticEventRefreshQueued = true;
        }

        eventQueueStatus = status;
        ClashLog.Message(
            $"[ClashOfRim][AutoEventRefresh] queued after local atomic mutation reason={reason ?? "<null>"}.");
    }

    private void StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation()
    {
        bool runQueued;
        lock (automaticEventRefreshLock)
        {
            runQueued = automaticEventRefreshQueued && !automaticEventRefreshInProgress;
            if (runQueued)
            {
                automaticEventRefreshQueued = false;
            }
        }

        if (runQueued)
        {
            StartAutomaticEventRefresh(ClashOfRimText.Key("ClashOfRim.Events.ReasonMergedNotifications"));
        }
    }

    internal void StartManualPullPendingEvents()
    {
        if (!CanRunManualSync(out string failureReason))
        {
            eventQueueStatus = failureReason;
            return;
        }

        manualSyncInProgress = true;
        eventQueueStatus = ClashOfRimText.Key("ClashOfRim.Events.StatusPullingPending");

        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));

                ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto> result =
                    await client.PullPendingEventsAsync();

                if (!result.Success || result.Response is null)
                {
                    eventQueueStatus = ClashOfRimText.Key(
                        "ClashOfRim.Events.StatusQueueFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? serverResult = result.Response.Result;
                if (serverResult is not null && !serverResult.Accepted)
                {
                    eventQueueStatus = ClashOfRimText.Key(
                        "ClashOfRim.Events.StatusQueueRejected",
                        serverResult.ErrorCode.Named("CODE"),
                        (serverResult.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                eventQueueStatus = FormatEventQueue(result.Response.EventQueue);
                CaptureEventIds(result.Response.EventQueue);
            }
            catch (Exception ex)
            {
                eventQueueStatus = ClashOfRimText.Key(
                    "ClashOfRim.Events.StatusQueueException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Manual event pull failed: " + ex);
            }
            finally
            {
                manualSyncInProgress = false;
            }
        });
    }

    internal void StartManualWaitForEvents()
    {
        if (!CanRunManualSync(out string failureReason))
        {
            eventQueueStatus = failureReason;
            return;
        }

        manualSyncInProgress = true;
        eventQueueStatus = ClashOfRimText.Key("ClashOfRim.Events.StatusWaitingOnline");

        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));

                ClashOfRimClientNetworkResult<ModWaitForEventsResponseDto> result =
                    await client.WaitForEventsAsync(lastNotificationVersion, timeoutSeconds: 15);

                if (!result.Success || result.Response is null)
                {
                    eventQueueStatus = ClashOfRimText.Key(
                        "ClashOfRim.Events.StatusOnlineWaitFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? serverResult = result.Response.Result;
                if (serverResult is not null && !serverResult.Accepted)
                {
                    eventQueueStatus = ClashOfRimText.Key(
                        "ClashOfRim.Events.StatusOnlineWaitRejected",
                        serverResult.ErrorCode.Named("CODE"),
                        (serverResult.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                lastNotificationVersion = result.Response.NotificationVersion;
                eventQueueStatus = result.Response.Changed
                    ? ClashOfRimText.Key("ClashOfRim.Events.StatusOnlineArrived", FormatEventQueue(result.Response.EventQueue).Named("QUEUE"))
                    : ClashOfRimText.Key("ClashOfRim.Events.StatusOnlineTimedOut", FormatEventQueue(result.Response.EventQueue).Named("QUEUE"));
                CaptureEventIds(result.Response.EventQueue);
            }
            catch (Exception ex)
            {
                eventQueueStatus = ClashOfRimText.Key(
                    "ClashOfRim.Events.StatusOnlineException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Online event wait failed: " + ex);
            }
            finally
            {
                manualSyncInProgress = false;
            }
        });
    }

    internal void StartManualPullEventDetails()
    {
        if (!CanRunManualSync(out string failureReason))
        {
            eventDetailsStatus = failureReason;
            return;
        }

        IReadOnlyList<string> eventIds = CopyLastEventQueueIds();
        if (eventIds.Count == 0)
        {
            eventDetailsStatus = ClashOfRimText.Key("ClashOfRim.Events.StatusNoViewableDetails");
            return;
        }

        manualSyncInProgress = true;
        eventDetailsStatus = ClashOfRimText.Key("ClashOfRim.Events.StatusPullingDetails");

        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));

                ClashOfRimClientNetworkResult<ModPullEventDetailsResponseDto> result =
                    await client.PullEventDetailsAsync(eventIds);

                if (!result.Success || result.Response is null)
                {
                    eventDetailsStatus = ClashOfRimText.Key(
                        "ClashOfRim.Events.StatusDetailsFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? serverResult = result.Response.Result;
                if (serverResult is not null && !serverResult.Accepted)
                {
                    eventDetailsStatus = ClashOfRimText.Key(
                        "ClashOfRim.Events.StatusDetailsRejected",
                        serverResult.ErrorCode.Named("CODE"),
                        (serverResult.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                eventDetailsStatus = FormatEventDetails(result.Response.Events);
                CaptureEventDetails(result.Response.Events);
                IReadOnlyCollection<ModEventDetailDto> events = result.Response.Events.ToList();
                ClashOfRimGameComponent.EnqueueMainThreadAction(() => PostEventLetters(events, ClashOfRimText.Key("ClashOfRim.Events.ReasonManualDetails")));
            }
            catch (Exception ex)
            {
                eventDetailsStatus = ClashOfRimText.Key(
                    "ClashOfRim.Events.StatusDetailsException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Manual event detail pull failed: " + ex);
            }
            finally
            {
                manualSyncInProgress = false;
            }
        });
    }

    internal void ProcessFirstGift(GiftClientDecision decision)
    {
        ModEventDetailDto? giftDetail;
        lock (eventStateLock)
        {
            giftDetail = lastEventDetails.FirstOrDefault(ItemDeliveryClientProcessor.IsGiftDetail);
        }

        if (giftDetail is null)
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftProcessing.NoGiftDetail");
            Log.Warning($"[ClashOfRim][GiftProcess] no gift detail in last details count={lastEventDetails.Count}.");
            return;
        }

        ProcessGiftDetail(giftDetail, decision);
    }

    private bool ProcessGiftDetail(ModEventDetailDto giftDetail, GiftClientDecision decision)
    {
        ClashLog.Message(
            $"[ClashOfRim][GiftProcess] selected event={giftDetail.EventId} type={giftDetail.EventType} status={giftDetail.Status} payloadType={giftDetail.PayloadType} payloadLength={giftDetail.PayloadSummary?.Length ?? 0} targetMap={giftDetail.TargetContext?.MapUniqueId ?? "<null>"} landingMode={giftDetail.TargetContext?.LandingMode ?? "<null>"} decision={decision}.");
        ItemDeliveryClientProcessingResult result = ItemDeliveryClientProcessor.Process(
            giftDetail,
            decision,
            settings.UserId,
            settings.ColonyId,
            settings.CurrentSnapshotId,
            rejectionReason: ClashOfRimText.Key("ClashOfRim.GiftProcessing.RejectReason"));
        ClashLog.Message(
            $"[ClashOfRim][GiftProcess] processor result kind={result.Kind} message={result.Message ?? "<null>"} hasPlan={result.LandingPlan is not null} hasReject={result.RejectionRequest is not null}.");

        if (decision != GiftClientDecision.Accept || result.LandingPlan is null)
        {
            giftProcessingStatus = FormatGiftProcessingResult(result);
            return result.RejectionRequest is not null;
        }

        ClashLog.Message(
            $"[ClashOfRim][GiftProcess] landing plan event={result.LandingPlan.EventId} map={result.LandingPlan.TargetMapUniqueId} mode={result.LandingPlan.LandingMode ?? "<null>"} items={result.LandingPlan.Items.Count}.");
        if (!TryHydrateGiftLandingPlanPawnPackages(result.LandingPlan, out string hydrateMessage))
        {
            giftProcessingStatus = hydrateMessage;
            PawnFlowFailurePolicy.LogFailure(
                "manual-gift-hydrate",
                result.LandingPlan.EventId,
                "GiftLanding",
                "PawnPackageHydrateFailed",
                hydrateMessage,
                willReportToServer: false);
            return false;
        }

        GiftLandingApplicationResult application = GiftLandingApplicator.ApplyToCurrentMap(result.LandingPlan);
        if (application.Success && !pendingGiftConfirmationEventIds.Contains(application.EventId, StringComparer.Ordinal))
        {
            pendingGiftConfirmationEventIds.Add(application.EventId);
            StartConfirmGiftApplication(
                application.EventId,
                ItemDeliveryAnchoredClientApplicationResult());
        }
        else if (!application.Success)
        {
            HandlePawnFlowApplicationFailure("manual-gift-landing", application.EventId, sourceEventId: null, application);
        }

        ClashLog.Message(
            $"[ClashOfRim][GiftProcess] landing result event={application.EventId} success={application.Success} kind={application.Kind} mode={application.LandingMode} placedEntries={application.PlacedThingCount} placedStacks={application.PlacedStackCount} pendingConfirm={pendingGiftConfirmationEventIds.Count} message={application.Message}");
        giftProcessingStatus = FormatGiftLandingApplicationResult(application);
        return application.Success;
    }

    private bool TryHydrateGiftLandingPlanPawnPackages(GiftLandingPlan plan, out string message)
    {
        List<GiftItemReference> items = plan.Items
            .Where(item =>
                (item.PawnPackage is null && !string.IsNullOrWhiteSpace(item.PawnPackageId))
                || (item.ThingPackage is null && !string.IsNullOrWhiteSpace(item.ThingPackageId)))
            .ToList();
        if (items.Count == 0)
        {
            message = string.Empty;
            return true;
        }

        try
        {
            using var httpClient = new HttpClient();
            var client = new ClashOfRimModNetworkClient(
                httpClient,
                ClashOfRimClientNetworkContext.FromSettings(settings));
            PawnPackageTransferResult result = PawnPackageTransferService
                .HydrateGiftItemsAsync(client, items)
                .GetAwaiter()
                .GetResult();
            if (!result.Success)
            {
                message = result.Message;
                return false;
            }

            PawnPackageTransferResult thingResult = PawnPackageTransferService
                .HydrateGiftThingStatePackagesAsync(client, items)
                .GetAwaiter()
                .GetResult();
            if (!thingResult.Success)
            {
                message = thingResult.Message;
                return false;
            }

            message = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            message = ClashOfRimText.Key(
                "ClashOfRim.PawnPackage.DownloadException",
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE"));
            Log.Warning("[ClashOfRim][GiftProcess] pawn package hydrate failed: " + ex);
            return false;
        }
    }

    private void HandlePawnFlowApplicationFailure(
        string stage,
        string eventId,
        string? sourceEventId,
        GiftLandingApplicationResult result)
    {
        PawnFlowFailurePolicy.LogApplicationFailure(stage, eventId, result);
        if (PawnFlowFailurePolicy.ShouldReportTerminalFailure(result))
        {
            StartReportEventApplicationFailure(eventId, sourceEventId, result.Message);
        }
    }

    private void HandlePawnFlowApplicationFailure(
        string stage,
        string eventId,
        string? sourceEventId,
        SupportPawnApplicationResult result)
    {
        PawnFlowFailurePolicy.LogApplicationFailure(stage, eventId, result);
        if (PawnFlowFailurePolicy.ShouldReportTerminalFailure(result))
        {
            StartReportEventApplicationFailure(eventId, sourceEventId, result.Message);
        }
    }

    private void StartReportEventApplicationFailure(string eventId, string? sourceEventId, string reason)
    {
        if (string.IsNullOrWhiteSpace(eventId) || !settings.IsConfigured)
        {
            return;
        }

        string currentSnapshotId = settings.CurrentSnapshotId ?? string.Empty;
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModReportEventApplicationFailureResponseDto> result =
                    await client.ReportEventApplicationFailureAsync(
                        $"event-application-failure:{settings.UserId}:{eventId}:{DateTime.UtcNow.Ticks}",
                        eventId,
                        sourceEventId,
                        currentSnapshotId,
                        reason);
                if (!result.Success || result.Response is null)
                {
                    Log.Warning("[ClashOfRim][EventFailure] report failed event=" + eventId + " error=" + result.ErrorCode + " " + result.Message);
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    Log.Warning("[ClashOfRim][EventFailure] server rejected event=" + eventId + " error=" + response.ErrorCode + " " + response.Message);
                    return;
                }

                ClashLog.Message("[ClashOfRim][EventFailure] reported event=" + eventId + " affected=" + result.Response.AffectedEventCount);
                StartAutomaticEventRefresh(ClashOfRimText.Key("ClashOfRim.Events.ReasonFailureReport"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                Log.Warning("[ClashOfRim][EventFailure] report exception event=" + eventId + ": " + ex);
            }
        });
    }

    private IReadOnlyList<ModConfirmEventApplicationEntryDto> ApplyDirectlyProcessableEvents(
        IReadOnlyCollection<ModEventDetailDto> details)
    {
        var applications = new List<ModConfirmEventApplicationEntryDto>();
        foreach (ModEventDetailDto detail in details)
        {
            if (!IsEventInGroup(detail.EventId, "DirectlyProcessable")
                && !IsEventInGroup(detail.EventId, "DeliveredUnconfirmed")
                || !CanAutoApplyDirectlyProcessableEvent(detail))
            {
                continue;
            }

            if (SupportPawnApplicator.IsReturnToSenderDetail(detail))
            {
                SupportPawnApplicationResult returned = SupportPawnApplicator.ApplyToCurrentMap(detail);
                if (!returned.Applied)
                {
                    eventDetailsStatus = ClashOfRimText.Key(
                        "ClashOfRim.Events.StatusAutoApplyFailed",
                        detail.EventId.Named("EVENTID"),
                        (returned.Message ?? string.Empty).Named("MESSAGE"));
                    HandlePawnFlowApplicationFailure("auto-support-return", detail.EventId, sourceEventId: null, returned);

                    continue;
                }

                applications.Add(new ModConfirmEventApplicationEntryDto
                {
                    EventId = detail.EventId,
                    SourceEventId = null,
                    ClientApplicationResult = "SupportPawnReturnCaravanCreated"
                });
                ClashOfRimGameComponent.MarkManualEventHandled(detail.EventId);
                ClashLog.Message($"[ClashOfRim][AutoEventApply] applied support return event={detail.EventId} message={returned.Message}");
                continue;
            }

            if (RaidAttackerLossPayloadReader.TryRead(
                    detail,
                    settings.CurrentSnapshotId,
                    out RaidAttackerLossApplicationRequest? attackerLossRequest,
                    out string attackerLossReadMessage)
                && attackerLossRequest is not null)
            {
                RemoteMapSessionController.CloseRaidBattleForExternalResolution(
                    attackerLossRequest.SourceRaidEventId,
                    "raid attacker loss event applied");
                RaidAttackerLossApplicationResult attackerLossResult = RaidAttackerLossApplicator.Apply(attackerLossRequest);
                if (!attackerLossResult.Applied)
                {
                    eventDetailsStatus = ClashOfRimText.Key(
                        "ClashOfRim.Events.StatusAutoRaidLossFailed",
                        detail.EventId.Named("EVENTID"),
                        (attackerLossResult.FailureReason ?? string.Empty).Named("MESSAGE"));
                    Log.Warning($"[ClashOfRim][AutoEventApply] raid attacker loss failed event={detail.EventId} kind={attackerLossResult.Kind} message={attackerLossResult.FailureReason}");
                    continue;
                }

                applications.Add(new ModConfirmEventApplicationEntryDto
                {
                    EventId = detail.EventId,
                    SourceEventId = attackerLossRequest.SourceRaidEventId,
                    ClientApplicationResult = attackerLossResult.Kind.ToString()
                });
                ClashOfRimGameComponent.MarkManualEventHandled(detail.EventId);
                ClashLog.Message($"[ClashOfRim][AutoEventApply] applied raid attacker loss event={detail.EventId} source={attackerLossRequest.SourceRaidEventId} kind={attackerLossResult.Kind}.");
                continue;
            }

            if (detail.EventType == ServerEventType.Raid
                && !string.IsNullOrWhiteSpace(attackerLossReadMessage)
                && RaidAttackerLossPayloadReader.HasAttackerLoss(detail))
            {
                eventDetailsStatus = ClashOfRimText.Key(
                    "ClashOfRim.Events.StatusAutoRaidLossFailed",
                    detail.EventId.Named("EVENTID"),
                    attackerLossReadMessage.Named("MESSAGE"));
                Log.Warning($"[ClashOfRim][AutoEventApply] raid attacker loss parse failed event={detail.EventId} message={attackerLossReadMessage}");
                continue;
            }

            ItemDeliveryClientProcessingResult prepared = ItemDeliveryClientProcessor.Process(
                detail,
                GiftClientDecision.Accept,
                settings.UserId,
                settings.ColonyId,
                settings.CurrentSnapshotId);
            if (prepared.LandingPlan is null)
            {
                eventDetailsStatus = ClashOfRimText.Key(
                    "ClashOfRim.Events.StatusAutoApplyFailed",
                    detail.EventId.Named("EVENTID"),
                    (prepared.Message ?? string.Empty).Named("MESSAGE"));
                Log.Warning($"[ClashOfRim][AutoEventApply] prepare failed event={detail.EventId} kind={prepared.Kind} message={prepared.Message}");
                continue;
            }

            if (!TryHydrateGiftLandingPlanPawnPackages(prepared.LandingPlan, out string hydrateMessage))
            {
                eventDetailsStatus = hydrateMessage;
                PawnFlowFailurePolicy.LogFailure(
                    "auto-gift-hydrate",
                    detail.EventId,
                    "GiftLanding",
                    "PawnPackageHydrateFailed",
                    hydrateMessage,
                    willReportToServer: false);
                continue;
            }

            GiftLandingApplicationResult applied = GiftLandingApplicator.ApplyToCurrentMap(prepared.LandingPlan);
            if (!applied.Success)
            {
                eventDetailsStatus = ClashOfRimText.Key(
                    "ClashOfRim.Events.StatusAutoApplyFailed",
                    detail.EventId.Named("EVENTID"),
                    (applied.Message ?? string.Empty).Named("MESSAGE"));
                HandlePawnFlowApplicationFailure("auto-gift-landing", detail.EventId, sourceEventId: null, applied);

                continue;
            }

            applications.Add(new ModConfirmEventApplicationEntryDto
            {
                EventId = applied.EventId,
                SourceEventId = null,
                ClientApplicationResult = ItemDeliveryAnchoredClientApplicationResult()
            });

            pendingGiftConfirmationEventIds.RemoveAll(id => string.Equals(id, applied.EventId, StringComparison.Ordinal));
            ClashOfRimGameComponent.MarkManualEventHandled(applied.EventId);
            ClashLog.Message($"[ClashOfRim][AutoEventApply] applied event={applied.EventId} type={detail.EventType} mode={applied.LandingMode}.");
        }

        if (applications.Count > 0)
        {
            eventDetailsStatus = ClashOfRimText.Key(
                "ClashOfRim.Events.StatusAutoAppliedConfirming",
                applications.Count.Named("COUNT"));
            Messages.Message(
                ClashOfRimText.Key("ClashOfRim.AutoOfflineEventsAppliedMessage", applications.Count.Named("COUNT")),
                MessageTypeDefOf.PositiveEvent,
                historical: false);
        }

        return applications;
    }

    private void StartConfirmAutomaticEventApplications(IReadOnlyList<ModConfirmEventApplicationEntryDto> applications)
    {
        if (applications.Count == 0)
        {
            return;
        }

        IReadOnlyList<ModConfirmEventApplicationEntryDto> entries = applications
            .Select(entry => new ModConfirmEventApplicationEntryDto
            {
                EventId = entry.EventId,
                SourceEventId = entry.SourceEventId,
                ClientApplicationResult = entry.ClientApplicationResult
            })
            .ToList();
        StartConfirmEventApplicationsSnapshot(new EventApplicationsSnapshotConfirmationRequest
        {
            Applications = entries,
            Operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationAutomaticEvents"),
            IdempotencyPrefix = "auto-event-confirm",
            UploadingStatus = ClashOfRimText.Key("ClashOfRim.Events.StatusAutoConfirmUploading", entries.Count.Named("COUNT")),
            RetryAction = () => StartConfirmAutomaticEventApplications(entries),
            SetStatus = value => eventDetailsStatus = value,
            BuildSnapshotBuildFailedStatus = buildFailureReason => ClashOfRimText.Key(
                "ClashOfRim.Events.StatusAutoConfirmBuildFailed",
                buildFailureReason.Named("REASON")),
            BuildRequestFailedStatus = result => ClashOfRimText.Key(
                "ClashOfRim.Events.StatusAutoConfirmFailed",
                (result.ErrorCode ?? string.Empty).Named("CODE"),
                (result.Message ?? string.Empty).Named("MESSAGE")),
            BuildRejectedStatus = (serverResult, _) => ClashOfRimText.Key(
                "ClashOfRim.Events.StatusAutoConfirmRejected",
                serverResult.ErrorCode.Named("CODE"),
                (serverResult.Message ?? string.Empty).Named("MESSAGE")),
            BuildExceptionStatus = ex => ClashOfRimText.Key(
                "ClashOfRim.Events.StatusAutoConfirmException",
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE")),
            SelectAppliedSnapshotId = response => response.Applications
                .Where(application => application.Result?.Accepted == true)
                .Select(application => application.AppliedSnapshotId)
                .LastOrDefault(snapshotId => !string.IsNullOrWhiteSpace(snapshotId))
                ?? response.AppliedSnapshotId,
            OnAcceptedOnMainThread = (response, appliedSnapshotId) =>
            {
                int acceptedCount = response.Applications.Count(application => application.Result?.Accepted == true);
                int rejectedCount = response.Applications.Count - acceptedCount;
                List<string> handledEventIds = response.Applications
                    .Where(application => application.Result?.Accepted == true)
                    .Select(application => application.EventId)
                    .Where(eventId => !string.IsNullOrWhiteSpace(eventId))
                    .Select(eventId => eventId!)
                    .ToList();
                foreach (string handledEventId in handledEventIds)
                {
                    ClashOfRimGameComponent.MarkManualEventHandled(handledEventId);
                }

                eventDetailsStatus = ClashOfRimText.Key(
                    "ClashOfRim.Events.StatusAutoConfirmCompleted",
                    acceptedCount.Named("ACCEPTED"),
                    rejectedCount.Named("REJECTED"),
                    (appliedSnapshotId ?? string.Empty).Named("SNAPSHOT"));
                ClashLog.Message($"[ClashOfRim][AutoEventConfirm] completed accepted={acceptedCount} rejected={rejectedCount} appliedSnapshot={appliedSnapshotId ?? "<null>"}.");
            }
        });
    }

    private static bool TryBuildCurrentGameSnapshotPackage(
        string ownerId,
        string colonyId,
        out ModSnapshotPackageBuildResult build,
        out string failureReason)
    {
        build = ModSnapshotPackageBuildResult.Failed("NotStarted", ClashOfRimText.Key("ClashOfRim.SnapshotPackage.BuildNotStarted"));
        if (!ClashOfRimGameComponent.TrySaveCurrentGameToBytes(out byte[] saveBytes, out failureReason))
        {
            return false;
        }

        build = ModSnapshotPackageBuilder.FromSaveBytes(
            saveBytes,
            "memory",
            ownerId,
            colonyId,
            DateTime.UtcNow);
        if (!build.Success || build.Package is null || build.Payload is null)
        {
            failureReason = $"{build.ErrorCode} {build.Message}";
            return false;
        }

        build.Package.DefenderThreatPoints = SnapshotDefenderThreatPointsCapture.TryCapture();

        failureReason = string.Empty;
        return true;
    }

    private void PersistAcceptedSnapshotLineage(string snapshotId, string? nextLineageToken)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return;
        }

        settings.CurrentSnapshotId = snapshotId;
        if (!string.IsNullOrWhiteSpace(nextLineageToken))
        {
            settings.CurrentLineageToken = nextLineageToken!;
            ClashOfRimGameComponent.SetSnapshotLineage(snapshotId, nextLineageToken);
        }

        settings.Write();
    }

    private sealed class EventApplicationSnapshotConfirmationRequest
    {
        public string EventId { get; set; } = string.Empty;

        public string? SourceEventId { get; set; }

        public string? BaseSnapshotId { get; set; }

        public string ClientApplicationResult { get; set; } = string.Empty;

        public string Operation { get; set; } = string.Empty;

        public string IdempotencyPrefix { get; set; } = string.Empty;

        public string UploadingStatus { get; set; } = string.Empty;

        public string BuildFailurePrefix { get; set; } = string.Empty;

        public string FailurePrefix { get; set; } = string.Empty;

        public string RejectedPrefix { get; set; } = string.Empty;

        public string ExceptionPrefix { get; set; } = string.Empty;

        public string EmptyEventMessage { get; set; } = string.Empty;

        public Action RetryAction { get; set; } = static () => { };

        public Action<string>? SetStatus { get; set; }

        public Func<string, string>? BuildSnapshotBuildFailedStatus { get; set; }

        public Func<ClashOfRimClientNetworkResult<ModConfirmEventApplicationResponseDto>, string>? BuildRequestFailedStatus { get; set; }

        public Func<ModProtocolResponseDto, ModConfirmEventApplicationResponseDto, string>? BuildRejectedStatus { get; set; }

        public Func<Exception, string>? BuildExceptionStatus { get; set; }

        public Action<ModConfirmEventApplicationResponseDto> OnAcceptedOnMainThread { get; set; } = static _ => { };
    }

    private sealed class EventApplicationsSnapshotConfirmationRequest
    {
        public IReadOnlyList<ModConfirmEventApplicationEntryDto> Applications { get; set; } = Array.Empty<ModConfirmEventApplicationEntryDto>();

        public string Operation { get; set; } = string.Empty;

        public string IdempotencyPrefix { get; set; } = string.Empty;

        public string UploadingStatus { get; set; } = string.Empty;

        public Action RetryAction { get; set; } = static () => { };

        public Action<string>? SetStatus { get; set; }

        public Func<string, string>? BuildSnapshotBuildFailedStatus { get; set; }

        public Func<ClashOfRimClientNetworkResult<ModConfirmEventApplicationsResponseDto>, string>? BuildRequestFailedStatus { get; set; }

        public Func<ModProtocolResponseDto, ModConfirmEventApplicationsResponseDto, string>? BuildRejectedStatus { get; set; }

        public Func<Exception, string>? BuildExceptionStatus { get; set; }

        public Func<ModConfirmEventApplicationsResponseDto, string?>? SelectAppliedSnapshotId { get; set; }

        public Action<ModConfirmEventApplicationsResponseDto, string?> OnAcceptedOnMainThread { get; set; } = static (_, _) => { };
    }

    private sealed class PreparedIdentityEventApplicationSnapshotConfirmationRequest
    {
        public string Operation { get; set; } = string.Empty;

        public string EventId { get; set; } = string.Empty;

        public string? SourceEventId { get; set; }

        public string UserId { get; set; } = string.Empty;

        public string ColonyId { get; set; } = string.Empty;

        public string BaseSnapshotId { get; set; } = string.Empty;

        public ModSnapshotPackageMetadataDto ConfirmedSnapshot { get; set; } = null!;

        public byte[] ConfirmedPayload { get; set; } = Array.Empty<byte>();

        public string ClientApplicationResult { get; set; } = string.Empty;

        public string IdempotencyKey { get; set; } = string.Empty;

        public TimeSpan? HttpTimeout { get; set; }

        public string UploadingStatus { get; set; } = string.Empty;

        public bool UploadTransactionAlreadyStarted { get; set; }

        public bool CompleteMutationBeforeAcceptedCallback { get; set; }

        public Action<string>? SetStatus { get; set; }

        public Action<Action, bool> ShowFailure { get; set; } = static (_, _) => { };

        public Action RetryAction { get; set; } = static () => { };

        public Func<ClashOfRimClientNetworkResult<ModConfirmEventApplicationResponseDto>, string>? BuildRequestFailedStatus { get; set; }

        public Func<ModProtocolResponseDto, ModConfirmEventApplicationResponseDto, string>? BuildRejectedStatus { get; set; }

        public Func<Exception, string>? BuildExceptionStatus { get; set; }

        public Action<ModConfirmEventApplicationResponseDto> OnAcceptedOnMainThread { get; set; } = static _ => { };
    }

    private void SetEventApplicationSnapshotConfirmationStatus(EventApplicationSnapshotConfirmationRequest request, string message)
    {
        if (request.SetStatus is null)
        {
            giftProcessingStatus = message;
            return;
        }

        request.SetStatus(message);
    }

    private void StartConfirmEventApplicationSnapshot(EventApplicationSnapshotConfirmationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EventId))
        {
            SetEventApplicationSnapshotConfirmationStatus(request, request.EmptyEventMessage);
            return;
        }

        if (TryRejectBlockedByDifferentLocalAtomicMutation(request.Operation, out string blockedMessage))
        {
            SetEventApplicationSnapshotConfirmationStatus(request, blockedMessage);
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            SetEventApplicationSnapshotConfirmationStatus(request, failureReason);
            ShowUnconfirmedSnapshotFailure(
                request.Operation,
                failureReason,
                request.RetryAction);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction())
        {
            string status = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            SetEventApplicationSnapshotConfirmationStatus(request, status);
            Messages.Message(status, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(request.Operation, request.UploadingStatus);
        if (!TryBuildCurrentGameSnapshotPackage(
                settings.UserId,
                settings.ColonyId,
                out ModSnapshotPackageBuildResult build,
                out string buildFailureReason))
        {
            string status = request.BuildSnapshotBuildFailedStatus?.Invoke(buildFailureReason)
                ?? $"{request.BuildFailurePrefix}：{buildFailureReason}";
            SetEventApplicationSnapshotConfirmationStatus(request, status);
            ShowUnconfirmedSnapshotFailure(
                request.Operation,
                status,
                request.RetryAction);
            EndSnapshotUploadTransaction();
            return;
        }

        string eventId = request.EventId;
        string baseSnapshotId = string.IsNullOrWhiteSpace(request.BaseSnapshotId)
            ? settings.CurrentSnapshotId
            : request.BaseSnapshotId!;
        ModSnapshotPackageMetadataDto confirmedSnapshot = build.Package!;
        confirmedSnapshot.SnapshotUploadKind = ModSnapshotUploadKinds.EventApplicationConfirmation;
        byte[] confirmedPayload = build.Payload!;
        SetEventApplicationSnapshotConfirmationStatus(request, request.UploadingStatus);

        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                string idempotencyKey = $"{request.IdempotencyPrefix}:{settings.UserId}:{settings.ColonyId}:{eventId}:{confirmedSnapshot.SnapshotId}";
                ClashOfRimClientNetworkResult<ModConfirmEventApplicationResponseDto> result =
                    await client.ConfirmEventApplicationAsync(
                        idempotencyKey,
                        eventId,
                        request.SourceEventId,
                        baseSnapshotId,
                        confirmedSnapshot,
                        confirmedPayload,
                        request.ClientApplicationResult);

                if (!result.Success || result.Response is null)
                {
                    string status = request.BuildRequestFailedStatus?.Invoke(result)
                        ?? ClashOfRimText.Key(
                            "ClashOfRim.Events.StatusSingleConfirmFailed",
                            request.FailurePrefix.Named("PREFIX"),
                            (result.ErrorCode ?? string.Empty).Named("CODE"),
                            (result.Message ?? string.Empty).Named("MESSAGE"));
                    SetEventApplicationSnapshotConfirmationStatus(request, status);
                    ShowUnconfirmedSnapshotFailure(
                        request.Operation,
                        status,
                        request.RetryAction,
                        allowRetry: IsRetryableNetworkFailure(result));
                    return;
                }

                ModProtocolResponseDto? serverResult = result.Response.Result;
                if (serverResult is not null && !serverResult.Accepted)
                {
                    string status = request.BuildRejectedStatus?.Invoke(serverResult, result.Response)
                        ?? ClashOfRimText.Key(
                            "ClashOfRim.Events.StatusSingleConfirmRejected",
                            request.RejectedPrefix.Named("PREFIX"),
                            serverResult.ErrorCode.Named("CODE"),
                            (serverResult.Message ?? string.Empty).Named("MESSAGE"),
                            (result.Response.ServerValidationResult ?? string.Empty).Named("VALIDATION"));
                    SetEventApplicationSnapshotConfirmationStatus(request, status);
                    ShowUnconfirmedSnapshotFailure(
                        request.Operation,
                        status,
                        request.RetryAction);
                    return;
                }

                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (!string.IsNullOrWhiteSpace(result.Response.AppliedSnapshotId))
                    {
                        PersistAcceptedSnapshotLineage(
                            result.Response.AppliedSnapshotId!,
                            result.Response.NextLineageToken);
                    }

                    request.OnAcceptedOnMainThread(result.Response);
                    CompleteLocalAtomicMutation();
                    CloseUnconfirmedSnapshotFailureWindow();
                });
            }
            catch (Exception ex)
            {
                string status = request.BuildExceptionStatus?.Invoke(ex)
                    ?? ClashOfRimText.Key(
                        "ClashOfRim.Events.StatusSingleConfirmException",
                        request.ExceptionPrefix.Named("PREFIX"),
                        ex.GetType().Name.Named("TYPE"),
                        ex.Message.Named("MESSAGE"));
                SetEventApplicationSnapshotConfirmationStatus(request, status);
                ShowUnconfirmedSnapshotFailure(
                    request.Operation,
                    status,
                    request.RetryAction,
                    allowRetry: IsRetryableNetworkException(ex));
                Log.Warning($"[ClashOfRim] {request.Operation} failed: " + ex);
            }
            finally
            {
                EndSnapshotUploadTransaction();
            }
        });
    }

    private static void SetEventApplicationsSnapshotConfirmationStatus(EventApplicationsSnapshotConfirmationRequest request, string message)
    {
        request.SetStatus?.Invoke(message);
    }

    private void StartConfirmEventApplicationsSnapshot(EventApplicationsSnapshotConfirmationRequest request)
    {
        if (request.Applications.Count == 0)
        {
            return;
        }

        if (TryRejectBlockedByDifferentLocalAtomicMutation(request.Operation, out string blockedMessage))
        {
            SetEventApplicationsSnapshotConfirmationStatus(request, blockedMessage);
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!CanRunEventApplicationSnapshotConfirmation(request.Operation, out string failureReason))
        {
            SetEventApplicationsSnapshotConfirmationStatus(request, failureReason);
            ShowUnconfirmedSnapshotFailure(
                request.Operation,
                failureReason,
                request.RetryAction);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction())
        {
            string status = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            SetEventApplicationsSnapshotConfirmationStatus(request, status);
            Messages.Message(status, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(request.Operation, request.UploadingStatus);
        if (!TryBuildCurrentGameSnapshotPackage(
                settings.UserId,
                settings.ColonyId,
                out ModSnapshotPackageBuildResult build,
                out string buildFailureReason))
        {
            string status = request.BuildSnapshotBuildFailedStatus?.Invoke(buildFailureReason)
                        ?? ClashOfRimText.Key(
                            "ClashOfRim.Events.StatusBatchConfirmBuildFailed",
                            buildFailureReason.Named("REASON"));
            SetEventApplicationsSnapshotConfirmationStatus(request, status);
            Log.Warning($"[ClashOfRim][EventBatchConfirm] build memory snapshot failed applications={request.Applications.Count} reason={buildFailureReason}");
            ShowUnconfirmedSnapshotFailure(
                request.Operation,
                status,
                request.RetryAction);
            EndSnapshotUploadTransaction();
            return;
        }

        string baseSnapshotId = settings.CurrentSnapshotId;
        IReadOnlyList<ModConfirmEventApplicationEntryDto> entries = request.Applications
            .Select(entry => new ModConfirmEventApplicationEntryDto
            {
                EventId = entry.EventId,
                SourceEventId = entry.SourceEventId,
                ClientApplicationResult = entry.ClientApplicationResult
            })
            .ToList();
        ModSnapshotPackageMetadataDto confirmedSnapshot = build.Package!;
        confirmedSnapshot.SnapshotUploadKind = ModSnapshotUploadKinds.BatchEventApplicationConfirmation;
        byte[] confirmedPayload = build.Payload!;
        SetEventApplicationsSnapshotConfirmationStatus(request, request.UploadingStatus);

        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                string eventDigest = string.Join(",", entries.Select(entry => entry.EventId).OrderBy(id => id, StringComparer.Ordinal));
                string idempotencyKey = $"{request.IdempotencyPrefix}:{settings.UserId}:{settings.ColonyId}:{eventDigest}:{confirmedSnapshot.SnapshotId}";
                ClashOfRimClientNetworkResult<ModConfirmEventApplicationsResponseDto> result =
                    await client.ConfirmEventApplicationsAsync(
                        idempotencyKey,
                        baseSnapshotId,
                        confirmedSnapshot,
                        confirmedPayload,
                        entries);

                if (!result.Success || result.Response is null)
                {
                    string status = request.BuildRequestFailedStatus?.Invoke(result)
                        ?? ClashOfRimText.Key(
                            "ClashOfRim.Events.StatusBatchConfirmFailed",
                            (result.ErrorCode ?? string.Empty).Named("CODE"),
                            (result.Message ?? string.Empty).Named("MESSAGE"));
                    SetEventApplicationsSnapshotConfirmationStatus(request, status);
                    ShowUnconfirmedSnapshotFailure(
                        request.Operation,
                        status,
                        request.RetryAction,
                        allowRetry: IsRetryableNetworkFailure(result));
                    return;
                }

                ModProtocolResponseDto? serverResult = result.Response.Result;
                if (serverResult is not null && !serverResult.Accepted)
                {
                    string status = request.BuildRejectedStatus?.Invoke(serverResult, result.Response)
                        ?? ClashOfRimText.Key(
                            "ClashOfRim.Events.StatusBatchConfirmRejected",
                            serverResult.ErrorCode.Named("CODE"),
                            (serverResult.Message ?? string.Empty).Named("MESSAGE"));
                    SetEventApplicationsSnapshotConfirmationStatus(request, status);
                    ShowUnconfirmedSnapshotFailure(
                        request.Operation,
                        status,
                        request.RetryAction);
                    return;
                }

                string? appliedSnapshotId = request.SelectAppliedSnapshotId?.Invoke(result.Response)
                    ?? result.Response.AppliedSnapshotId;
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (!string.IsNullOrWhiteSpace(appliedSnapshotId))
                    {
                        PersistAcceptedSnapshotLineage(appliedSnapshotId!, result.Response.NextLineageToken);
                    }

                    request.OnAcceptedOnMainThread(result.Response, appliedSnapshotId);
                    CompleteLocalAtomicMutation();
                    CloseUnconfirmedSnapshotFailureWindow();
                });
            }
            catch (Exception ex)
            {
                string status = request.BuildExceptionStatus?.Invoke(ex)
                    ?? ClashOfRimText.Key(
                        "ClashOfRim.Events.StatusBatchConfirmException",
                        ex.GetType().Name.Named("TYPE"),
                        ex.Message.Named("MESSAGE"));
                SetEventApplicationsSnapshotConfirmationStatus(request, status);
                ShowUnconfirmedSnapshotFailure(
                    request.Operation,
                    status,
                    request.RetryAction,
                    allowRetry: IsRetryableNetworkException(ex));
                Log.Warning($"[ClashOfRim] {request.Operation} batch event confirmation failed: " + ex);
            }
            finally
            {
                EndSnapshotUploadTransaction();
            }
        });
    }

    private bool CanRunEventApplicationSnapshotConfirmation(string operation, out string failureReason)
    {
        if (!settings.IsConfigured)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Sync.StatusNotConfigured");
            return false;
        }

        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string atomicMessage))
        {
            failureReason = atomicMessage;
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.CurrentSnapshotId))
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Sync.StatusSnapshotMissing");
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static bool IsRetryableNetworkFailure<T>(ClashOfRimClientNetworkResult<T> result)
    {
        string code = result.ErrorCode ?? string.Empty;
        return (string.Equals(code, "HttpError", StringComparison.Ordinal)
                && IsRetryableHttpError(result.Message))
            || string.Equals(code, nameof(HttpRequestException), StringComparison.Ordinal)
            || string.Equals(code, nameof(TaskCanceledException), StringComparison.Ordinal)
            || string.Equals(code, nameof(IOException), StringComparison.Ordinal)
            || string.Equals(code, nameof(ObjectDisposedException), StringComparison.Ordinal);
    }

    private static bool IsRetryableNetworkException(Exception ex)
    {
        return ex is HttpRequestException or TaskCanceledException or IOException or ObjectDisposedException;
    }

    private static bool IsRetryableHttpError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        string trimmed = message!.TrimStart();
        return trimmed.StartsWith("408 ", StringComparison.Ordinal)
            || trimmed.StartsWith("429 ", StringComparison.Ordinal)
            || trimmed.StartsWith("5", StringComparison.Ordinal);
    }

    private static bool IsRetryableSnapshotUploadFailure(ModSnapshotUploadResult result)
    {
        string code = result.ErrorCode ?? string.Empty;
        return (string.Equals(code, "HttpError", StringComparison.Ordinal)
                && IsRetryableHttpError(result.Message))
            || string.Equals(code, nameof(HttpRequestException), StringComparison.Ordinal)
            || string.Equals(code, nameof(TaskCanceledException), StringComparison.Ordinal)
            || string.Equals(code, nameof(IOException), StringComparison.Ordinal)
            || string.Equals(code, nameof(ObjectDisposedException), StringComparison.Ordinal);
    }

    private static void SetPreparedIdentityEventApplicationSnapshotConfirmationStatus(
        PreparedIdentityEventApplicationSnapshotConfirmationRequest request,
        string message)
    {
        request.SetStatus?.Invoke(message);
    }

    private void StartConfirmPreparedIdentityEventApplicationSnapshot(
        PreparedIdentityEventApplicationSnapshotConfirmationRequest request)
    {
        if (request.ConfirmedSnapshot is null || request.ConfirmedPayload.Length == 0)
        {
            return;
        }

        ModSnapshotPackageMetadataDto confirmedSnapshot = request.ConfirmedSnapshot;
        byte[] confirmedPayload = request.ConfirmedPayload;
        string operation = string.IsNullOrWhiteSpace(request.Operation)
            ? ClashOfRimText.Key("ClashOfRim.Events.StatusPreparedConfirmOperation")
            : request.Operation;
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            SetPreparedIdentityEventApplicationSnapshotConfirmationStatus(request, blockedMessage);
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        bool uploadTransactionOwned = request.UploadTransactionAlreadyStarted;
        if (!uploadTransactionOwned && !TryBeginSnapshotUploadTransaction())
        {
            string status = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            SetPreparedIdentityEventApplicationSnapshotConfirmationStatus(request, status);
            Messages.Message(status, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        uploadTransactionOwned = true;

        BeginLocalAtomicMutation(operation, request.UploadingStatus);
        SetPreparedIdentityEventApplicationSnapshotConfirmationStatus(request, request.UploadingStatus);
        ClashLog.Message("[ClashOfRim] Prepared identity event confirmation upload started: operation="
            + operation
            + ", event="
            + request.EventId
            + ", user="
            + request.UserId
            + ", colony="
            + request.ColonyId
            + ", baseSnapshot="
            + request.BaseSnapshotId
            + ", confirmedSnapshot="
            + (confirmedSnapshot.SnapshotId ?? string.Empty)
            + ".");

        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                if (request.HttpTimeout.HasValue)
                {
                    httpClient.Timeout = request.HttpTimeout.Value;
                }

                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModConfirmEventApplicationResponseDto> result =
                    string.Equals(request.UserId, settings.UserId, StringComparison.Ordinal)
                    && string.Equals(request.ColonyId, settings.ColonyId, StringComparison.Ordinal)
                        ? await client.ConfirmEventApplicationAsync(
                            request.IdempotencyKey,
                            request.EventId,
                            request.SourceEventId,
                            request.BaseSnapshotId,
                            confirmedSnapshot,
                            confirmedPayload,
                            request.ClientApplicationResult)
                        : await client.ConfirmEventApplicationForIdentityAsync(
                            request.IdempotencyKey,
                            request.EventId,
                            request.SourceEventId,
                            request.UserId,
                            request.ColonyId,
                            request.BaseSnapshotId,
                            confirmedSnapshot,
                            confirmedPayload,
                            request.ClientApplicationResult);

                if (!result.Success || result.Response is null)
                {
                    string status = request.BuildRequestFailedStatus?.Invoke(result)
                        ?? ClashOfRimText.Key(
                            "ClashOfRim.Events.StatusPreparedConfirmFailed",
                            (result.ErrorCode ?? string.Empty).Named("CODE"),
                            (result.Message ?? string.Empty).Named("MESSAGE"));
                    SetPreparedIdentityEventApplicationSnapshotConfirmationStatus(request, status);
                    request.ShowFailure(request.RetryAction, IsRetryableNetworkFailure(result));
                    Log.Warning("[ClashOfRim] Prepared identity event confirmation request failed: operation="
                        + operation
                        + ", event="
                        + request.EventId
                        + ", code="
                        + (result.ErrorCode ?? string.Empty)
                        + ", message="
                        + (result.Message ?? string.Empty)
                        + ".");
                    return;
                }

                ModProtocolResponseDto? serverResult = result.Response.Result;
                if (serverResult is not null && !serverResult.Accepted)
                {
                    string status = request.BuildRejectedStatus?.Invoke(serverResult, result.Response)
                        ?? ClashOfRimText.Key(
                            "ClashOfRim.Events.StatusPreparedConfirmRejected",
                            serverResult.ErrorCode.Named("CODE"),
                            (serverResult.Message ?? string.Empty).Named("MESSAGE"));
                    SetPreparedIdentityEventApplicationSnapshotConfirmationStatus(request, status);
                    request.ShowFailure(request.RetryAction, false);
                    Log.Warning("[ClashOfRim] Prepared identity event confirmation rejected: operation="
                        + operation
                        + ", event="
                        + request.EventId
                        + ", code="
                        + serverResult.ErrorCode
                        + ", message="
                        + (serverResult.Message ?? string.Empty)
                        + ", validation="
                        + (result.Response.ServerValidationResult ?? string.Empty)
                        + ".");
                    return;
                }

                bool confirmationTargetsCurrentColony =
                    string.Equals(request.UserId, settings.UserId, StringComparison.Ordinal)
                    && string.Equals(request.ColonyId, settings.ColonyId, StringComparison.Ordinal);

                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (confirmationTargetsCurrentColony
                        && !string.IsNullOrWhiteSpace(result.Response.AppliedSnapshotId))
                    {
                        PersistAcceptedSnapshotLineage(
                            result.Response.AppliedSnapshotId!,
                            result.Response.NextLineageToken);
                    }

                    if (request.CompleteMutationBeforeAcceptedCallback)
                    {
                        CompleteLocalAtomicMutation();
                    }

                    request.OnAcceptedOnMainThread(result.Response);
                    if (!request.CompleteMutationBeforeAcceptedCallback)
                    {
                        CompleteLocalAtomicMutation();
                    }

                    CloseUnconfirmedSnapshotFailureWindow();
                });
            }
            catch (Exception ex)
            {
                string status = request.BuildExceptionStatus?.Invoke(ex)
                    ?? ClashOfRimText.Key(
                        "ClashOfRim.Events.StatusPreparedConfirmException",
                        ex.GetType().Name.Named("TYPE"),
                        ex.Message.Named("MESSAGE"));
                SetPreparedIdentityEventApplicationSnapshotConfirmationStatus(request, status);
                request.ShowFailure(request.RetryAction, IsRetryableNetworkException(ex));
                Log.Warning("[ClashOfRim] Prepared identity event confirmation failed: " + ex);
            }
            finally
            {
                if (uploadTransactionOwned)
                {
                    EndSnapshotUploadTransaction();
                }
            }
        });
    }

    private sealed class RaidAttackPawnLoad
    {
        public RaidAttackPawnLoad(string globalKey, Pawn pawn)
        {
            GlobalKey = globalKey;
            Pawn = pawn;
        }

        public string GlobalKey { get; }

        public Pawn Pawn { get; }
    }

    internal void StartManualRejectFirstGift()
    {
        if (!CanRunManualSync(out string failureReason))
        {
            giftProcessingStatus = failureReason;
            return;
        }

        ModEventDetailDto? giftDetail;
        lock (eventStateLock)
        {
            giftDetail = lastEventDetails.FirstOrDefault(ItemDeliveryClientProcessor.IsGiftDetail);
        }

        if (giftDetail is null)
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftProcessing.NoGiftDetail");
            return;
        }

        StartRejectGift(giftDetail, ClashOfRimText.Key("ClashOfRim.GiftProcessing.RejectReason"));
    }

    private bool StartRejectGift(ModEventDetailDto giftDetail, string reason)
    {
        if (!CanRunManualSync(out string failureReason))
        {
            giftProcessingStatus = failureReason;
            return false;
        }

        ItemDeliveryClientProcessingResult prepared = ItemDeliveryClientProcessor.Process(
            giftDetail,
            GiftClientDecision.Reject,
            settings.UserId,
            settings.ColonyId,
            settings.CurrentSnapshotId,
            rejectionReason: reason);

        if (prepared.RejectionRequest is null)
        {
            giftProcessingStatus = FormatGiftProcessingResult(prepared);
            return false;
        }

        manualSyncInProgress = true;
        giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftProcessing.RejectSubmitting");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModRejectGiftResponseDto> result =
                    await client.RejectGiftAsync(prepared.RejectionRequest.EventId, prepared.RejectionRequest.Reason);

                if (!result.Success || result.Response is null)
                {
                    giftProcessingStatus = ClashOfRimText.Key(
                        "ClashOfRim.GiftProcessing.RejectFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? serverResult = result.Response.Result;
                if (serverResult is not null && !serverResult.Accepted)
                {
                    giftProcessingStatus = ClashOfRimText.Key(
                        "ClashOfRim.GiftProcessing.RejectRejected",
                        serverResult.ErrorCode.Named("CODE"),
                        (serverResult.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                giftProcessingStatus = ClashOfRimText.Key(
                    "ClashOfRim.GiftProcessing.Rejected",
                    (result.Response.ReturnEventId ?? string.Empty).Named("RETURNEVENT"),
                    result.Response.ReturnEventCreated.Named("CREATED"));
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    ClashOfRimGameComponent.MarkManualEventHandled(giftDetail.EventId));
            }
            catch (Exception ex)
            {
                giftProcessingStatus = ClashOfRimText.Key(
                    "ClashOfRim.GiftProcessing.RejectException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Gift rejection failed: " + ex);
            }
            finally
            {
                manualSyncInProgress = false;
            }
        });

        return true;
    }

    private bool StartRejectSupportPawn(ModEventDetailDto supportDetail, string reason)
    {
        if (!CanRunManualSync(out string failureReason))
        {
            giftProcessingStatus = failureReason;
            return false;
        }

        manualSyncInProgress = true;
        giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.Support.RejectSubmitting", supportDetail.EventId.Named("EVENTID"));
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModRejectSupportPawnResponseDto> result =
                    await client.RejectSupportPawnAsync(supportDetail.EventId, reason);

                if (!result.Success || result.Response is null)
                {
                    giftProcessingStatus = ClashOfRimText.Key(
                        "ClashOfRim.Support.RejectFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    giftProcessingStatus = ClashOfRimText.Key(
                        "ClashOfRim.Support.RejectRejected",
                        response.ErrorCode.Named("CODE"),
                        (response.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                giftProcessingStatus = ClashOfRimText.Key(
                    "ClashOfRim.Support.Rejected",
                    (result.Response.ReturnEventId ?? string.Empty).Named("RETURNEVENT"),
                    result.Response.ReturnEventCreated.Named("CREATED"));
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    ClashOfRimGameComponent.MarkManualEventHandled(supportDetail.EventId);
                    Messages.Message(
                        giftProcessingStatus,
                        MessageTypeDefOf.NeutralEvent,
                        historical: false);
                });
            }
            catch (Exception ex)
            {
                giftProcessingStatus = ClashOfRimText.Key(
                    "ClashOfRim.Support.RejectException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Support pawn rejection failed: " + ex);
            }
            finally
            {
                manualSyncInProgress = false;
            }
        });

        return true;
    }

    internal void StartManualConfirmFirstGift()
    {
        if (pendingGiftConfirmationEventIds.Count == 0)
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftProcessing.NoPendingConfirmation");
            return;
        }

        StartConfirmGiftApplication(pendingGiftConfirmationEventIds[0], "ItemDeliveryAnchored");
    }

    private static string ItemDeliveryAnchoredClientApplicationResult()
    {
        return "ItemDeliveryAnchored";
    }

    private void StartConfirmGiftApplication(string eventId, string clientApplicationResult)
    {
        StartConfirmEventApplicationSnapshot(new EventApplicationSnapshotConfirmationRequest
        {
            EventId = eventId,
            ClientApplicationResult = clientApplicationResult,
            Operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationGift"),
            IdempotencyPrefix = "gift-confirm",
            UploadingStatus = ClashOfRimText.Key("ClashOfRim.GiftProcessing.ConfirmUploading", eventId.Named("EVENTID")),
            BuildFailurePrefix = ClashOfRimText.Key("ClashOfRim.GiftProcessing.ConfirmBuildFailed"),
            FailurePrefix = ClashOfRimText.Key("ClashOfRim.GiftProcessing.ConfirmFailed"),
            RejectedPrefix = ClashOfRimText.Key("ClashOfRim.GiftProcessing.ConfirmRejected"),
            ExceptionPrefix = ClashOfRimText.Key("ClashOfRim.GiftProcessing.ConfirmException"),
            EmptyEventMessage = ClashOfRimText.Key("ClashOfRim.GiftProcessing.ConfirmEmpty"),
            RetryAction = () => StartConfirmGiftApplication(eventId, clientApplicationResult),
            OnAcceptedOnMainThread = response =>
            {
                pendingGiftConfirmationEventIds.Remove(eventId);
                ClashOfRimGameComponent.MarkManualEventHandled(eventId);
                giftProcessingStatus = ClashOfRimText.Key(
                    "ClashOfRim.GiftProcessing.ConfirmSucceeded",
                    eventId.Named("EVENTID"),
                    response.AppliedSnapshotId.Named("SNAPSHOT"),
                    pendingGiftConfirmationEventIds.Count.Named("PENDING"));
                Messages.Message(giftProcessingStatus, MessageTypeDefOf.PositiveEvent, historical: false);
            }
        });
    }

    private sealed class LocalMutationSnapshotConfirmationRequest
    {
        public string Operation { get; set; } = string.Empty;

        public string UploadingStatus { get; set; } = string.Empty;

        public bool RemoveRaidBattleSessions { get; set; }

        public bool BestEffort { get; set; }

        public Action RetryAction { get; set; } = static () => { };

        public Action<string>? SetStatus { get; set; }

        public Func<ModSnapshotUploadResult, string>? BuildFailureStatus { get; set; }

        public Func<ModSnapshotUploadResult, string>? BuildSuccessStatus { get; set; }

        public Func<Exception, string>? BuildExceptionStatus { get; set; }

        public Action<ModSnapshotUploadResult>? OnSuccessOnMainThread { get; set; }
    }

    private void SetLocalMutationSnapshotConfirmationStatus(LocalMutationSnapshotConfirmationRequest request, string message)
    {
        if (request.SetStatus is null)
        {
            snapshotUploadStatus = message;
            return;
        }

        request.SetStatus(message);
    }

    private void StartConfirmLocalMutationSnapshot(LocalMutationSnapshotConfirmationRequest request)
    {
        if (TryRejectBlockedByDifferentLocalAtomicMutation(request.Operation, out string blockedMessage))
        {
            SetLocalMutationSnapshotConfirmationStatus(request, blockedMessage);
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction())
        {
            string status = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            SetLocalMutationSnapshotConfirmationStatus(request, status);
            Messages.Message(status, request.BestEffort ? MessageTypeDefOf.NeutralEvent : MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(request.Operation, request.UploadingStatus);
        SetLocalMutationSnapshotConfirmationStatus(request, request.UploadingStatus);
        Task.Run(async () =>
        {
            try
            {
                var service = new ModSnapshotUploadService(settings);
                ModSnapshotUploadResult result = await service.UploadConfiguredSnapshotAsync(
                    request.RemoveRaidBattleSessions,
                    snapshotUploadKind: ModSnapshotUploadKinds.EventApplicationConfirmation);
                if (!result.Success)
                {
                    string status = request.BuildFailureStatus?.Invoke(result)
                        ?? ClashOfRimText.Key(
                            "ClashOfRim.LocalMutation.ConfirmFailed",
                            request.UploadingStatus.Named("STATUS"),
                            (result.ErrorCode ?? string.Empty).Named("CODE"),
                            (result.Message ?? string.Empty).Named("MESSAGE"));
                    SetLocalMutationSnapshotConfirmationStatus(request, status);
                    if (request.BestEffort)
                    {
                        Log.Warning("[ClashOfRim] Best-effort local mutation snapshot upload failed: operation="
                            + request.Operation
                            + ", code="
                            + (result.ErrorCode ?? string.Empty)
                            + ", message="
                            + (result.Message ?? string.Empty)
                            + ".");
                        ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                        {
                            Messages.Message(status, MessageTypeDefOf.NeutralEvent, historical: false);
                            CompleteLocalAtomicMutation();
                        });
                        return;
                    }

                    ShowUnconfirmedSnapshotFailure(
                        request.Operation,
                        status,
                        request.RetryAction,
                        allowRetry: IsRetryableSnapshotUploadFailure(result));
                    return;
                }

                string successStatus = request.BuildSuccessStatus?.Invoke(result)
                    ?? ClashOfRimText.Key(
                        "ClashOfRim.LocalMutation.ConfirmSucceeded",
                        (result.AcceptedSnapshotId ?? settings.CurrentSnapshotId).Named("SNAPSHOT"));
                SetLocalMutationSnapshotConfirmationStatus(request, successStatus);
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    CloseUnconfirmedSnapshotFailureWindow();
                    Messages.Message(successStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                    CompleteLocalAtomicMutation();
                    request.OnSuccessOnMainThread?.Invoke(result);
                });
            }
            catch (Exception ex)
            {
                string status = request.BuildExceptionStatus?.Invoke(ex)
                    ?? ClashOfRimText.Key(
                        "ClashOfRim.LocalMutation.ConfirmException",
                        request.UploadingStatus.Named("STATUS"),
                        ex.GetType().Name.Named("TYPE"),
                        ex.Message.Named("MESSAGE"));
                SetLocalMutationSnapshotConfirmationStatus(request, status);
                if (request.BestEffort)
                {
                    Log.Warning($"[ClashOfRim] {request.Operation} best-effort local mutation snapshot confirmation failed: " + ex);
                    ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    {
                        Messages.Message(status, MessageTypeDefOf.NeutralEvent, historical: false);
                        CompleteLocalAtomicMutation();
                    });
                    return;
                }

                ShowUnconfirmedSnapshotFailure(
                    request.Operation,
                    status,
                    request.RetryAction,
                    allowRetry: IsRetryableNetworkException(ex));
                Log.Warning($"[ClashOfRim] {request.Operation} local mutation snapshot confirmation failed: " + ex);
            }
            finally
            {
                EndSnapshotUploadTransaction();
            }
        });
    }

    private void StartConfirmLocalMutationSnapshot(string operation, string uploadingMessage, string successMessage)
    {
        StartConfirmLocalMutationSnapshot(new LocalMutationSnapshotConfirmationRequest
        {
            Operation = operation,
            UploadingStatus = uploadingMessage,
            RetryAction = () => StartConfirmLocalMutationSnapshot(operation, uploadingMessage, successMessage),
            BuildSuccessStatus = result => ClashOfRimText.Key(
                "ClashOfRim.LocalMutation.ConfirmSucceededWithMessage",
                successMessage.Named("MESSAGE"),
                (result.AcceptedSnapshotId ?? settings.CurrentSnapshotId).Named("SNAPSHOT")),
            OnSuccessOnMainThread = _ => StartRegisterPlayerColonySites()
        });
    }

    internal void StartManualSnapshotUpload()
    {
        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            snapshotUploadStatus = atomicMessage;
            Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction())
        {
            snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Uploading");

        Task.Run(async () =>
        {
            try
            {
                var service = new ModSnapshotUploadService(settings);
                ModSnapshotUploadResult result = await service.UploadConfiguredSnapshotAsync(
                    snapshotUploadKind: ModSnapshotUploadKinds.ManualUpload);
                if (result.Success)
                {
                    snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Succeeded", (result.AcceptedSnapshotId ?? string.Empty).Named("SNAPSHOT"));
                    ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    {
                        Messages.Message(snapshotUploadStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                        RequestWorldConfigurationExtensionSync(ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Succeeded", (result.AcceptedSnapshotId ?? string.Empty).Named("SNAPSHOT")));
                    });
                }
                else
                {
                    snapshotUploadStatus = ClashOfRimText.Key(
                        "ClashOfRim.SnapshotUpload.Failed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    NotifyPlayerMessage(snapshotUploadStatus, MessageTypeDefOf.RejectInput);
                }
            }
            catch (Exception ex)
            {
                snapshotUploadStatus = ClashOfRimText.Key(
                    "ClashOfRim.SnapshotUpload.Exception",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Snapshot upload failed: " + ex);
                NotifyPlayerMessage(snapshotUploadStatus, MessageTypeDefOf.RejectInput);
            }
            finally
            {
                EndSnapshotUploadTransaction();
            }
        });
    }

    internal void StartVanillaMenuSnapshotUpload(Action? onSuccess = null)
    {
        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            snapshotUploadStatus = atomicMessage;
            Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!CanUseVanillaMenuSnapshotUpload)
        {
            snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.Menu.UploadSnapshotNoSession");
            Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction())
        {
            snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.Menu.UploadSnapshotBusy");
            Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.Menu.UploadSnapshotUploading");

        Task.Run(async () =>
        {
            try
            {
                var service = new ModSnapshotUploadService(settings);
                ModSnapshotUploadResult result = await service.UploadConfiguredSnapshotAsync(
                    snapshotUploadKind: ModSnapshotUploadKinds.ManualUpload);
                if (!result.Success)
                {
                    snapshotUploadStatus = ClashOfRimText.Key(
                        "ClashOfRim.Menu.UploadSnapshotFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                        Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false));
                    return;
                }

                snapshotUploadStatus = ClashOfRimText.Key(
                    "ClashOfRim.Menu.UploadSnapshotSucceeded",
                    (result.AcceptedSnapshotId ?? settings.CurrentSnapshotId).Named("SNAPSHOT"));
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    Messages.Message(snapshotUploadStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                    StartRegisterPlayerColonySites();
                    StartSyncWorldConfigurationExtensions();
                    if (onSuccess is not null)
                    {
                        DisconnectMultiplayerSessionForExit();
                    }

                    onSuccess?.Invoke();
                });
            }
            catch (Exception ex)
            {
                snapshotUploadStatus = ClashOfRimText.Key(
                    "ClashOfRim.Menu.UploadSnapshotException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Vanilla menu snapshot upload failed: " + ex);
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false));
            }
            finally
            {
                EndSnapshotUploadTransaction();
            }
        });
    }

    internal void StartAutosaveSnapshotUpload()
    {
        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            snapshotUploadStatus = ClashOfRimText.Key(
                "ClashOfRim.AutosaveSnapshot.SkippedAtomic",
                atomicMessage.Named("REASON"));
            ClashLog.Message("[ClashOfRim] Autosave snapshot upload skipped: " + atomicMessage);
            return;
        }

        if (!CanUseVanillaMenuSnapshotUpload)
        {
            snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.AutosaveSnapshot.SkippedNoSession");
            ClashLog.Message("[ClashOfRim] Autosave snapshot upload skipped: no tracked multiplayer snapshot.");
            return;
        }

        if (!TryBeginSnapshotUploadTransaction())
        {
            snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.AutosaveSnapshot.SkippedBusy");
            ClashLog.Message("[ClashOfRim] Autosave snapshot upload skipped: sync already running.");
            return;
        }

        snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.AutosaveSnapshot.Uploading");

        Task.Run(async () =>
        {
            try
            {
                var service = new ModSnapshotUploadService(settings);
                ModSnapshotUploadResult result = await service.UploadConfiguredSnapshotAsync(
                    snapshotUploadKind: ModSnapshotUploadKinds.AutoSave);
                if (!result.Success)
                {
                    snapshotUploadStatus = ClashOfRimText.Key(
                        "ClashOfRim.AutosaveSnapshot.Failed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    Log.Warning("[ClashOfRim] Autosave snapshot upload failed: " + snapshotUploadStatus);
                    NotifyPlayerMessage(snapshotUploadStatus, MessageTypeDefOf.RejectInput);
                    return;
                }

                snapshotUploadStatus = ClashOfRimText.Key(
                    "ClashOfRim.AutosaveSnapshot.Succeeded",
                    (result.AcceptedSnapshotId ?? settings.CurrentSnapshotId).Named("SNAPSHOT"));
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    Messages.Message(snapshotUploadStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                    StartRegisterPlayerColonySites();
                    StartSyncWorldConfigurationExtensions();
                });
            }
            catch (Exception ex)
            {
                snapshotUploadStatus = ClashOfRimText.Key(
                    "ClashOfRim.AutosaveSnapshot.Exception",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Autosave snapshot upload exception: " + ex);
                NotifyPlayerMessage(snapshotUploadStatus, MessageTypeDefOf.RejectInput);
            }
            finally
            {
                EndSnapshotUploadTransaction();
            }
        });
    }

}
