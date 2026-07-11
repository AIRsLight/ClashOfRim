using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.EventLetters;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.Support;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal bool HandleEventLetterAction(string eventId, ClashOfRimEventLetterActionKind actionKind)
    {
        ModEventDetailDto? detail = FindEventDetail(eventId);
        if (detail is null)
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.EventLetter.ActionMissingDetail", eventId.Named("EVENTID"));
            StartAutomaticEventRefresh(ClashOfRimText.Key("ClashOfRim.EventLetter.RefreshReasonMissingDetail"));
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
                RemoveEventLetters(detail.EventId);
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

        if (!GiftClientProcessor.IsGiftDetail(detail))
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

    private static void RemoveEventLetters(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId) || Find.LetterStack?.LettersListForReading is null)
        {
            return;
        }

        List<ClashOfRimEventChoiceLetter> letters = Find.LetterStack.LettersListForReading
            .OfType<ClashOfRimEventChoiceLetter>()
            .Where(letter => string.Equals(letter.EventId, eventId, StringComparison.Ordinal))
            .ToList();
        for (int i = 0; i < letters.Count; i++)
        {
            Find.LetterStack.RemoveLetter(letters[i]);
            Find.Archive?.Remove(letters[i]);
        }
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
            Operation = ClashOfRimText.Key("ClashOfRim.EventLetter.SupportConfirmOperation"),
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

        manualSyncInProgress = true;
        giftProcessingStatus = accepted
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.DiplomacyRespondingAccept", detail.EventId.Named("EVENTID"))
            : ClashOfRimText.Key("ClashOfRim.EventLetter.DiplomacyRespondingReject", detail.EventId.Named("EVENTID"));
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto> result =
                    await client.RespondDiplomacyEventAsync(detail.EventId, accepted, reason);

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
            {
                giftProcessingStatus = ClashOfRimText.Key(
                    "ClashOfRim.EventLetter.DiplomacyResponseException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Diplomacy response failed: " + ex);
            }
            finally
            {
                manualSyncInProgress = false;
            }
        });

        return true;
    }

    private static void LogGiftDetails(string source, IReadOnlyCollection<ModEventDetailDto>? details)
    {
        if (details is null)
        {
            ClashLog.Message($"[ClashOfRim][{source}] details=null.");
            return;
        }

        ClashLog.Message($"[ClashOfRim][{source}] detailCount={details.Count}.");
        foreach (ModEventDetailDto detail in details.Where(GiftClientProcessor.IsGiftDetail).Take(5))
        {
            ClashLog.Message(
                $"[ClashOfRim][{source}] gift event={detail.EventId} status={detail.Status} targetMap={detail.TargetContext?.MapUniqueId ?? "<null>"} landingMode={detail.TargetContext?.LandingMode ?? "<null>"} payloadLength={detail.PayloadSummary?.Length ?? 0} payloadPreview={Preview(detail.PayloadSummary, 512)}");
        }
    }

    private void PostEventLetters(IReadOnlyCollection<ModEventDetailDto> details, string source)
    {
        if (details.Count == 0)
        {
            return;
        }

        ApplyDiplomacyEventSideEffects(details);
        ApplyServerNotificationSideEffects(details);

        lock (eventStateLock)
        {
            foreach (ModEventDetailDto detail in details)
            {
                if (!ShouldPostEventLetter(detail))
                {
                    continue;
                }

                string eventId = detail.EventId;
                if (postedEventLetterIds.Contains(eventId) || HasOpenClashOfRimLetter(eventId))
                {
                    continue;
                }

                if (IsEventInGroup(eventId, "WaitingForUserChoice"))
                {
                    ClashOfRimGameComponent.RegisterPendingManualEvent(eventId);
                }

                string group = ResolveEventReferenceGroup(detail.EventId);
                ClashOfRimEventLetterProjection projection = BuildEventLetterProjection(detail, group);
                if (projection.HasServerAction)
                {
                    ClashOfRimEventChoiceLetter letter = new()
                    {
                        def = projection.LetterDef,
                        ID = Find.UniqueIDsManager.GetNextLetterID(),
                        EventId = projection.EventId,
                        EventType = projection.EventType,
                        Actions = projection.Actions.ToList(),
                        CanAccept = projection.Actions.Contains(ClashOfRimEventLetterActionKind.Accept),
                        CanReject = projection.Actions.Contains(ClashOfRimEventLetterActionKind.Reject),
                        lookTargets = projection.LookTargets
                    };
                    letter.Label = projection.Label;
                    letter.title = projection.Label;
                    letter.Text = projection.Text;
                    Find.LetterStack.ReceiveLetter(letter, $"ClashOfRim event {eventId} from {source}", playSound: true);
                }
                else
                {
                    Find.LetterStack.ReceiveLetter(
                        projection.Label,
                        projection.Text,
                        projection.LetterDef,
                        projection.LookTargets,
                        null,
                        null,
                        null,
                        $"ClashOfRim event {eventId} from {source}",
                        playSound: true);
                }

                postedEventLetterIds.Add(eventId);
                ClashLog.Message($"[ClashOfRim][EventLetter] posted event={eventId} type={detail.EventType} source={source}.");
            }
        }
    }

    private ClashOfRimEventLetterProjection BuildEventLetterProjection(ModEventDetailDto detail, string group)
    {
        LookTargets? lookTargets = ShouldShowLetterLocation(detail)
            ? BuildEventLookTargets(detail)
            : null;

        return new ClashOfRimEventLetterProjection(
            detail.EventId,
            detail.EventType,
            BuildEventLetterLabel(detail),
            BuildEventLetterText(detail, group),
            ResolveLetterDef(detail),
            lookTargets,
            BuildEventLetterActions(detail, lookTargets, group));
    }

    private static IReadOnlyList<ClashOfRimEventLetterActionKind> BuildEventLetterActions(
        ModEventDetailDto detail,
        LookTargets? lookTargets,
        string group)
    {
        var actions = new List<ClashOfRimEventLetterActionKind>();
        bool canRunServerAction = string.Equals(group, "WaitingForUserChoice", StringComparison.Ordinal)
            || string.Equals(group, "DirectlyProcessable", StringComparison.Ordinal);
        if (canRunServerAction && CanAcceptEventFromLetter(detail))
        {
            actions.Add(ClashOfRimEventLetterActionKind.Accept);
        }

        if (canRunServerAction && CanRejectEventFromLetter(detail))
        {
            actions.Add(ClashOfRimEventLetterActionKind.Reject);
        }

        if (lookTargets is not null && lookTargets.IsValid)
        {
            actions.Add(ClashOfRimEventLetterActionKind.JumpToTarget);
        }

        if (actions.Count > 0)
        {
            actions.Add(ClashOfRimEventLetterActionKind.Postpone);
        }

        return actions;
    }

    private string ResolveEventReferenceGroup(string eventId)
    {
        return lastEventReferenceGroups.TryGetValue(eventId, out string? knownGroup)
            ? knownGroup
            : ClashOfRimText.Key("ClashOfRim.EventGroup.Unknown");
    }

    private void ApplyServerNotificationSideEffects(IReadOnlyCollection<ModEventDetailDto> details)
    {
        foreach (ModEventDetailDto detail in details)
        {
            if (!string.Equals(detail.EventType, "ServerNotification", StringComparison.Ordinal))
            {
                continue;
            }

            lock (eventStateLock)
            {
                if (!appliedServerNotificationSideEffectIds.Add(detail.EventId))
                {
                    continue;
                }
            }

            ServerNotificationPayloadSummary? payload = ReadServerNotificationPayloadForDisplay(detail);
            if (payload is null
                || payload.RelatedAccepted != true
                || string.IsNullOrWhiteSpace(payload.RelatedUserId)
                || string.IsNullOrWhiteSpace(payload.RelatedEventType))
            {
                continue;
            }

            FactionRelationKind relationKind = ResolveDiplomacyRelationKind(payload.RelatedEventType!);
            PlayerFactionProxyUtility.SetPlayerRelation(
                payload.RelatedUserId,
                relationKind,
                DiplomacyRelationReason(payload.RelatedEventType!),
                canSendHostilityLetter: true);
            RequestWorldMapMarkerRefresh(ClashOfRimText.Key("ClashOfRim.EventLetter.RefreshReasonDiplomacyResponseNotification"));
            ClashLog.Message(
                "[ClashOfRim][Diplomacy] Applied response notification relation: event="
                + detail.EventId
                + ", related="
                + (payload.RelatedEventId ?? "<null>")
                + ", user="
                + payload.RelatedUserId
                + ", relation="
                + relationKind);
        }
    }

    private void ApplyDiplomacyEventSideEffects(IReadOnlyCollection<ModEventDetailDto> details)
    {
        foreach (ModEventDetailDto detail in details)
        {
            if (!IsImmediateDiplomacyEvent(detail))
            {
                continue;
            }

            string? actorUserId = detail.Actor?.UserId;
            if (string.IsNullOrWhiteSpace(actorUserId))
            {
                continue;
            }

            lock (eventStateLock)
            {
                if (!appliedDiplomacyEventSideEffectIds.Add(detail.EventId))
                {
                    continue;
                }
            }

            FactionRelationKind relationKind = ResolveDiplomacyRelationKind(detail.EventType);
            PlayerFactionProxyUtility.SetPlayerRelation(
                actorUserId,
                relationKind,
                DiplomacyRelationReason(detail.EventType),
                canSendHostilityLetter: true);
            RequestWorldMapMarkerRefresh(ClashOfRimText.Key("ClashOfRim.EventLetter.RefreshReasonDiplomacyRelationChanged"));
            ClashLog.Message(
                "[ClashOfRim][Diplomacy] Applied immediate diplomacy relation: event="
                + detail.EventId
                + ", actor="
                + actorUserId
                + ", type="
                + detail.EventType
                + ", relation="
                + relationKind);
        }
    }

    private bool ShouldPostEventLetter(ModEventDetailDto detail)
    {
        if (string.IsNullOrWhiteSpace(detail.EventId))
        {
            return false;
        }

        if (!lastEventReferenceGroups.TryGetValue(detail.EventId, out string? group))
        {
            return IsRaidSettlementDetail(detail)
                || IsRaidAttackerLossDetail(detail);
        }

        if (IsRaidAttackerLossDetail(detail))
        {
            return string.Equals(group, "DirectlyProcessable", StringComparison.Ordinal)
                || string.Equals(group, "DeliveredUnconfirmed", StringComparison.Ordinal)
                || string.Equals(group, "Conflicts", StringComparison.Ordinal)
                || string.Equals(group, "Rejected", StringComparison.Ordinal);
        }

        if (IsDefenderRaidTimeoutNotification(detail))
        {
            return false;
        }

        return string.Equals(group, "WaitingForUserChoice", StringComparison.Ordinal)
            || (string.Equals(group, "DirectlyProcessable", StringComparison.Ordinal)
                && !CanAutoApplyDirectlyProcessableEvent(detail))
            || (string.Equals(group, "DeliveredUnconfirmed", StringComparison.Ordinal)
                && !CanAutoApplyDirectlyProcessableEvent(detail))
            || string.Equals(group, "Conflicts", StringComparison.Ordinal)
            || string.Equals(group, "Rejected", StringComparison.Ordinal);
    }

    private bool IsEventInGroup(string eventId, string expectedGroup)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return false;
        }

        lock (eventStateLock)
        {
            return lastEventReferenceGroups.TryGetValue(eventId, out string? group)
                && string.Equals(group, expectedGroup, StringComparison.Ordinal);
        }
    }

    private static bool CanAutoApplyDirectlyProcessableEvent(ModEventDetailDto detail)
    {
        return string.Equals(detail.EventType, "GiftReturn", StringComparison.Ordinal)
            && GiftClientProcessor.IsGiftDetail(detail)
            || SupportPawnApplicator.IsReturnToSenderDetail(detail)
            || IsRaidAttackerLossDetail(detail);
    }

    private static bool IsRaidAttackerLossDetail(ModEventDetailDto detail)
    {
        return RaidAttackerLossPayloadReader.HasAttackerLoss(detail);
    }

    private static bool IsRaidSettlementDetail(ModEventDetailDto detail)
    {
        return string.Equals(detail.EventType, "Raid", StringComparison.Ordinal)
            && detail.PayloadSummary?.IndexOf("\"Settlement\"", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool HasOpenClashOfRimLetter(string eventId)
    {
        return Find.LetterStack?.LettersListForReading
            .OfType<ClashOfRimEventChoiceLetter>()
            .Any(letter => string.Equals(letter.EventId, eventId, StringComparison.Ordinal)) == true;
    }

    private static bool CanAcceptEventFromLetter(ModEventDetailDto detail)
    {
        return GiftClientProcessor.IsGiftDetail(detail)
            || SupportPawnApplicator.IsSupportPawnDetail(detail)
            || IsRejectableDiplomacyEvent(detail);
    }

    private static bool CanRejectEventFromLetter(ModEventDetailDto detail)
    {
        return string.Equals(detail.EventType, "Gift", StringComparison.Ordinal)
            && GiftClientProcessor.IsGiftDetail(detail)
            && !IsForcedGiftDetail(detail)
            || SupportPawnApplicator.IsRejectableSupportPawnDetail(detail)
            || IsRejectableDiplomacyEvent(detail);
    }

    private ModEventDetailDto? FindEventDetail(string eventId)
    {
        lock (eventStateLock)
        {
            return lastEventDetails.FirstOrDefault(detail =>
                string.Equals(detail.EventId, eventId, StringComparison.Ordinal));
        }
    }

    private static LetterDef ResolveLetterDef(ModEventDetailDto detail)
    {
        if (string.Equals(detail.EventType, "ServerNotification", StringComparison.Ordinal))
        {
            ServerNotificationPayloadSummary? payload = ReadServerNotificationPayloadForDisplay(detail);
            return payload?.Severity switch
            {
                2 => LetterDefOf.ThreatBig,
                1 => LetterDefOf.NegativeEvent,
                _ => LetterDefOf.NeutralEvent
            };
        }

        return detail.EventType switch
        {
            "Raid" => LetterDefOf.ThreatBig,
            "WarDeclaration" => LetterDefOf.ThreatBig,
            "Gift" when IsForcedGiftDetail(detail) => LetterDefOf.NegativeEvent,
            "Gift" => LetterDefOf.PositiveEvent,
            "GiftReturn" when IsTradeDeliveryDetail(detail) => LetterDefOf.PositiveEvent,
            "GiftReturn" => LetterDefOf.NeutralEvent,
            "Trade" => LetterDefOf.PositiveEvent,
            "SupportPawn" => LetterDefOf.PositiveEvent,
            "AllianceRequest" => LetterDefOf.NeutralEvent,
            "AllianceCancellation" => LetterDefOf.NeutralEvent,
            "ServerNotification" => LetterDefOf.NeutralEvent,
            "PeaceRequest" => LetterDefOf.NeutralEvent,
            _ => LetterDefOf.NeutralEvent
        };
    }

    private static string BuildEventLetterLabel(ModEventDetailDto detail)
    {
        return detail.EventType switch
        {
            "Raid" when IsRaidAttackerLossDetail(detail) => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.RaidAttackerLoss"),
            "Raid" when IsRaidSettlementDetail(detail) => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.RaidSettlement"),
            "Raid" => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.Raid"),
            "Gift" when IsForcedGiftDetail(detail) => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.ForcedGift"),
            "Gift" => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.Gift"),
            "GiftReturn" when IsTradeDeliveryDetail(detail) => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.TradeDelivery"),
            "GiftReturn" when IsTradeReturnDetail(detail) => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.TradeReturn"),
            "GiftReturn" => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.GiftReturn"),
            "Trade" => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.Trade"),
            "SupportPawn" => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.SupportPawn"),
            "AllianceRequest" => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.AllianceRequest"),
            "AllianceCancellation" => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.AllianceCancellation"),
            "WarDeclaration" => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.WarDeclaration"),
            "PeaceRequest" => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.PeaceRequest"),
            "ServerNotification" => BuildServerNotificationEventLetterLabel(detail),
            _ => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.ServerEvent")
        };
    }

    private string BuildEventLetterText(ModEventDetailDto detail, string group)
    {
        if (string.Equals(detail.EventType, "Raid", StringComparison.Ordinal)
            && TryBuildRaidSettlementEventLetterText(detail, out string raidSettlementText))
        {
            return raidSettlementText;
        }

        if (string.Equals(detail.EventType, "Raid", StringComparison.Ordinal)
            && TryBuildRaidAttackerLossEventLetterText(detail, out string raidAttackerLossText))
        {
            return raidAttackerLossText;
        }

        if (IsReadOnlyEventGroup(group) && !string.Equals(detail.EventType, "ServerNotification", StringComparison.Ordinal))
        {
            return BuildReadOnlyEventLetterText(detail, group);
        }

        if (GiftClientProcessor.IsGiftDetail(detail))
        {
            return BuildGiftEventLetterText(detail);
        }

        if (IsDiplomacyEvent(detail))
        {
            return BuildDiplomacyEventLetterText(detail);
        }

        if (string.Equals(detail.EventType, "SupportPawn", StringComparison.Ordinal))
        {
            return BuildSupportPawnEventLetterText(detail, group);
        }

        if (string.Equals(detail.EventType, "ServerNotification", StringComparison.Ordinal))
        {
            return BuildServerNotificationEventLetterText(detail);
        }

        string actionText = BuildEventActionText(detail, group);
        if (!Prefs.DevMode)
        {
            return actionText;
        }

        string targetText = ShouldShowLetterLocation(detail)
            ? BuildTargetText(detail)
            : "";
        string payloadText = string.IsNullOrWhiteSpace(detail.PayloadSummary)
            ? ""
            : "\n" + ClashOfRimText.Key("ClashOfRim.EventLetter.PayloadSummary", Preview(detail.PayloadSummary, 900).Named("PAYLOAD"));

        return actionText
            + targetText
            + "\n\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.DebugHeading")
            + "\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.ServerEventLine", detail.EventId.Named("EVENTID"))
            + "\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.TypeLine", FormatEventType(detail.EventType).Named("TYPE"))
            + "\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.StatusLine", detail.Status.Named("STATUS"))
            + "\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.GroupLine", FormatEventGroupName(group).Named("GROUP"))
            + payloadText;
    }

    private static string BuildGiftEventLetterText(ModEventDetailDto detail)
    {
        string sender = FormatEventParty(detail.Actor);
        GiftPayloadSummary? payload = ReadGiftPayloadForDisplay(detail);
        bool tradeDelivery = payload?.IsTradeDelivery == true;
        bool tradeReturn = payload?.IsTradeReturn == true;
        bool forcedGift = payload?.IsForcedDelivery == true;
        string intro = forcedGift
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.ForcedGiftIntro", sender.Named("SENDER"))
            : tradeDelivery
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.TradeDeliveryIntro", sender.Named("SENDER"))
            : tradeReturn
                ? ClashOfRimText.Key("ClashOfRim.EventLetter.TradeReturnIntro")
                : string.Equals(detail.EventType, "GiftReturn", StringComparison.Ordinal)
                    ? ClashOfRimText.Key("ClashOfRim.EventLetter.GiftReturnIntro", sender.Named("SENDER"))
                    : ClashOfRimText.Key("ClashOfRim.EventLetter.GiftIntro", sender.Named("SENDER"));
        string action = forcedGift
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.ForcedGiftAction")
            : tradeDelivery || tradeReturn
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.TradeDeliveryAction")
            : string.Equals(detail.EventType, "GiftReturn", StringComparison.Ordinal)
                ? ClashOfRimText.Key("ClashOfRim.EventLetter.GiftReturnAction")
                : ClashOfRimText.Key("ClashOfRim.EventLetter.GiftAction");
        string itemList = payload is null
            ? "- " + ClashOfRimText.Key("ClashOfRim.EventLetter.GiftItemsUnavailable")
            : BuildGiftItemListText(payload);

        return intro
            + "\n\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.GiftItemsHeading")
            + "\n"
            + itemList
            + "\n\n"
            + action;
    }

    private static string BuildDiplomacyEventLetterText(ModEventDetailDto detail)
    {
        string sender = FormatEventParty(detail.Actor);
        string intro = detail.EventType switch
        {
            "AllianceRequest" => ClashOfRimText.Key("ClashOfRim.EventLetter.AllianceRequestIntro", sender.Named("SENDER")),
            "AllianceCancellation" => ClashOfRimText.Key("ClashOfRim.EventLetter.AllianceCancellationIntro", sender.Named("SENDER")),
            "WarDeclaration" => ClashOfRimText.Key("ClashOfRim.EventLetter.WarDeclarationIntro", sender.Named("SENDER")),
            "PeaceRequest" => ClashOfRimText.Key("ClashOfRim.EventLetter.PeaceRequestIntro", sender.Named("SENDER")),
            _ => ClashOfRimText.Key("ClashOfRim.EventLetter.GenericAction")
        };
        string action = detail.EventType switch
        {
            "AllianceRequest" => ClashOfRimText.Key("ClashOfRim.EventLetter.AllianceRequestAction"),
            "AllianceCancellation" => ClashOfRimText.Key("ClashOfRim.EventLetter.AllianceCancellationAction"),
            "WarDeclaration" => ClashOfRimText.Key("ClashOfRim.EventLetter.WarDeclarationAction"),
            "PeaceRequest" => ClashOfRimText.Key("ClashOfRim.EventLetter.PeaceRequestAction"),
            _ => ClashOfRimText.Key("ClashOfRim.EventLetter.GenericAction")
        };
        string relation = ClashOfRimText.Key(
            "ClashOfRim.EventLetter.DiplomacyRelation",
            FormatDiplomacyRelationKind(ResolveDiplomacyRelationKind(detail.EventType)).Named("RELATION"));
        return intro + "\n\n" + relation + "\n\n" + action;
    }

    private static string BuildSupportPawnEventLetterText(ModEventDetailDto detail, string group)
    {
        string sender = FormatEventParty(detail.Actor);
        SupportPawnPayloadSummary? payload = ReadSupportPawnPayloadForDisplay(detail);
        string pawn = ResolveSupportPawnLabel(payload);
        string duration = FormatSupportDuration(payload);

        if (payload?.ReturnToSender == true)
        {
            string reason = string.IsNullOrWhiteSpace(payload.ReturnReason)
                ? ClashOfRimText.Key("ClashOfRim.EventLetter.SupportReturnReasonDefault")
                : payload.ReturnReason!;
            return ClashOfRimText.Key(
                "ClashOfRim.EventLetter.SupportReturnIntro",
                pawn.Named("PAWN"),
                reason.Named("REASON"));
        }

        if (string.Equals(group, "Rejected", StringComparison.Ordinal))
        {
            return ClashOfRimText.Key(
                "ClashOfRim.EventLetter.SupportRejectedIntro",
                pawn.Named("PAWN"),
                sender.Named("SENDER"));
        }

        string intro = ClashOfRimText.Key(
            "ClashOfRim.EventLetter.SupportRequestIntro",
            sender.Named("SENDER"),
            pawn.Named("PAWN"));
        string term = ClashOfRimText.Key(
            "ClashOfRim.EventLetter.SupportTerm",
            duration.Named("DURATION"));
        string action = payload?.PermanentSupport == true
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.SupportPermanentRequestAction")
            : payload?.TemporaryControl == true
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.SupportRequestAction")
            : ClashOfRimText.Key("ClashOfRim.EventLetter.SupportNoticeAction");
        return intro + "\n\n" + term + "\n\n" + action;
    }

    private static string BuildServerNotificationEventLetterText(ModEventDetailDto detail)
    {
        ServerNotificationPayloadSummary? payload = ReadServerNotificationPayloadForDisplay(detail);
        if (payload is null)
        {
            return ClashOfRimText.Key("ClashOfRim.EventLetter.ServerNotificationFallback");
        }

        string title = string.IsNullOrWhiteSpace(payload.Title)
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.Label.ServerNotification")
            : payload.Title!;
        string message = string.IsNullOrWhiteSpace(payload.Message)
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.ServerNotificationFallback")
            : payload.Message!;
        return title + "\n\n" + message;
    }

    private static bool TryBuildRaidSettlementEventLetterText(ModEventDetailDto detail, out string text)
    {
        text = string.Empty;
        RaidSettlementPayloadSummary? payload = ReadRaidSettlementPayloadForDisplay(detail);
        if (payload?.Settlement?.Losses is null)
        {
            return false;
        }

        IReadOnlyList<string> lossLines = BuildRaidDefenderLossLines(payload.Settlement.Losses);
        if (lossLines.Count == 0)
        {
            text = ClashOfRimText.Key("ClashOfRim.EventLetter.RaidNoDefenderLoss");
            return true;
        }

        text = ClashOfRimText.Key("ClashOfRim.EventLetter.RaidSettlementIntro")
            + "\n\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.RaidDefenderLossHeading")
            + "\n"
            + string.Join("\n", lossLines);
        return true;
    }

    private static bool TryBuildRaidAttackerLossEventLetterText(ModEventDetailDto detail, out string text)
    {
        text = string.Empty;
        if (!RaidAttackerLossPayloadReader.TryReadSummary(detail, out RaidAttackerLossSummary? loss)
            || loss is null)
        {
            return false;
        }

        IReadOnlyList<string> pawnLines = BuildRaidAttackerLossPawnLines(loss);
        IReadOnlyList<string> thingLines = BuildRaidAttackerLossThingLines(loss.LostThings);
        text = ClashOfRimText.Key("ClashOfRim.EventLetter.RaidAttackerLossIntro")
            + "\n\n"
            + string.Join("\n", pawnLines)
            + "\n\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.RaidAttackerLossThingsHeading")
            + "\n"
            + string.Join("\n", thingLines);
        return true;
    }

    private static IReadOnlyList<string> BuildRaidAttackerLossPawnLines(RaidAttackerLossSummary loss)
    {
        List<string> keys = (loss.LostPawnGlobalKeys ?? new List<string>())
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (keys.Count == 0)
        {
            return new[] { "- " + ClashOfRimText.Key("ClashOfRim.None") };
        }

        List<string> labels = new();
        int missing = 0;
        foreach (string key in keys)
        {
            if (TryFindLocalPawnByGlobalKey(key, out Pawn? pawn) && pawn is not null)
            {
                labels.Add(FormatRaidAttackerLostPawnLabel(pawn));
                continue;
            }

            missing++;
        }

        if (missing == keys.Count)
        {
            labels.Add(ClashOfRimText.Key("ClashOfRim.EventLetter.RaidAttackerLossPawnCountFallback", keys.Count.Named("COUNT")));
        }
        else if (missing > 0)
        {
            labels.Add(ClashOfRimText.Key("ClashOfRim.EventLetter.RaidAttackerLossPawnCountFallback", missing.Named("COUNT")));
        }

        return labels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .OrderBy(label => label, StringComparer.Ordinal)
            .Select(label => "- " + ClashOfRimText.Key("ClashOfRim.EventLetter.RaidAttackerLossPawnLine", label.Named("PAWN")))
            .ToList();
    }

    private static string FormatRaidAttackerLostPawnLabel(Pawn pawn)
    {
        string label = pawn.Name is null
            ? pawn.LabelShortCap
            : pawn.Name.ToStringShort;
        label = string.IsNullOrWhiteSpace(label)
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.SupportUnknownPawn")
            : label.Trim();
        string? title = pawn.story?.TitleShortCap;
        return string.IsNullOrWhiteSpace(title)
            ? label
            : ClashOfRimText.Key(
                "ClashOfRim.EventLetter.RaidAttackerLossPawnWithTitle",
                label.Named("PAWN"),
                title.Named("TITLE"));
    }

    private static bool TryFindLocalPawnByGlobalKey(string globalKey, out Pawn? pawn)
    {
        pawn = null;
        HashSet<string> localKeys = BuildRaidAttackerLossLocalPawnKeys(globalKey);
        return localKeys.Count > 0
            && TryFindLocalPawnByKeys(localKeys, out pawn);
    }

    private static bool TryFindLocalPawnByKeys(HashSet<string> localKeys, out Pawn? pawn)
    {
        pawn = null;
        if (Current.ProgramState == ProgramState.Entry)
        {
            return false;
        }

        foreach (Pawn candidate in PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead)
        {
            if (candidate is null || candidate.Destroyed)
            {
                continue;
            }

            if (!localKeys.Contains(candidate.ThingID) && !localKeys.Contains(candidate.GetUniqueLoadID()))
            {
                continue;
            }

            pawn = candidate;
            return true;
        }

        return false;
    }

    private static HashSet<string> BuildRaidAttackerLossLocalPawnKeys(string? globalKey)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        AddRaidAttackerLossLocalPawnKey(keys, globalKey);
        if (string.IsNullOrWhiteSpace(globalKey))
        {
            return keys;
        }

        string key = globalKey!;
        int thingMarker = key.LastIndexOf("/thing:", StringComparison.Ordinal);
        if (thingMarker >= 0)
        {
            AddRaidAttackerLossLocalPawnKey(keys, key.Substring(thingMarker + "/thing:".Length));
            return keys;
        }

        int looseMarker = key.LastIndexOf("thing:", StringComparison.Ordinal);
        if (looseMarker >= 0)
        {
            AddRaidAttackerLossLocalPawnKey(keys, key.Substring(looseMarker + "thing:".Length));
        }

        return keys;
    }

    private static void AddRaidAttackerLossLocalPawnKey(HashSet<string> keys, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        string trimmed = key!.Trim();
        keys.Add(trimmed);
        if (trimmed.StartsWith("Thing_", StringComparison.Ordinal))
        {
            keys.Add(trimmed.Substring("Thing_".Length));
        }
        else
        {
            keys.Add("Thing_" + trimmed);
        }
    }

    private static IReadOnlyList<string> BuildRaidAttackerLossThingLines(IReadOnlyList<RaidLostThingSummary>? things)
    {
        if (things is null || things.Count == 0)
        {
            return new[] { "- " + ClashOfRimText.Key("ClashOfRim.None") };
        }

        Dictionary<string, int> countsByLabel = new(StringComparer.Ordinal);
        foreach (RaidLostThingSummary thing in things)
        {
            string label = RaidLostThingLabel(thing);
            int count = Math.Max(1, thing.StackCount);
            countsByLabel[label] = countsByLabel.TryGetValue(label, out int existing)
                ? existing + count
                : count;
        }

        return countsByLabel
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => "- " + ClashOfRimText.Key(
                "ClashOfRim.EventLetter.RaidAttackerLossThingLine",
                pair.Key.Named("THING"),
                pair.Value.Named("COUNT")))
            .ToList();
    }

    private static string RaidLostThingLabel(RaidLostThingSummary thing)
    {
        if (!string.IsNullOrWhiteSpace(thing.DisplayLabel))
        {
            return thing.DisplayLabel!;
        }

        if (!string.IsNullOrWhiteSpace(thing.Def))
        {
            ThingDef? def = DefDatabase<ThingDef>.GetNamedSilentFail(thing.Def);
            return def?.LabelCap.ToString() ?? thing.Def!;
        }

        return ClashOfRimText.Key("ClashOfRim.Unknown");
    }

    private static IReadOnlyList<string> BuildRaidDefenderLossLines(IReadOnlyList<RaidSettlementLossPayloadSummary> losses)
    {
        Dictionary<string, int> itemCountsByLabel = new(StringComparer.Ordinal);
        Dictionary<string, int> destroyedBuildingsByLabel = new(StringComparer.Ordinal);
        Dictionary<string, int> damagedBuildingsByLabel = new(StringComparer.Ordinal);
        foreach (RaidSettlementLossPayloadSummary loss in losses)
        {
            if (string.IsNullOrWhiteSpace(loss.Def))
            {
                continue;
            }

            ThingDef? def = DefDatabase<ThingDef>.GetNamedSilentFail(loss.Def);
            string label = def?.LabelCap.ToString() ?? loss.Def!;
            bool building = def?.category == ThingCategory.Building;
            if (building)
            {
                if (loss.LossCount > 0 && loss.WholeThingMissing)
                {
                    destroyedBuildingsByLabel[label] = destroyedBuildingsByLabel.TryGetValue(label, out int current)
                        ? current + Math.Max(1, loss.LossCount)
                        : Math.Max(1, loss.LossCount);
                }
                else if (loss.LossCount <= 0 && loss.RemainingHitPointsAfterDamage.HasValue)
                {
                    damagedBuildingsByLabel[label] = damagedBuildingsByLabel.TryGetValue(label, out int current)
                        ? current + 1
                        : 1;
                }

                continue;
            }

            if (loss.LossCount <= 0)
            {
                continue;
            }

            itemCountsByLabel[label] = itemCountsByLabel.TryGetValue(label, out int existing)
                ? existing + loss.LossCount
                : loss.LossCount;
        }

        var lines = new List<string>();
        lines.AddRange(itemCountsByLabel
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => "- " + ClashOfRimText.Key(
                "ClashOfRim.EventLetter.RaidLossItemLine",
                pair.Key.Named("THING"),
                pair.Value.Named("COUNT"))));
        lines.AddRange(destroyedBuildingsByLabel
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => "- " + ClashOfRimText.Key(
                "ClashOfRim.EventLetter.RaidLossBuildingLine",
                pair.Key.Named("THING"),
                pair.Value.Named("COUNT"))));
        lines.AddRange(damagedBuildingsByLabel
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => "- " + ClashOfRimText.Key(
                "ClashOfRim.EventLetter.RaidDamagedBuildingLine",
                pair.Key.Named("THING"),
                pair.Value.Named("COUNT"))));
        return lines;
    }

    private static RaidSettlementPayloadSummary? ReadRaidSettlementPayloadForDisplay(ModEventDetailDto detail)
    {
        if (string.IsNullOrWhiteSpace(detail.PayloadSummary))
        {
            return null;
        }

        try
        {
            var serializer = new DataContractJsonSerializer(typeof(RaidSettlementPayloadSummary));
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(detail.PayloadSummary));
            return serializer.ReadObject(stream) as RaidSettlementPayloadSummary;
        }
        catch (Exception ex) when (ex is SerializationException or InvalidOperationException)
        {
            Log.Warning(
                $"[ClashOfRim][EventLetter] raid settlement payload display parse failed event={detail.EventId} payloadLength={detail.PayloadSummary.Length} exception={ex}");
            return null;
        }
    }

    private static bool IsDefenderRaidTimeoutNotification(ModEventDetailDto detail, ServerNotificationPayloadSummary payload)
    {
        if (!payload.IsRaidTimeout)
        {
            return false;
        }

        return string.Equals(payload.RelatedEventType, "Raid", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(payload.RelatedUserId)
            && string.Equals(detail.Target?.UserId, payload.RelatedUserId, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(payload.RelatedColonyId)
                || string.Equals(detail.Target?.ColonyId, payload.RelatedColonyId, StringComparison.Ordinal));
    }

    private static bool IsDefenderRaidTimeoutNotification(ModEventDetailDto detail)
    {
        if (!string.Equals(detail.EventType, "ServerNotification", StringComparison.Ordinal))
        {
            return false;
        }

        ServerNotificationPayloadSummary? payload = ReadServerNotificationPayloadForDisplay(detail);
        return payload is not null && IsDefenderRaidTimeoutNotification(detail, payload);
    }

    private static string BuildServerNotificationEventLetterLabel(ModEventDetailDto detail)
    {
        ServerNotificationPayloadSummary? payload = ReadServerNotificationPayloadForDisplay(detail);
        string? title = payload?.Title;
        return string.IsNullOrWhiteSpace(title)
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.Label.ServerNotification")
            : title!;
    }

    private static bool IsReadOnlyEventGroup(string group)
    {
        return string.Equals(group, "Rejected", StringComparison.Ordinal)
            || string.Equals(group, "DeliveredUnconfirmed", StringComparison.Ordinal)
            || string.Equals(group, "Conflicts", StringComparison.Ordinal);
    }

    private static string BuildReadOnlyEventLetterText(ModEventDetailDto detail, string group)
    {
        string status = group switch
        {
            "Rejected" => ClashOfRimText.Key("ClashOfRim.EventLetter.ReadOnlyRejected"),
            "DeliveredUnconfirmed" => ClashOfRimText.Key("ClashOfRim.EventLetter.ReadOnlyDeliveredUnconfirmed"),
            "Conflicts" => ClashOfRimText.Key("ClashOfRim.EventLetter.ReadOnlyConflict"),
            _ => ClashOfRimText.Key("ClashOfRim.EventLetter.GenericAction")
        };
        string type = ClashOfRimText.Key(
            "ClashOfRim.EventLetter.ReadOnlyType",
            FormatEventType(detail.EventType).Named("TYPE"));
        string targetText = Prefs.DevMode && ShouldShowLetterLocation(detail)
            ? BuildTargetText(detail)
            : "";
        string devText = !Prefs.DevMode
            ? ""
            : "\n\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.DebugHeading")
            + "\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.ServerEventLine", detail.EventId.Named("EVENTID"))
            + "\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.StatusLine", detail.Status.Named("STATUS"))
            + "\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.GroupLine", FormatEventGroupName(group).Named("GROUP"));

        return status + "\n\n" + type + targetText + devText;
    }

    private static ServerNotificationPayloadSummary? ReadServerNotificationPayloadForDisplay(ModEventDetailDto detail)
    {
        if (string.IsNullOrWhiteSpace(detail.PayloadSummary))
        {
            return null;
        }

        try
        {
            var serializer = new DataContractJsonSerializer(typeof(ServerNotificationPayloadSummary));
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(detail.PayloadSummary));
            return serializer.ReadObject(stream) as ServerNotificationPayloadSummary;
        }
        catch (Exception ex) when (ex is SerializationException or InvalidOperationException)
        {
            Log.Warning(
                $"[ClashOfRim][EventLetter] server notification payload display parse failed event={detail.EventId} payloadLength={detail.PayloadSummary.Length} exception={ex}");
            return null;
        }
    }

    private static string BuildGiftItemListText(ModEventDetailDto detail)
    {
        GiftPayloadSummary? payload = ReadGiftPayloadForDisplay(detail);
        return payload is null
            ? "- " + ClashOfRimText.Key("ClashOfRim.EventLetter.GiftItemsUnavailable")
            : BuildGiftItemListText(payload);
    }

    private static GiftPayloadSummary? ReadGiftPayloadForDisplay(ModEventDetailDto detail)
    {
        if (string.IsNullOrWhiteSpace(detail.PayloadSummary))
        {
            return null;
        }

        try
        {
            return GiftPayloadReader.Read(detail.PayloadSummary);
        }
        catch (Exception ex) when (ex is SerializationException or InvalidOperationException)
        {
            Log.Warning(
                $"[ClashOfRim][EventLetter] gift payload display parse failed event={detail.EventId} payloadLength={detail.PayloadSummary.Length} exception={ex}");
            return null;
        }
    }

    private static SupportPawnPayloadSummary? ReadSupportPawnPayloadForDisplay(ModEventDetailDto detail)
    {
        if (string.IsNullOrWhiteSpace(detail.PayloadSummary))
        {
            return null;
        }

        try
        {
            return SupportPawnPayloadReader.Read(detail.PayloadSummary);
        }
        catch (Exception ex) when (ex is SerializationException or InvalidOperationException)
        {
            Log.Warning(
                $"[ClashOfRim][EventLetter] support pawn payload display parse failed event={detail.EventId} payloadLength={detail.PayloadSummary.Length} exception={ex}");
            return null;
        }
    }

    private static bool IsForcedGiftDetail(ModEventDetailDto detail)
    {
        return GiftClientProcessor.IsGiftDetail(detail)
            && ReadGiftPayloadForDisplay(detail)?.IsForcedDelivery == true;
    }

    private static bool IsTradeDeliveryDetail(ModEventDetailDto detail)
    {
        return GiftClientProcessor.IsGiftDetail(detail)
            && ReadGiftPayloadForDisplay(detail)?.IsTradeDelivery == true;
    }

    private static bool IsTradeReturnDetail(ModEventDetailDto detail)
    {
        return GiftClientProcessor.IsGiftDetail(detail)
            && ReadGiftPayloadForDisplay(detail)?.IsTradeReturn == true;
    }

    private static string BuildGiftItemListText(GiftPayloadSummary payload)
    {
        if (payload.Items.Count == 0)
        {
            return "- " + ClashOfRimText.Key("ClashOfRim.EventLetter.GiftItemsEmpty");
        }

        return string.Join("\n", payload.Items.Select(item => "- " + FormatGiftItem(item)));
    }

    private static string FormatGiftItem(GiftItemSummary item)
    {
        ThingDef? def = TradeUiUtility.ResolveThingDef(item.MinifiedInnerDefName)
            ?? TradeUiUtility.ResolveThingDef(item.Def);
        string label = !string.IsNullOrWhiteSpace(item.DisplayLabel)
            ? item.DisplayLabel!
            : def?.label?.CapitalizeFirst() ?? item.Def ?? ClashOfRimText.Key("ClashOfRim.UnknownItem");
        int stackCount = Math.Max(1, item.StackCount);
        string countText = stackCount > 1 ? " x" + stackCount.ToString(CultureInfo.InvariantCulture) : string.Empty;
        string condition = FormatGiftItemCondition(item, def);
        return label + countText + condition;
    }

    private static string FormatGiftItemCondition(GiftItemSummary item, ThingDef? def)
    {
        List<string> parts = new();
        string? quality = item.MinifiedInnerQuality ?? item.Quality;
        if (!string.IsNullOrWhiteSpace(quality))
        {
            parts.Add(ClashOfRimText.Key("ClashOfRim.ItemQuality", quality.Named("QUALITY")));
        }

        int? hitPoints = item.MinifiedInnerHitPoints ?? item.HitPoints;
        if (hitPoints.HasValue)
        {
            parts.Add(TradeUiUtility.FormatHitPointsPercent(hitPoints.Value, def));
        }

        if (item.WornByCorpse == true)
        {
            parts.Add(ClashOfRimText.Key("ClashOfRim.ItemWornByCorpse"));
        }

        if (item.Biocoded == true)
        {
            parts.Add(string.IsNullOrWhiteSpace(item.BiocodedPawnLabel)
                ? ClashOfRimText.Key("ClashOfRim.ItemBiocoded")
                : ClashOfRimText.Key("ClashOfRim.ItemBiocodedTo", item.BiocodedPawnLabel.Named("PAWN")));
        }

        if (item.UniqueWeapon == true)
        {
            parts.Add(ClashOfRimText.Key(WeaponTraitLabelKey(item.Metadata, def)));
            string[] traits = item.UniqueWeaponTraits
                .Where(trait => !string.IsNullOrWhiteSpace(trait))
                .ToArray();
            if (traits.Length > 0)
            {
                parts.Add(ClashOfRimText.Key("ClashOfRim.ItemUniqueWeaponTraits", string.Join("/", traits).Named("TRAITS")));
            }
        }

        return parts.Count == 0
            ? string.Empty
            : " (" + string.Join(ClashOfRimText.Key("ClashOfRim.ListSeparator"), parts) + ")";
    }

    private static string WeaponTraitLabelKey(IReadOnlyDictionary<string, string?>? metadata, ThingDef? def)
    {
        string? kind = null;
        if (metadata is not null
            && metadata.TryGetValue(TradeThingReferenceUtility.WeaponTraitKindMetadataKey, out string? value)
            && !string.IsNullOrWhiteSpace(value))
        {
            kind = value;
        }

        kind ??= TradeThingReferenceUtility.WeaponTraitKind(def);
        return kind switch
        {
            TradeThingReferenceUtility.WeaponTraitKindPersona => "ClashOfRim.ItemPersonaWeapon",
            TradeThingReferenceUtility.WeaponTraitKindSpecialized => "ClashOfRim.ItemSpecializedWeapon",
            _ => "ClashOfRim.ItemSpecializedWeapon"
        };
    }

    private static string FormatEventParty(ModProtocolIdentityDto? party)
    {
        if (party is null || string.IsNullOrWhiteSpace(party.UserId))
        {
            return ClashOfRimText.Key("ClashOfRim.UnknownOtherPlayer");
        }

        return string.IsNullOrWhiteSpace(party.ColonyId)
            ? party.UserId
            : party.UserId + "/" + party.ColonyId;
    }

    private static string ResolveSupportPawnLabel(SupportPawnPayloadSummary? payload)
    {
        if (!string.IsNullOrWhiteSpace(payload?.PawnName))
        {
            return payload!.PawnName!;
        }

        if (!string.IsNullOrWhiteSpace(payload?.PawnPackage?.Appearance?.DisplayName))
        {
            return payload!.PawnPackage!.Appearance!.DisplayName!;
        }

        return ClashOfRimText.Key("ClashOfRim.EventLetter.SupportUnknownPawn");
    }

    private static string FormatSupportDuration(SupportPawnPayloadSummary? payload)
    {
        if (payload?.PermanentSupport == true)
        {
            return ClashOfRimText.Key("ClashOfRim.EventLetter.SupportDurationPermanent");
        }

        int days = Math.Max(1, payload?.SupportDurationDays ?? 0);
        return ClashOfRimText.Key(
            "ClashOfRim.EventLetter.SupportDurationDays",
            days.Named("DAYS"));
    }

    private static string BuildEventActionText(ModEventDetailDto detail, string group)
    {
        if (string.Equals(group, "DeliveredUnconfirmed", StringComparison.Ordinal))
        {
            return ClashOfRimText.Key("ClashOfRim.EventLetter.ReadOnlyDeliveredUnconfirmed");
        }

        if (string.Equals(detail.EventType, "Gift", StringComparison.Ordinal))
        {
            return ClashOfRimText.Key("ClashOfRim.EventLetter.GiftAction");
        }

        if (string.Equals(detail.EventType, "GiftReturn", StringComparison.Ordinal))
        {
            return ClashOfRimText.Key("ClashOfRim.EventLetter.GiftReturnAction");
        }

        if (string.Equals(detail.EventType, "Raid", StringComparison.Ordinal))
        {
            return ClashOfRimText.Key("ClashOfRim.EventLetter.RaidAction");
        }

        if (string.Equals(detail.EventType, "Trade", StringComparison.Ordinal))
        {
            return ClashOfRimText.Key("ClashOfRim.EventLetter.TradeAction");
        }

        return ClashOfRimText.Key("ClashOfRim.EventLetter.GenericAction");
    }

    private static bool IsDiplomacyEvent(ModEventDetailDto detail)
    {
        return string.Equals(detail.EventType, "AllianceRequest", StringComparison.Ordinal)
            || string.Equals(detail.EventType, "AllianceCancellation", StringComparison.Ordinal)
            || string.Equals(detail.EventType, "WarDeclaration", StringComparison.Ordinal)
            || string.Equals(detail.EventType, "PeaceRequest", StringComparison.Ordinal);
    }

    private static bool IsRejectableDiplomacyEvent(ModEventDetailDto detail)
    {
        return string.Equals(detail.EventType, "AllianceRequest", StringComparison.Ordinal)
            || string.Equals(detail.EventType, "PeaceRequest", StringComparison.Ordinal);
    }

    private static bool IsImmediateDiplomacyEvent(ModEventDetailDto detail)
    {
        return string.Equals(detail.EventType, "WarDeclaration", StringComparison.Ordinal)
            || string.Equals(detail.EventType, "AllianceCancellation", StringComparison.Ordinal);
    }

    private static FactionRelationKind ResolveDiplomacyRelationKind(string eventType)
    {
        return eventType switch
        {
            "AllianceRequest" => FactionRelationKind.Ally,
            "AllianceCancellation" => FactionRelationKind.Neutral,
            "WarDeclaration" => FactionRelationKind.Hostile,
            "PeaceRequest" => FactionRelationKind.Neutral,
            _ => FactionRelationKind.Neutral
        };
    }

    private static bool TryResolveServerRelationKind(string? relationKind, out FactionRelationKind resolved)
    {
        if (string.Equals(relationKind, "Ally", StringComparison.OrdinalIgnoreCase))
        {
            resolved = FactionRelationKind.Ally;
            return true;
        }

        if (string.Equals(relationKind, "Hostile", StringComparison.OrdinalIgnoreCase))
        {
            resolved = FactionRelationKind.Hostile;
            return true;
        }

        if (string.Equals(relationKind, "Neutral", StringComparison.OrdinalIgnoreCase))
        {
            resolved = FactionRelationKind.Neutral;
            return true;
        }

        resolved = FactionRelationKind.Neutral;
        return false;
    }

    private static string DiplomacyRelationReason(string eventType)
    {
        return eventType switch
        {
            "WarDeclaration" => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationReasonWarDeclaration"),
            "AllianceCancellation" => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationReasonAllianceCancellation"),
            "AllianceRequest" => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationReasonAllianceRequest"),
            "PeaceRequest" => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationReasonPeaceRequest"),
            _ => "ClashOfRim"
        };
    }

    private static bool ShouldShowLetterLocation(ModEventDetailDto detail)
    {
        return !string.Equals(detail.EventType, "Gift", StringComparison.Ordinal)
            && !string.Equals(detail.EventType, "GiftReturn", StringComparison.Ordinal)
            && !string.Equals(detail.EventType, "SupportPawn", StringComparison.Ordinal);
    }

    private static string BuildTargetText(ModEventDetailDto detail)
    {
        List<string> parts = new();
        ModEventTargetContextDto? targetContext = detail.TargetContext;
        string? worldObjectId = targetContext?.WorldObjectId;
        if (!string.IsNullOrWhiteSpace(worldObjectId))
        {
            parts.Add(ClashOfRimText.Key("ClashOfRim.TargetWorldObject", worldObjectId.Named("ID")));
        }

        string? mapUniqueId = targetContext?.MapUniqueId;
        if (!string.IsNullOrWhiteSpace(mapUniqueId))
        {
            parts.Add(ClashOfRimText.Key("ClashOfRim.TargetMap", mapUniqueId.Named("ID")));
        }

        if (targetContext?.Tile is int tile)
        {
            parts.Add(ClashOfRimText.Key("ClashOfRim.TargetTile", tile.Named("TILE")));
        }

        return parts.Count == 0
            ? ""
            : "\n" + ClashOfRimText.Key("ClashOfRim.Target", string.Join(ClashOfRimText.Key("ClashOfRim.ListSeparator"), parts).Named("TARGET"));
    }

    private static LookTargets? BuildEventLookTargets(ModEventDetailDto detail)
    {
        if (IsRaidSettlementDetail(detail)
            && TryBuildRaidSettlementLookTargets(detail, out LookTargets? raidSettlementTargets))
        {
            return raidSettlementTargets;
        }

        Map? map = Find.CurrentMap;
        if (map is not null
            && MapIdsMatch($"Map_{map.uniqueID}", detail.TargetContext?.MapUniqueId))
        {
            return new LookTargets(map.Center, map);
        }

        return null;
    }

    private static bool TryBuildRaidSettlementLookTargets(ModEventDetailDto detail, out LookTargets? lookTargets)
    {
        lookTargets = null;
        Map? map = Find.CurrentMap;
        if (map is null)
        {
            return false;
        }

        RaidSettlementPayloadSummary? payload = ReadRaidSettlementPayloadForDisplay(detail);
        if (payload?.Settlement?.Losses is null || payload.Settlement.Losses.Count == 0)
        {
            return false;
        }

        var targets = new List<TargetInfo>();
        foreach (RaidSettlementLossPayloadSummary loss in payload.Settlement.Losses)
        {
            if (!MapIdsMatch($"Map_{map.uniqueID}", loss.MapUniqueId ?? detail.TargetContext?.MapUniqueId)
                || !TryParseLossCell(loss.Position, out IntVec3 cell)
                || !cell.InBounds(map))
            {
                continue;
            }

            targets.Add(new TargetInfo(cell, map));
            if (targets.Count >= 8)
            {
                break;
            }
        }

        if (targets.Count == 0)
        {
            return false;
        }

        lookTargets = new LookTargets(targets);
        return true;
    }

    private static bool TryParseLossCell(string? value, out IntVec3 cell)
    {
        cell = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value!.Trim().Trim('(', ')').Split(',');
        if (parts.Length < 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
            || !int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int z))
        {
            return false;
        }

        cell = new IntVec3(x, y, z);
        return true;
    }

    private static bool MapIdsMatch(string currentMapId, string? targetMapId)
    {
        if (string.IsNullOrWhiteSpace(targetMapId))
        {
            return false;
        }

        string normalizedTarget = targetMapId!.StartsWith("Map_", StringComparison.Ordinal)
            ? targetMapId
            : "Map_" + targetMapId;
        return string.Equals(currentMapId, normalizedTarget, StringComparison.Ordinal);
    }

    private static string FormatEventType(string eventType)
    {
        return eventType switch
        {
            "Raid" => ClashOfRimText.Key("ClashOfRim.EventType.Raid"),
            "Gift" => ClashOfRimText.Key("ClashOfRim.EventType.Gift"),
            "GiftReturn" => ClashOfRimText.Key("ClashOfRim.EventType.GiftReturn"),
            "Trade" => ClashOfRimText.Key("ClashOfRim.EventType.Trade"),
            "SupportPawn" => ClashOfRimText.Key("ClashOfRim.EventType.SupportPawn"),
            "AllianceRequest" => ClashOfRimText.Key("ClashOfRim.EventType.AllianceRequest"),
            "AllianceCancellation" => ClashOfRimText.Key("ClashOfRim.EventType.AllianceCancellation"),
            "WarDeclaration" => ClashOfRimText.Key("ClashOfRim.EventType.WarDeclaration"),
            "PeaceRequest" => ClashOfRimText.Key("ClashOfRim.EventType.PeaceRequest"),
            "ServerNotification" => ClashOfRimText.Key("ClashOfRim.EventType.ServerNotification"),
            _ => string.IsNullOrWhiteSpace(eventType) ? ClashOfRimText.Key("ClashOfRim.Unknown") : eventType
        };
    }

    private static string FormatDiplomacyKind(string kind)
    {
        return kind switch
        {
            "AllianceRequest" => ClashOfRimText.Key("ClashOfRim.EventType.AllianceRequest"),
            "AllianceCancellation" => ClashOfRimText.Key("ClashOfRim.EventType.AllianceCancellation"),
            "SupportRequest" => ClashOfRimText.Key("ClashOfRim.EventType.SupportRequest"),
            "WarDeclaration" => ClashOfRimText.Key("ClashOfRim.EventType.WarDeclaration"),
            "PeaceRequest" => ClashOfRimText.Key("ClashOfRim.EventType.PeaceRequest"),
            _ => kind
        };
    }

    private static string FormatDiplomacyRelationKind(FactionRelationKind relationKind)
    {
        return relationKind switch
        {
            FactionRelationKind.Ally => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationAlly"),
            FactionRelationKind.Hostile => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationHostile"),
            _ => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationNeutral")
        };
    }

    private static string DefaultDiplomacyMessage(string kind)
    {
        return kind switch
        {
            "AllianceRequest" => ClashOfRimText.Key("ClashOfRim.EventLetter.DiplomacyDefaultAllianceRequest"),
            "AllianceCancellation" => ClashOfRimText.Key("ClashOfRim.EventLetter.DiplomacyDefaultAllianceCancellation"),
            "SupportRequest" => ClashOfRimText.Key("ClashOfRim.EventLetter.DiplomacyDefaultSupportRequest"),
            "WarDeclaration" => ClashOfRimText.Key("ClashOfRim.EventLetter.DiplomacyDefaultWarDeclaration"),
            "PeaceRequest" => ClashOfRimText.Key("ClashOfRim.EventLetter.DiplomacyDefaultPeaceRequest"),
            _ => ClashOfRimText.Key("ClashOfRim.EventLetter.DiplomacyDefaultGeneric")
        };
    }
}
[DataContract]
internal sealed class ServerNotificationPayloadSummary
{
    [DataMember(Name = "NotificationId")]
    public string? NotificationId { get; set; }

