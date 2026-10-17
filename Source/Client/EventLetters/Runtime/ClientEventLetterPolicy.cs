using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.Support;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.EventLetters;

// The game renderer, action execution and automatic application use these same rules.
internal static class ClientEventLetterPolicy
{
    internal static IReadOnlyList<ClashOfRimEventLetterActionKind> BuildEventLetterActions(
        ModEventDetailDto detail,
        bool hasLookTarget,
        string group)
    {
        var actions = new List<ClashOfRimEventLetterActionKind>();
        if (CanRunEventLetterAction(detail, ClashOfRimEventLetterActionKind.Accept, group))
        {
            actions.Add(ClashOfRimEventLetterActionKind.Accept);
        }

        if (CanRunEventLetterAction(detail, ClashOfRimEventLetterActionKind.Reject, group))
        {
            actions.Add(ClashOfRimEventLetterActionKind.Reject);
        }

        if (hasLookTarget)
        {
            actions.Add(ClashOfRimEventLetterActionKind.JumpToTarget);
        }

        if (actions.Count > 0)
        {
            actions.Add(ClashOfRimEventLetterActionKind.Postpone);
        }

        return actions;
    }

    internal static bool CanRunEventLetterAction(ModEventDetailDto detail, ClashOfRimEventLetterActionKind actionKind, string group)
    {
        if (!string.Equals(group, "WaitingForUserChoice", StringComparison.Ordinal)
            && !string.Equals(group, "DirectlyProcessable", StringComparison.Ordinal))
        {
            return false;
        }

        return actionKind switch
        {
            ClashOfRimEventLetterActionKind.Accept => CanAcceptEventFromLetter(detail),
            ClashOfRimEventLetterActionKind.Reject => CanRejectEventFromLetter(detail),
            _ => false
        };
    }

    internal static bool CanAutoApplyDirectlyProcessableEvent(ModEventDetailDto detail)
    {
        return ItemDeliveryClientProcessor.IsGiftDetail(detail)
            || SupportPawnApplicator.IsReturnToSenderDetail(detail)
            || IsRaidAttackerLossDetail(detail);
    }

    internal static bool IsRaidAttackerLossDetail(ModEventDetailDto detail)
    {
        return RaidAttackerLossPayloadReader.HasAttackerLoss(detail);
    }

    internal static bool IsRaidSettlementDetail(ModEventDetailDto detail)
    {
        return RaidSettlementPayloadReader.HasSettlement(detail);
    }

    internal static bool CanAcceptEventFromLetter(ModEventDetailDto detail)
    {
        return ItemDeliveryClientProcessor.IsGiftDetail(detail)
            || SupportPawnApplicator.IsSupportPawnDetail(detail)
            || IsRejectableDiplomacyEvent(detail);
    }

    internal static bool CanRejectEventFromLetter(ModEventDetailDto detail)
    {
        if (ItemDeliveryClientProcessor.IsGiftDetail(detail))
        {
            ItemDeliveryPayloadSummary? payload = ReadGiftPayloadForDisplay(detail);
            return payload?.Purpose == ItemDeliveryPurpose.Gift && !payload.IsForcedDelivery;
        }

        return SupportPawnApplicator.IsRejectableSupportPawnDetail(detail)
            || IsRejectableDiplomacyEvent(detail);
    }

