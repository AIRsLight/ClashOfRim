using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using static AIRsLight.ClashOfRim.EventLetters.ClientEventLetterPolicy;
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
        using var publication = ClientSessionWork.EnterPublication();
        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            QueueAutomaticEventRefreshAfterLocalAtomicMutation(reason, atomicMessage);
            return;
        }
        if (!CanRunManualSync(out string failureReason))
        {
            eventQueueStatus = failureReason;
            return;
        }
        var attempt = automaticEventRefresh.Begin();
        if (attempt is null)
        {
            eventQueueStatus = ClashOfRimText.Key("ClashOfRim.Events.StatusAutoMerged", reason.Named("REASON"));
            return;
        }
        eventQueueStatus = ClashOfRimText.Key("ClashOfRim.Events.StatusAutoRefreshing", reason.Named("REASON"));
        var request = eventReader.Capture(ClientEventReadKind.Automatic, reason);
        ClientSessionWork.Run(async () =>
        {
            try { await eventReader.ReadAsync(request); }
            finally
            {
                // Completion and the optional restart share the same identity publication boundary.
                eventReader.TryApply(request, () =>
                {
                    if (automaticEventRefresh.Complete(attempt))
                        StartAutomaticEventRefresh(ClashOfRimText.Key("ClashOfRim.Events.ReasonMergedNotifications"));
                });
            }
        });
    }

    private void QueueAutomaticEventRefreshAfterLocalAtomicMutation(string reason, string status)
    {
        automaticEventRefresh.Defer();
        eventQueueStatus = status;
        ClashLog.Message($"[ClashOfRim][AutoEventRefresh] queued after local atomic mutation reason={reason ?? "<null>"}.");
    }

    private void StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation()
    {
        using var publication = ClientSessionWork.EnterPublication();
        if (automaticEventRefresh.TakeDeferred())
            StartAutomaticEventRefresh(ClashOfRimText.Key("ClashOfRim.Events.ReasonMergedNotifications"));
    }

    internal void StartManualPullPendingEvents() => StartManualEventRead(ClientEventReadKind.Queue);
    internal void StartManualWaitForEvents() => StartManualEventRead(ClientEventReadKind.Wait);
    internal void StartManualPullEventDetails() => StartManualEventRead(ClientEventReadKind.Details);

    private void StartManualEventRead(ClientEventReadKind kind)
    {
        using var publication = ClientSessionWork.EnterPublication();
        if (!CanRunManualSync(out string failureReason)
            || !TryBeginManualSynchronization(out var operation, out failureReason))
        {
            SetEventReadStatus(kind == ClientEventReadKind.Details, failureReason);
            return;
        }
        SetEventReadStatus(kind == ClientEventReadKind.Details, ClientEventReadPresentation.Started(kind));
        string reason = kind == ClientEventReadKind.Details
            ? ClashOfRimText.Key("ClashOfRim.Events.ReasonManualDetails") : "manual-refresh";
        var request = eventReader.Capture(kind, reason, lastNotificationVersion);
        ClientSessionWork.Run(async () =>
        {
            try { await eventReader.ReadAsync(request); }
            finally { operation.Dispose(); }
        });
    }

    private void SetEventReadStatus(bool details, string status)
    {
        if (details) eventDetailsStatus = status;
        else eventQueueStatus = status;
    }

    // Presentation and engine effects remain host adapters until M4c. The module has
    // already checked identity ownership and published inbox state before this notice.
    private void PresentEventRead(ClientEventReadUpdate update)
    {
        var request = update.Request;
        if (update.Failure is { } failure)
        {
            bool detailsStatus = failure.Kind == ClientEventReadFailureKind.Exception
                ? request.Kind == ClientEventReadKind.Details : update.Stage == ClientEventReadStage.Details;
            SetEventReadStatus(detailsStatus, ClientEventReadPresentation.Failed(update));
            if (failure.Exception is not null) Log.Warning("[ClashOfRim] Event read failed: " + failure.Exception);
            return;
        }
        if (update.Stage == ClientEventReadStage.Queue)
        {
            string queue = FormatEventQueue(update.Queue);
            if (request.Kind == ClientEventReadKind.Wait)
            {
                lastNotificationVersion = Math.Max(lastNotificationVersion, update.NotificationVersion);
                eventQueueStatus = ClashOfRimText.Key(update.Changed ? "ClashOfRim.Events.StatusOnlineArrived"
                    : "ClashOfRim.Events.StatusOnlineTimedOut", queue.Named("QUEUE"));
            }
            else eventQueueStatus = queue;
            return;
        }
        if (update.Empty)
        {
            eventDetailsStatus = ClientEventReadPresentation.Empty(request.Kind);
            return;
        }
        var events = update.Details!;
        eventDetailsStatus = FormatEventDetails(events);
        ClientEventReadPresentation.LogGiftDetails(request.Kind == ClientEventReadKind.Automatic ? "AutoEventDetails" : "RefreshDetails", events);
        ClashOfRimGameComponent.EnqueueMainThreadAction(() => eventReader.TryApply(request, () =>
        {
            if (request.Kind == ClientEventReadKind.Automatic) ApplyAutomaticEventRead(events, request.Reason);
            else PostEventLetters(events, request.Reason);
        }));
    }

    private void ApplyAutomaticEventRead(IReadOnlyCollection<ModEventDetailDto> events, string reason)
    {
        if (TryRejectBlockedByLocalAtomicMutation(out string applyBlockedMessage))
        {
            // A mutation can begin after the HTTP read. Retain a deferred refresh so
            // this notification does not depend on another server event to be applied.
            QueueAutomaticEventRefreshAfterLocalAtomicMutation(reason, applyBlockedMessage);
            eventDetailsStatus = applyBlockedMessage;
            return;
        }
        BeginLocalAtomicMutation(LocalMutationKind.AutomaticEvents, ClashOfRimText.Key("ClashOfRim.Events.StatusAutoApplying"));
        IReadOnlyList<ModConfirmEventApplicationEntryDto> automaticApplications;
        try { automaticApplications = ApplyDirectlyProcessableEvents(events); }
        catch (Exception ex) when (ClientSessionWork.IsCurrent)
        {
            eventDetailsStatus = ClashOfRimText.Key("ClashOfRim.Events.StatusAutoApplyException",
                ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
            ClearLocalAtomicMutation();
            Log.Warning("[ClashOfRim][AutoEventApply] automatic application failed: " + ex);
            return;
        }
        if (automaticApplications.Count > 0) StartConfirmAutomaticEventApplications(automaticApplications);
        else CompleteLocalAtomicMutation();
        PostEventLetters(events, reason);
    }

    internal void ProcessFirstGift(GiftClientDecision decision)
    {
        IReadOnlyList<ModEventDetailDto> details = eventInbox.CopyDetails();
        ModEventDetailDto? giftDetail = details.FirstOrDefault(ItemDeliveryClientProcessor.IsGiftDetail);

        if (giftDetail is null)
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftProcessing.NoGiftDetail");
            Log.Warning($"[ClashOfRim][GiftProcess] no gift detail in last details count={details.Count}.");
            return;
        }

        ProcessGiftDetail(giftDetail, decision);
    }

    private bool ProcessGiftDetail(ModEventDetailDto giftDetail, GiftClientDecision decision)
    {
        if (IsAnyManualOrSnapshotSyncInProgress()
            || eventInbox.HasPendingConfirmation(giftDetail.EventId)
            || !CanRunEventLetterAction(giftDetail,
                decision == GiftClientDecision.Accept ? ClashOfRimEventLetterActionKind.Accept : ClashOfRimEventLetterActionKind.Reject,
                ResolveEventReferenceGroup(giftDetail.EventId)))
        {
            return false;
        }

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
        var hydration = giftPackageHydration.HydrateAsync(result.LandingPlan, sessionLifecycle.ReadContext(), ClientSessionWork.Token)
            .GetAwaiter().GetResult();
        if (!hydration.Success)
        {
            string hydrateMessage = hydration.Message;
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
        if (application.Success && eventInbox.TryTrackPendingConfirmation(application.EventId))
        {
            StartConfirmGiftApplication(
                application.EventId,
                ItemDeliveryAnchoredClientApplicationResult());
        }
        else if (!application.Success)
        {
            HandlePawnFlowApplicationFailure("manual-gift-landing", application.EventId, sourceEventId: null, application);
        }

        ClashLog.Message(
            $"[ClashOfRim][GiftProcess] landing result event={application.EventId} success={application.Success} kind={application.Kind} mode={application.LandingMode} placedEntries={application.PlacedThingCount} placedStacks={application.PlacedStackCount} pendingConfirm={eventInbox.PendingConfirmationCount} message={application.Message}");
        giftProcessingStatus = FormatGiftLandingApplicationResult(application);
        return application.Success;
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
        => _ = eventDecisions.ReportFailureAsync(eventId, sourceEventId, reason);

    private IReadOnlyList<ModConfirmEventApplicationEntryDto> ApplyDirectlyProcessableEvents(
        IReadOnlyCollection<ModEventDetailDto> details)
    {
        var applications = new List<ModConfirmEventApplicationEntryDto>();
        foreach (ModEventDetailDto detail in details)
        {
            if (!eventInbox.IsInGroup(detail.EventId, "DirectlyProcessable")
                && !eventInbox.IsInGroup(detail.EventId, "DeliveredUnconfirmed")
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

            var hydration = giftPackageHydration.HydrateAsync(prepared.LandingPlan, sessionLifecycle.ReadContext(), ClientSessionWork.Token)
                .GetAwaiter().GetResult();
            if (!hydration.Success)
            {
                string hydrateMessage = hydration.Message;
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

            eventInbox.ForgetPendingConfirmation(applied.EventId);
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
        => _ = eventConfirmations.AutomaticAsync(applications);

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

        IReadOnlyList<ModEventDetailDto> details = eventInbox.CopyDetails();
        ModEventDetailDto? giftDetail = details.FirstOrDefault(ItemDeliveryClientProcessor.IsGiftDetail);

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

        return eventDecisions.RejectGift(prepared.RejectionRequest.EventId, prepared.RejectionRequest.Reason, out _);
    }

    private bool StartRejectSupportPawn(ModEventDetailDto supportDetail, string reason)
        => eventDecisions.RejectSupport(supportDetail.EventId, reason, out _);

    internal void StartManualConfirmFirstGift()
    {
        string? eventId = eventInbox.FirstPendingConfirmation;
        if (eventId is null)
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftProcessing.NoPendingConfirmation");
            return;
        }

        StartConfirmGiftApplication(eventId, "ItemDeliveryAnchored");
    }

    private static string ItemDeliveryAnchoredClientApplicationResult()
    {
        return "ItemDeliveryAnchored";
    }

    private void StartConfirmGiftApplication(string eventId, string clientApplicationResult)
        => _ = eventConfirmations.GiftAsync(eventId, clientApplicationResult);

    private sealed class LocalMutationSnapshotConfirmationRequest
    {
        public LocalMutationKind Operation { get; set; }

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
        if (TryRejectBlockedByLocalAtomicMutation(out string blockedMessage, request.Operation))
        {
            SetLocalMutationSnapshotConfirmationStatus(request, blockedMessage);
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction(out var uploadTransaction))
        {
            string status = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            SetLocalMutationSnapshotConfirmationStatus(request, status);
            Messages.Message(status, request.BestEffort ? MessageTypeDefOf.NeutralEvent : MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(request.Operation, request.UploadingStatus);
        SetLocalMutationSnapshotConfirmationStatus(request, request.UploadingStatus);
        ClientSessionWork.Run(async () =>
        {
            try
            {
                var service = new ModSnapshotUploadService(settings, uploadTransaction);
                ModSnapshotUploadResult result = await ClientSessionWork.Observe(service.UploadConfiguredSnapshotAsync(
                    request.RemoveRaidBattleSessions,
                    snapshotUploadKind: ModSnapshotUploadKinds.EventApplicationConfirmation));
                using var publication = ClientSessionWork.EnterPublication();
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
                        allowRetry: ClientNetworkFailurePolicy.CanRetry(result.ErrorCode, result.Message));
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
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
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
                    allowRetry: ClientNetworkFailurePolicy.CanRetry(ex));
                Log.Warning($"[ClashOfRim] {request.Operation} local mutation snapshot confirmation failed: " + ex);
            }
            finally
            {
                uploadTransaction.Dispose();
            }
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

        if (!TryBeginSnapshotUploadTransaction(out var uploadTransaction))
        {
            snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Uploading");

        ClientSessionWork.Run(async () =>
        {
            try
            {
                var service = new ModSnapshotUploadService(settings, uploadTransaction);
                ModSnapshotUploadResult result = await ClientSessionWork.Observe(service.UploadConfiguredSnapshotAsync(
                    snapshotUploadKind: ModSnapshotUploadKinds.ManualUpload));
                using var publication = ClientSessionWork.EnterPublication();
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
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                snapshotUploadStatus = ClashOfRimText.Key(
                    "ClashOfRim.SnapshotUpload.Exception",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Snapshot upload failed: " + ex);
                NotifyPlayerMessage(snapshotUploadStatus, MessageTypeDefOf.RejectInput);
            }
            finally
            {
                uploadTransaction.Dispose();
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

        if (!TryBeginSnapshotUploadTransaction(out var uploadTransaction))
        {
            snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.Menu.UploadSnapshotBusy");
            Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.Menu.UploadSnapshotUploading");

        ClientSessionWork.Run(async () =>
        {
            try
            {
                var service = new ModSnapshotUploadService(settings, uploadTransaction);
                ModSnapshotUploadResult result = await ClientSessionWork.Observe(service.UploadConfiguredSnapshotAsync(
                    snapshotUploadKind: ModSnapshotUploadKinds.ManualUpload));
                using var publication = ClientSessionWork.EnterPublication();
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
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
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
                uploadTransaction.Dispose();
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

        if (!TryBeginSnapshotUploadTransaction(out var uploadTransaction))
        {
            snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.AutosaveSnapshot.SkippedBusy");
            ClashLog.Message("[ClashOfRim] Autosave snapshot upload skipped: sync already running.");
            return;
        }

        snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.AutosaveSnapshot.Uploading");

        ClientSessionWork.Run(async () =>
        {
            try
            {
                var service = new ModSnapshotUploadService(settings, uploadTransaction);
                ModSnapshotUploadResult result = await ClientSessionWork.Observe(service.UploadConfiguredSnapshotAsync(
                    snapshotUploadKind: ModSnapshotUploadKinds.AutoSave));
                using var publication = ClientSessionWork.EnterPublication();
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
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                snapshotUploadStatus = ClashOfRimText.Key(
                    "ClashOfRim.AutosaveSnapshot.Exception",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Autosave snapshot upload exception: " + ex);
                NotifyPlayerMessage(snapshotUploadStatus, MessageTypeDefOf.RejectInput);
            }
            finally
            {
                uploadTransaction.Dispose();
            }
        });
    }

}