    [DataMember(Name = "Title")]
    public string? Title { get; set; }

    [DataMember(Name = "Message")]
    public string? Message { get; set; }

    [DataMember(Name = "Severity")]
    public int? Severity { get; set; }

    [DataMember(Name = "RelatedEventId")]
    public string? RelatedEventId { get; set; }

    [DataMember(Name = "RelatedEventType")]
    public string? RelatedEventType { get; set; }

    [DataMember(Name = "RelatedUserId")]
    public string? RelatedUserId { get; set; }

    [DataMember(Name = "RelatedColonyId")]
    public string? RelatedColonyId { get; set; }

    [DataMember(Name = "RelatedAccepted")]
    public bool? RelatedAccepted { get; set; }

    public bool IsRaidTimeout =>
        !string.IsNullOrWhiteSpace(NotificationId)
        && NotificationId!.StartsWith("raid-timeout:", StringComparison.Ordinal);
}

[DataContract]
internal sealed class RaidSettlementPayloadSummary
{
    [DataMember(Name = "Settlement")]
    public RaidSettlementPayloadDetail? Settlement { get; set; }
}

[DataContract]
internal sealed class RaidSettlementPayloadDetail
{
    [DataMember(Name = "Losses")]
    public List<RaidSettlementLossPayloadSummary> Losses { get; set; } = new();
}

[DataContract]
internal sealed class RaidSettlementLossPayloadSummary
{
    [DataMember(Name = "Def")]
    public string? Def { get; set; }

    [DataMember(Name = "Position")]
    public string? Position { get; set; }

    [DataMember(Name = "MapUniqueId")]
    public string? MapUniqueId { get; set; }

    [DataMember(Name = "WholeThingMissing")]
    public bool WholeThingMissing { get; set; }

    [DataMember(Name = "LossCount")]
    public int LossCount { get; set; }

    [DataMember(Name = "RemainingHitPointsAfterDamage")]
    public int? RemainingHitPointsAfterDamage { get; set; }
}
