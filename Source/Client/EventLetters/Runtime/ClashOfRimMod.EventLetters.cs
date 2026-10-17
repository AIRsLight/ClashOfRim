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
        => _ = eventConfirmations.SupportArrivalAsync(detail.EventId);

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
        => eventDecisions.RespondDiplomacy(detail.EventId, detail.Actor?.UserId ?? string.Empty, detail.EventType, accepted, reason, out _);

    private string ResolveEventReferenceGroup(string eventId)
    {
        return eventInbox.GroupOf(eventId) ?? "Unknown";
    }

    private void PostEventLetters(IReadOnlyCollection<ModEventDetailDto> details, string source) => eventDelivery.Post(details, source);
}
