using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Bank;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.WorldObjects;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    private static string FormatEventQueue(ModEventQueueSummaryDto? queue)
    {
        if (queue is null)
        {
            return ClashOfRimText.Key("ClashOfRim.EventQueue.Empty");
        }

        return string.Join(ClashOfRimText.Key("ClashOfRim.ListSeparator"), new[]
        {
            FormatEventGroup(ClashOfRimText.Key("ClashOfRim.EventGroup.DirectlyProcessable"), queue.DirectlyProcessable),
            FormatEventGroup(ClashOfRimText.Key("ClashOfRim.EventGroup.WaitingForUserChoice"), queue.WaitingForUserChoice),
            FormatEventGroup(ClashOfRimText.Key("ClashOfRim.EventGroup.DeliveredUnconfirmed"), queue.DeliveredUnconfirmed),
            FormatEventGroup(ClashOfRimText.Key("ClashOfRim.EventGroup.Conflicts"), queue.Conflicts),
            FormatEventGroup(ClashOfRimText.Key("ClashOfRim.EventGroup.Rejected"), queue.Rejected)
        });
    }

    private static string FormatPlayers(IReadOnlyCollection<ModPlayerSummaryDto>? players)
    {
        if (players is null || players.Count == 0)
        {
            return ClashOfRimText.Key("ClashOfRim.Players.NoneRegistered");
        }

        return ClashOfRimText.Key("ClashOfRim.Players.SummaryPrefix") + string.Join(ClashOfRimText.Key("ClashOfRim.ListSeparator"), players
            .OrderByDescending(player => player.Online)
            .ThenBy(player => player.UserId, StringComparer.Ordinal)
            .Take(8)
            .Select(player =>
            {
                string online = player.Online
                    ? ClashOfRimText.Key("ClashOfRim.Multiplayer.Online")
                    : ClashOfRimText.Key("ClashOfRim.Multiplayer.Offline");
                string playerName = string.IsNullOrWhiteSpace(player.DisplayName)
                    ? player.UserId
                    : player.DisplayName!;
                return ClashOfRimText.Key(
                    "ClashOfRim.Players.SummaryItem",
                    ClashOfRimText.SafeArgument(playerName).Named("PLAYER"),
                    online.Named("STATUS"));
            }));
    }

    private bool HasTarget(out string failureReason)
    {
        if (string.IsNullOrWhiteSpace(settings.TargetUserId)
            || string.IsNullOrWhiteSpace(settings.TargetColonyId))
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Target.StatusMissing");
            return false;
        }

        if (string.Equals(settings.TargetUserId, settings.UserId, StringComparison.Ordinal)
            && string.Equals(settings.TargetColonyId, settings.ColonyId, StringComparison.Ordinal))
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Target.StatusSelf");
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static string FormatCreationResult(
        ClashOfRimClientNetworkResult<ModEventCreationResponseDto> created,
        string label)
    {
        if (!created.Success || created.Response is null)
        {
            return ClashOfRimText.Key(
                "ClashOfRim.CreationResult.Failed",
                label.Named("LABEL"),
                (created.ErrorCode ?? string.Empty).Named("CODE"),
                (created.Message ?? string.Empty).Named("MESSAGE"));
        }

        if (created.Response.Result is not null && !created.Response.Result.Accepted)
        {
            return ClashOfRimText.Key(
                "ClashOfRim.CreationResult.Rejected",
                label.Named("LABEL"),
                created.Response.Result.ErrorCode.Named("CODE"),
                (created.Response.Result.Message ?? string.Empty).Named("MESSAGE"));
        }

        return ClashOfRimText.Key(
            "ClashOfRim.CreationResult.Created",
            label.Named("LABEL"));
    }

    private void CaptureEventIds(ModEventQueueSummaryDto? queue)
    {
        lock (eventStateLock)
        {
            lastEventQueueEventIds.Clear();
            lastEventReferences.Clear();
            lastEventReferenceGroups.Clear();
            if (queue is null)
            {
                return;
            }

            CaptureEventReferenceGroup(queue.DirectlyProcessable, "DirectlyProcessable");
            CaptureEventReferenceGroup(queue.WaitingForUserChoice, "WaitingForUserChoice");
            CaptureEventReferenceGroup(queue.DeliveredUnconfirmed, "DeliveredUnconfirmed");
            CaptureEventReferenceGroup(queue.Conflicts, "Conflicts");
            CaptureEventReferenceGroup(queue.Rejected, "Rejected");
        }
    }

    private void CaptureWorldMapMarkers(
        ModWorldMapMarkerDeliveryDto? delivery,
        bool applyImmediately = false,
        string? source = null)
    {
        List<ModWorldMapMarkerDto> markers = delivery?.Markers?.ToList() ?? new List<ModWorldMapMarkerDto>();
        lock (eventStateLock)
        {
            lastWorldMapMarkers.Clear();
            lastWorldMapMarkers.AddRange(markers);
            giftsEnabled = delivery?.GiftsEnabled ?? true;
            pvpEnabled = delivery?.PvpEnabled ?? true;
        }

        MergeOccupiedPlayerColonySitesFromWorldMapMarkers(markers);
        string sourceLabel = string.IsNullOrWhiteSpace(source)
            ? "unspecified"
            : source!;
        ClashLog.Message("[ClashOfRim] Captured world map markers from "
            + sourceLabel
            + ": "
            + DescribeWorldMapMarkerDelivery(delivery)
            + ", "
            + DescribeWorldMapMarkerSample(markers));
        if (applyImmediately)
        {
            worldMapStatus = FormatWorldMapMarkers(markers);
            RemoteWorldObjectApplyResult applyResult =
                RemoteRuntimeWorldObjectRegistry.Apply(markers, settings.UserId);
                ClashLog.Message("[ClashOfRim] Applied world map markers immediately from " + sourceLabel + ": " + applyResult);
        }
        else if (Find.WorldObjects is not null)
        {
            EnqueueClashOfRimMainThreadAction(() =>
            {
                worldMapStatus = FormatWorldMapMarkers(markers);
                RemoteWorldObjectApplyResult applyResult =
                    RemoteRuntimeWorldObjectRegistry.Apply(markers, settings.UserId);
                ClashLog.Message("[ClashOfRim] Applied world map markers on main thread from " + sourceLabel + ": " + applyResult);
            });
        }
        else
        {
            EnqueueClashOfRimMainThreadAction(() =>
            {
                worldMapStatus = FormatWorldMapMarkers(markers);
            });
            Log.Warning("[ClashOfRim] Captured world map markers but Find.WorldObjects is null; cached markers will be applied when the world page is ready.");
        }
    }

    internal void ApplyCachedWorldMapMarkersToWorldObjects()
    {
        if (Find.WorldObjects is null || string.IsNullOrWhiteSpace(settings.UserId))
        {
            return;
        }

        List<ModWorldMapMarkerDto> markers;
        lock (eventStateLock)
        {
            if (lastWorldMapMarkers.Count == 0)
            {
                return;
            }

            markers = lastWorldMapMarkers.ToList();
        }

        RemoteWorldObjectApplyResult applyResult =
            RemoteRuntimeWorldObjectRegistry.Apply(markers, settings.UserId);
        if (applyResult.Created > 0 || applyResult.Removed > 0 || applyResult.Failed > 0)
        {
            ClashLog.Message("[ClashOfRim] Applied cached world map markers on starting-site GUI: " + applyResult);
        }
    }

    private static string FormatWorldMapMarkers(IReadOnlyCollection<ModWorldMapMarkerDto>? markers)
    {
        if (markers is null || markers.Count == 0)
        {
            return ClashOfRimText.Key("ClashOfRim.WorldMap.StatusNoMarkers");
        }

        string summary = string.Join("；", markers
            .OrderBy(marker => marker.Tile)
            .ThenBy(marker => marker.OwnerUserId, StringComparer.Ordinal)
            .Take(6)
            .Select(marker =>
            {
                string label = string.IsNullOrWhiteSpace(marker.Label)
                    ? marker.OwnerUserId
                    : marker.Label!;
                var actions = new List<string>();
                if (marker.CanTrade)
                {
                    actions.Add(ClashOfRimText.Key("ClashOfRim.WorldMap.ActionTrade"));
                }

                if (marker.CanRaid)
                {
                    actions.Add(ClashOfRimText.Key("ClashOfRim.WorldMap.ActionRaid"));
                }

                if (marker.CanReinforce)
                {
                    actions.Add(ClashOfRimText.Key("ClashOfRim.WorldMap.ActionReinforce"));
                }

                string actionText = actions.Count == 0 ? ClashOfRimText.Key("ClashOfRim.WorldMap.ActionNone") : string.Join("/", actions);
                return $"{label}@{marker.Tile} {actionText}";
            }));

        return ClashOfRimText.Key(
            "ClashOfRim.WorldMap.StatusMarkerSummary",
            markers.Count.Named("COUNT"),
            summary.Named("SUMMARY"));
    }

    private void CaptureEventReferenceGroup(
        IReadOnlyCollection<ModEventReferenceDto>? events,
        string groupName)
    {
        if (events is null)
        {
            return;
        }

        foreach (ModEventReferenceDto eventReference in events)
        {
            if (string.IsNullOrWhiteSpace(eventReference.EventId))
            {
                continue;
            }

            if (!lastEventReferences.ContainsKey(eventReference.EventId))
            {
                lastEventReferences.Add(eventReference.EventId, eventReference);
                lastEventQueueEventIds.Add(eventReference.EventId);
            }

            lastEventReferenceGroups[eventReference.EventId] = groupName;
        }
    }

    private IReadOnlyList<string> CopyLastEventQueueIds()
    {
        lock (eventStateLock)
        {
            return lastEventQueueEventIds.ToList();
        }
    }

    private void CaptureEventDetails(IReadOnlyCollection<ModEventDetailDto>? details)
    {
        lock (eventStateLock)
        {
            lastEventDetails.Clear();
            if (details is null)
            {
                return;
            }

            lastEventDetails.AddRange(details);
        }
    }

    private static string FormatEventGroupName(string group)
    {
        return group switch
        {
            "DirectlyProcessable" => ClashOfRimText.Key("ClashOfRim.EventGroup.DirectlyProcessable"),
            "WaitingForUserChoice" => ClashOfRimText.Key("ClashOfRim.EventGroup.WaitingForUserChoice"),
            "DeliveredUnconfirmed" => ClashOfRimText.Key("ClashOfRim.EventGroup.DeliveredUnconfirmed"),
            "Conflicts" => ClashOfRimText.Key("ClashOfRim.EventGroup.Conflicts"),
            "Rejected" => ClashOfRimText.Key("ClashOfRim.EventGroup.Rejected"),
            _ => group
        };
    }

    private static string Preview(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "<empty>";
        }

        string compact = value!.Replace("\r", string.Empty).Replace("\n", string.Empty);
        return compact.Length <= maxLength ? compact : compact.Substring(0, maxLength) + "...";
    }

    private static string FormatEventGroup(string label, IReadOnlyCollection<ModEventReferenceDto>? events)
    {
        if (events is null || events.Count == 0)
        {
            return ClashOfRimText.Key("ClashOfRim.EventQueue.GroupCount", label.Named("GROUP"), 0.Named("COUNT"));
        }

        string sample = string.Join(", ", events.Take(3).Select(FormatEventReference));
        return ClashOfRimText.Key("ClashOfRim.EventQueue.GroupSample", label.Named("GROUP"), events.Count.Named("COUNT"), sample.Named("SAMPLE"));
    }

    private static string FormatEventReference(ModEventReferenceDto eventReference)
    {
        string confirmation = eventReference.RequiresSnapshotConfirmation
            ? ClashOfRimText.Key("ClashOfRim.EventQueue.RequiresSnapshotConfirmation")
            : string.Empty;
        return ClashOfRimText.Key(
            "ClashOfRim.EventQueue.Item",
            FormatEventTypeForSummary(eventReference.EventType).Named("TYPE"),
            FormatEventStatus(eventReference.Status).Named("STATUS"),
            FormatDeliverySemantics(eventReference.DeliverySemantics).Named("DELIVERY"),
            confirmation.Named("CONFIRMATION"));
    }

    private static string FormatEventDetails(IReadOnlyCollection<ModEventDetailDto>? details)
    {
        if (details is null || details.Count == 0)
        {
            return ClashOfRimText.Key("ClashOfRim.EventDetails.Empty");
        }

        string sample = string.Join(ClashOfRimText.Key("ClashOfRim.ListSeparator"), details.Take(3).Select(FormatEventDetail));
        return ClashOfRimText.Key("ClashOfRim.EventDetails.Summary", details.Count.Named("COUNT"), sample.Named("SAMPLE"));
    }

    private static string FormatEventDetail(ModEventDetailDto detail)
    {
        string map = string.IsNullOrWhiteSpace(detail.TargetContext?.MapUniqueId)
            ? string.Empty
            : ClashOfRimText.Key("ClashOfRim.EventDetails.MapSuffix");
        return ClashOfRimText.Key(
            "ClashOfRim.EventDetails.Item",
            FormatEventTypeForSummary(detail.EventType).Named("TYPE"),
            FormatEventStatus(detail.Status).Named("STATUS"),
            map.Named("MAP"));
    }

    private static string FormatGiftProcessingResult(ItemDeliveryClientProcessingResult result)
    {
        if (result.LandingPlan is not null)
        {
            return ClashOfRimText.Key(
                "ClashOfRim.GiftProcessing.AcceptPlan",
                result.LandingPlan.Items.Count.Named("COUNT"));
        }

        if (result.RejectionRequest is not null)
        {
            return ClashOfRimText.Key("ClashOfRim.GiftProcessing.RejectPlan");
        }

        return ClashOfRimText.Key("ClashOfRim.GiftProcessing.Failed", result.Kind.Named("KIND"), result.Message.Named("MESSAGE"));
    }

    private string FormatGiftLandingApplicationResult(GiftLandingApplicationResult result)
    {
        if (result.Success)
        {
            return ClashOfRimText.Key(
                "ClashOfRim.GiftLanding.StatusSucceeded",
                FormatGiftLandingMode(result.LandingMode).Named("MODE"),
                result.PlacedThingCount.Named("THINGCOUNT"),
                result.PlacedStackCount.Named("STACKCOUNT"),
                pendingGiftConfirmationEventIds.Count.Named("PENDING"));
        }

        return ClashOfRimText.Key("ClashOfRim.GiftLanding.StatusFailed", result.Kind.Named("KIND"), result.Message.Named("MESSAGE"));
    }

    private static string FormatGiftLandingMode(string landingMode)
    {
        return landingMode switch
        {
            "DropPod" => ClashOfRimText.Key("ClashOfRim.GiftLanding.Mode.DropPod"),
            "MapEdge" => ClashOfRimText.Key("ClashOfRim.GiftLanding.Mode.MapEdge"),
            "CenterNear" => ClashOfRimText.Key("ClashOfRim.GiftLanding.Mode.CenterNear"),
            _ => string.IsNullOrWhiteSpace(landingMode) ? ClashOfRimText.Key("ClashOfRim.NotSpecified") : landingMode
        };
    }

    private static long? ExtractNotificationVersion(string data)
    {
        return ExtractJsonLong(data, "\"NotificationVersion\":");
    }

    private static long? ExtractWorldConfigurationVersion(string data)
    {
        return ExtractJsonLong(data, "\"WorldConfigurationVersion\":");
    }

    private static long? ExtractJsonLong(string data, string marker)
    {
        int start = data.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        while (start < data.Length && char.IsWhiteSpace(data[start]))
        {
            start++;
        }

        int end = start;
        while (end < data.Length && char.IsDigit(data[end]))
        {
            end++;
        }

        return end > start && long.TryParse(data.Substring(start, end - start), out long value)
            ? value
            : null;
    }

    private static string FormatDeliverySemantics(int deliverySemantics)
    {
        return deliverySemantics switch
        {
            0 => ClashOfRimText.Key("ClashOfRim.EventDelivery.OnlineImmediate"),
            1 => ClashOfRimText.Key("ClashOfRim.EventDelivery.OfflineQueued"),
            2 => ClashOfRimText.Key("ClashOfRim.EventDelivery.SnapshotConfirmation"),
            3 => ClashOfRimText.Key("ClashOfRim.EventDelivery.ServerNotification"),
            _ => deliverySemantics.ToString()
        };
    }

    private static string FormatEventTypeForSummary(ServerEventType eventType)
    {
        return eventType switch
        {
            ServerEventType.AllianceRequest => ClashOfRimText.Key("ClashOfRim.EventType.AllianceRequest"),
            ServerEventType.AllianceCancellation => ClashOfRimText.Key("ClashOfRim.EventType.AllianceCancellation"),
            ServerEventType.ItemDelivery => ClashOfRimText.Key("ClashOfRim.EventType.ItemDelivery"),
            ServerEventType.PeaceRequest => ClashOfRimText.Key("ClashOfRim.EventType.PeaceRequest"),
            ServerEventType.Raid => ClashOfRimText.Key("ClashOfRim.EventType.Raid"),
            ServerEventType.ServerNotification => ClashOfRimText.Key("ClashOfRim.EventType.ServerNotification"),
            ServerEventType.SupportPawn => ClashOfRimText.Key("ClashOfRim.EventType.SupportPawn"),
            ServerEventType.Trade => ClashOfRimText.Key("ClashOfRim.EventType.Trade"),
            ServerEventType.WarDeclaration => ClashOfRimText.Key("ClashOfRim.EventType.WarDeclaration"),
            _ => ClashOfRimText.Key("ClashOfRim.EventType.Unknown")
        };
    }

    private static string FormatEventStatus(string? status)
    {
        return status switch
        {
            "AppliedToSnapshot" => ClashOfRimText.Key("ClashOfRim.EventStatus.Handled"),
            "Cancelled" => ClashOfRimText.Key("ClashOfRim.EventStatus.Cancelled"),
            "Conflict" => ClashOfRimText.Key("ClashOfRim.EventStatus.Conflict"),
            "DeliveredToClient" => ClashOfRimText.Key("ClashOfRim.EventStatus.Waiting"),
            "Failed" => ClashOfRimText.Key("ClashOfRim.EventStatus.Failed"),
            "PendingOfflineDelivery" => ClashOfRimText.Key("ClashOfRim.EventStatus.Waiting"),
            "ReadyForImmediateDelivery" => ClashOfRimText.Key("ClashOfRim.EventStatus.Waiting"),
            "Recorded" => ClashOfRimText.Key("ClashOfRim.EventStatus.Waiting"),
            "RejectedByTarget" => ClashOfRimText.Key("ClashOfRim.EventStatus.Rejected"),
            _ => ClashOfRimText.Key("ClashOfRim.EventStatus.Unknown")
        };
    }
}