    internal static LetterDef ResolveLetterDef(ModEventDetailDto detail)
    {
        if (detail.EventType == ServerEventType.ServerNotification)
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
            ServerEventType.Raid => LetterDefOf.ThreatBig,
            ServerEventType.WarDeclaration => LetterDefOf.ThreatBig,
            ServerEventType.ItemDelivery when IsForcedGiftDetail(detail) => LetterDefOf.NegativeEvent,
            ServerEventType.ItemDelivery when IsRejectedGiftReturnDetail(detail) => LetterDefOf.NeutralEvent,
            ServerEventType.ItemDelivery => LetterDefOf.PositiveEvent,
            ServerEventType.Trade => LetterDefOf.PositiveEvent,
            ServerEventType.SupportPawn => LetterDefOf.PositiveEvent,
            ServerEventType.AllianceRequest => LetterDefOf.NeutralEvent,
            ServerEventType.AllianceCancellation => LetterDefOf.NeutralEvent,
            ServerEventType.ServerNotification => LetterDefOf.NeutralEvent,
            ServerEventType.PeaceRequest => LetterDefOf.NeutralEvent,
            _ => LetterDefOf.NeutralEvent
        };
    }

    internal static ServerNotificationPayloadSummary? ReadServerNotificationPayloadForDisplay(ModEventDetailDto detail)
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

    internal static ItemDeliveryPayloadSummary? ReadGiftPayloadForDisplay(ModEventDetailDto detail)
    {
        if (string.IsNullOrWhiteSpace(detail.PayloadSummary))
        {
            return null;
        }

        try
        {
            return ItemDeliveryPayloadReader.Read(detail.PayloadSummary);
        }
        catch (Exception ex) when (ex is SerializationException or InvalidOperationException)
        {
            Log.Warning(
                $"[ClashOfRim][EventLetter] gift payload display parse failed event={detail.EventId} payloadLength={detail.PayloadSummary.Length} exception={ex}");
            return null;
        }
    }

    internal static bool IsForcedGiftDetail(ModEventDetailDto detail)
    {
        return ItemDeliveryClientProcessor.IsGiftDetail(detail)
            && ReadGiftPayloadForDisplay(detail)?.IsForcedDelivery == true;
    }

    internal static bool IsRejectedGiftReturnDetail(ModEventDetailDto detail)
    {
        return ItemDeliveryClientProcessor.IsGiftDetail(detail)
            && ReadGiftPayloadForDisplay(detail)?.Purpose == ItemDeliveryPurpose.RejectedGiftReturn;
    }

    internal static bool IsTradeDeliveryDetail(ModEventDetailDto detail)
    {
        return ItemDeliveryClientProcessor.IsGiftDetail(detail)
            && ReadGiftPayloadForDisplay(detail)?.IsTradeDelivery == true;
    }

    internal static bool IsTradeReturnDetail(ModEventDetailDto detail)
    {
        return ItemDeliveryClientProcessor.IsGiftDetail(detail)
            && ReadGiftPayloadForDisplay(detail)?.IsTradeReturn == true;
    }

    internal static bool IsRejectableDiplomacyEvent(ModEventDetailDto detail) =>
        AIRsLight.ClashOfRim.Modules.Events.ClientEventDiplomacyPolicy.CanRespond(detail.EventType);

    internal static bool ShouldPostEventLetter(ModEventDetailDto detail, string? group)
    {
        if (string.IsNullOrWhiteSpace(detail.EventId))
        {
            return false;
        }

        if (group is null)
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

        return group switch
        {
            "WaitingForUserChoice" or "Conflicts" or "Rejected" => true,
            "DirectlyProcessable" or "DeliveredUnconfirmed" => !CanAutoApplyDirectlyProcessableEvent(detail),
            _ => false
        };
    }

    internal static bool IsDefenderRaidTimeoutNotification(ModEventDetailDto detail, ServerNotificationPayloadSummary payload)
    {
        if (!payload.IsRaidTimeout)
        {
            return false;
        }

        return payload.RelatedEventType == ServerEventType.Raid
            && !string.IsNullOrWhiteSpace(payload.RelatedUserId)
            && string.Equals(detail.Target?.UserId, payload.RelatedUserId, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(payload.RelatedColonyId)
                || string.Equals(detail.Target?.ColonyId, payload.RelatedColonyId, StringComparison.Ordinal));
    }

    internal static bool IsDefenderRaidTimeoutNotification(ModEventDetailDto detail)
    {
        if (detail.EventType != ServerEventType.ServerNotification)
        {
            return false;
        }

        ServerNotificationPayloadSummary? payload = ReadServerNotificationPayloadForDisplay(detail);
        return payload is not null && IsDefenderRaidTimeoutNotification(detail, payload);
    }
}
