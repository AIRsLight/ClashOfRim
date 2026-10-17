using static AIRsLight.ClashOfRim.Diplomacy.ClientDiplomacyPresentation;
using static AIRsLight.ClashOfRim.Modules.Events.ClientEventDiplomacyPolicy;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using System;
using System.Collections.Generic;
using System.Net.Http;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.EventLetters;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Support;
using RimWorld;
using Verse;
using static AIRsLight.ClashOfRim.EventLetters.ClientEventLetterPolicy;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal bool HandleEventLetterAction(string eventId, ClashOfRimEventLetterActionKind actionKind)
    {
        ModEventDetailDto? detail = eventInbox.FindDetail(eventId);
        if (detail is null)
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.EventLetter.ActionMissingDetail", eventId.Named("EVENTID"));
            StartAutomaticEventRefresh(ClashOfRimText.Key("ClashOfRim.EventLetter.RefreshReasonMissingDetail"));
            return false;
        }

        if (!CanRunEventLetterAction(detail, actionKind, ResolveEventReferenceGroup(eventId)))
        {
            return false;
        }

        return actionKind switch
        {
            ClashOfRimEventLetterActionKind.Accept => HandleEventLetterAccept(detail),
            ClashOfRimEventLetterActionKind.Reject => HandleEventLetterReject(detail),
            _ => false
        };
    }

    private bool HandleEventLetterAccept(ModEventDetailDto detail)
    {
        if (SupportPawnApplicator.IsSupportPawnDetail(detail))
        {
            if (IsAnyManualOrSnapshotSyncInProgress())
            {
                giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.EventLetter.SupportAcceptBusy");
                Messages.Message(giftProcessingStatus, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }

            SupportPawnApplicationResult result = SupportPawnApplicator.ApplyToCurrentMap(detail);
            giftProcessingStatus = result.Message;
            Messages.Message(
                result.Message,
                result.Applied ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.RejectInput,
                historical: false);
            if (result.Applied)
            {
                ClientEventLetterRuntime.RemoveEventLetters(detail.EventId);
                StartConfirmSupportPawnApplication(detail);
            }
            else
            {
                HandlePawnFlowApplicationFailure("letter-support-accept", detail.EventId, sourceEventId: null, result);
            }

            return result.Applied;
        }

        if (IsDiplomacyEvent(detail))
        {
            return StartRespondDiplomacyEvent(
                detail,
                accepted: true,
                reason: ClashOfRimText.Key("ClashOfRim.EventLetter.ReasonAcceptedByLetter"));
        }

        if (!ItemDeliveryClientProcessor.IsGiftDetail(detail))
        {
            giftProcessingStatus = ClashOfRimText.Key(
                "ClashOfRim.EventLetter.AcceptUnsupportedTyped",
                detail.EventId.Named("EVENTID"),
                detail.EventType.Named("TYPE"));
            Messages.Message(giftProcessingStatus, MessageTypeDefOf.NeutralEvent, historical: false);
            return false;
        }

        if (IsAnyManualOrSnapshotSyncInProgress())
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.EventLetter.GiftAcceptBusy");
            Messages.Message(giftProcessingStatus, MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }

        return ProcessGiftDetail(detail, GiftClientDecision.Accept);
    }

    private void StartConfirmSupportPawnApplication(ModEventDetailDto detail)
    {
        if (IsAnyManualOrSnapshotSyncInProgress())
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.EventLetter.SupportConfirmBusy");
            Messages.Message(giftProcessingStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        string eventId = detail.EventId;
        StartConfirmEventApplicationSnapshot(new EventApplicationSnapshotConfirmationRequest
        {
            EventId = eventId,
            ClientApplicationResult = "SupportPawnArrived",
            Operation = LocalMutationKind.SupportArrival,
            IdempotencyPrefix = "support-confirm",
            UploadingStatus = ClashOfRimText.Key("ClashOfRim.EventLetter.SupportConfirmUploading", eventId.Named("EVENTID")),
            BuildFailurePrefix = ClashOfRimText.Key("ClashOfRim.EventLetter.SupportConfirmBuildFailed"),
            FailurePrefix = ClashOfRimText.Key("ClashOfRim.EventLetter.SupportConfirmFailed"),
            RejectedPrefix = ClashOfRimText.Key("ClashOfRim.EventLetter.SupportConfirmRejected"),
            ExceptionPrefix = ClashOfRimText.Key("ClashOfRim.EventLetter.SupportConfirmException"),
            EmptyEventMessage = ClashOfRimText.Key("ClashOfRim.EventLetter.SupportConfirmEmpty"),
            RetryAction = () => StartConfirmSupportPawnApplication(detail),
            OnAcceptedOnMainThread = response =>
            {
                ClashOfRimGameComponent.MarkManualEventHandled(eventId);
                giftProcessingStatus = ClashOfRimText.Key(
                    "ClashOfRim.EventLetter.SupportConfirmSucceeded",
                    eventId.Named("EVENTID"),
                    response.AppliedSnapshotId.Named("SNAPSHOT"));
                Messages.Message(giftProcessingStatus, MessageTypeDefOf.PositiveEvent, historical: false);
            }
        });
    }

    private bool HandleEventLetterReject(ModEventDetailDto detail)
    {
        if (!CanRejectEventFromLetter(detail))
        {
            giftProcessingStatus = ClashOfRimText.Key(
                "ClashOfRim.EventLetter.RejectUnsupportedTyped",
                detail.EventId.Named("EVENTID"),
                detail.EventType.Named("TYPE"));
            Messages.Message(giftProcessingStatus, MessageTypeDefOf.NeutralEvent, historical: false);
            return false;
        }

        if (SupportPawnApplicator.IsRejectableSupportPawnDetail(detail))
        {
            return StartRejectSupportPawn(detail, ClashOfRimText.Key("ClashOfRim.EventLetter.ReasonRejectedSupportByLetter"));
        }

        if (IsRejectableDiplomacyEvent(detail))
        {
            return StartRespondDiplomacyEvent(detail, accepted: false, reason: ClashOfRimText.Key("ClashOfRim.EventLetter.ReasonRejectedByLetter"));
        }

        return StartRejectGift(detail, ClashOfRimText.Key("ClashOfRim.EventLetter.ReasonRejectedByLetter"));
    }

    private bool StartRespondDiplomacyEvent(ModEventDetailDto detail, bool accepted, string reason)
    {
        if (!CanRunManualSync(out string failureReason))
        {
            giftProcessingStatus = failureReason;
            return false;
        }

        if (!TryBeginManualSynchronization(out var manualOperation, out failureReason))
        {
            giftProcessingStatus = failureReason;
            return false;
        }
        giftProcessingStatus = accepted
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.DiplomacyRespondingAccept", detail.EventId.Named("EVENTID"))
            : ClashOfRimText.Key("ClashOfRim.EventLetter.DiplomacyRespondingReject", detail.EventId.Named("EVENTID"));
        ClientSessionWork.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto> result =
                    await ClientSessionWork.Observe(client.RespondDiplomacyEventAsync(detail.EventId, accepted, reason));

                using var publication = ClientSessionWork.EnterPublication();
                if (!result.Success || result.Response is null)
                {
                    giftProcessingStatus = ClashOfRimText.Key(
                        "ClashOfRim.EventLetter.DiplomacyResponseFailed",
                        result.ErrorCode.Named("CODE"),
                        result.Message.Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    giftProcessingStatus = ClashOfRimText.Key(
                        "ClashOfRim.EventLetter.DiplomacyResponseRejected",
                        response.ErrorCode.Named("CODE"),
                        response.Message.Named("MESSAGE"));
                    return;
                }

                giftProcessingStatus = accepted
                    ? ClashOfRimText.Key("ClashOfRim.EventLetter.DiplomacyAccepted", detail.EventId.Named("EVENTID"))
                    : ClashOfRimText.Key("ClashOfRim.EventLetter.DiplomacyRejected", detail.EventId.Named("EVENTID"));
                string actorUserId = detail.Actor?.UserId ?? string.Empty;
                FactionRelationKind relationKind = ResolveDiplomacyRelationKind(detail.EventType);
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (accepted)
                    {
                        PlayerFactionProxyUtility.SetPlayerRelation(
                            actorUserId,
                            relationKind,
                            DiplomacyRelationReason(detail.EventType));
                        RequestWorldMapMarkerRefresh(ClashOfRimText.Key("ClashOfRim.EventLetter.RefreshReasonDiplomacyRelationChanged"));
                    }

                    ClashOfRimGameComponent.MarkManualEventHandled(detail.EventId);
                    Messages.Message(giftProcessingStatus, MessageTypeDefOf.NeutralEvent, historical: false);
                });
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                giftProcessingStatus = ClashOfRimText.Key(
                    "ClashOfRim.EventLetter.DiplomacyResponseException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Diplomacy response failed: " + ex);
            }
            finally
            {
                manualOperation.Dispose();
            }
        });

        return true;
    }

    private string ResolveEventReferenceGroup(string eventId)
    {
        return eventInbox.GroupOf(eventId) ?? "Unknown";
    }

    private void PostEventLetters(IReadOnlyCollection<ModEventDetailDto> details, string source) => eventDelivery.Post(details, source);
}
