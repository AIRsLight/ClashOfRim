using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Protocol;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using static AIRsLight.ClashOfRim.EventLetters.ClientEventTextFormatting;
using AIRsLight.ClashOfRim.Modules.Events;
using Verse;

namespace AIRsLight.ClashOfRim.EventLetters;

internal sealed class ClientEventReadPresentation : IClientEventReadPresentation
{
    public string MergedReason => ClashOfRimText.Key("ClashOfRim.Events.ReasonMergedNotifications");
    public string ManualDetailsReason => ClashOfRimText.Key("ClashOfRim.Events.ReasonManualDetails");

    public string Format(ClientEventRefreshNotice notice) => notice.Stage switch
    {
        ClientEventRefreshStage.InitialQueue => ClashOfRimText.Key("ClashOfRim.Status.EventQueueNotPulled"),
        ClientEventRefreshStage.InitialAction => ClashOfRimText.Key("ClashOfRim.Status.GiftNotProcessed"),
        ClientEventRefreshStage.InitialDetails => ClashOfRimText.Key("ClashOfRim.Status.EventDetailsNotPulled"),
        ClientEventRefreshStage.AdmissionFailed => ClientLocalMutationPresentation.AdmissionFailure(notice.Admission, notice.Mutation),
        ClientEventRefreshStage.Busy => ClashOfRimText.Key("ClashOfRim.Sync.StatusBusy"),
        ClientEventRefreshStage.Merged => ClashOfRimText.Key("ClashOfRim.Events.StatusAutoMerged", notice.Reason.Named("REASON")),
        ClientEventRefreshStage.Deferred => notice.Status ?? string.Empty,
        ClientEventRefreshStage.Started when notice.Kind == ClientEventReadKind.Automatic =>
            ClashOfRimText.Key("ClashOfRim.Events.StatusAutoRefreshing", notice.Reason.Named("REASON")),
        ClientEventRefreshStage.Started => Started(notice.Kind),
        ClientEventRefreshStage.AdoptedQueue => FormatEventQueue(notice.Queue),
        _ => FormatUpdate(notice.Update!)
    };

    private static string FormatUpdate(ClientEventReadUpdate update)
    {
        if (update.Failure is not null) return Failed(update);
        if (update.Stage == ClientEventReadStage.Queue)
        {
            string queue = FormatEventQueue(update.Queue);
            return update.Request.Kind == ClientEventReadKind.Wait
                ? ClashOfRimText.Key(update.Changed ? "ClashOfRim.Events.StatusOnlineArrived" : "ClashOfRim.Events.StatusOnlineTimedOut", queue.Named("QUEUE"))
                : queue;
        }
        return update.Empty ? Empty(update.Request.Kind) : FormatEventDetails(update.Details);
    }

    public void Diagnose(ClientEventRefreshNotice notice)
    {
        if (notice.Stage == ClientEventRefreshStage.Deferred)
            ClashLog.Message($"[ClashOfRim][AutoEventRefresh] queued after local atomic mutation reason={notice.Reason}.");
        if (notice.Update is not { } update) return;
        if (update.Failure?.Exception is { } exception) Log.Warning("[ClashOfRim] Event read failed: " + exception);
        if (update.Failure is null && !update.Empty && update.Stage == ClientEventReadStage.Details)
            LogGiftDetails(update.Request.Kind == ClientEventReadKind.Automatic ? "AutoEventDetails" : "RefreshDetails", update.Details);
    }

    internal static string Started(ClientEventReadKind kind) => ClashOfRimText.Key("ClashOfRim.Events." + (kind switch
    {
        ClientEventReadKind.Queue => "StatusPullingPending",
        ClientEventReadKind.Wait => "StatusWaitingOnline",
        ClientEventReadKind.Details => "StatusPullingDetails",
        _ => "StatusRefreshing"
    }));

    internal static string Empty(ClientEventReadKind kind) => ClashOfRimText.Key("ClashOfRim.Events." + (kind switch
    {
        ClientEventReadKind.Automatic => "StatusAutoNoEvents",
        ClientEventReadKind.Details => "StatusNoViewableDetails",
        _ => "StatusNoDetails"
    }));

    internal static string Failed(ClientEventReadUpdate update)
    {
        var failure = update.Failure!;
        bool automatic = update.Request.Kind == ClientEventReadKind.Automatic;
        if (failure.Kind == ClientEventReadFailureKind.Exception)
        {
            string key = update.Request.Kind switch
            {
                ClientEventReadKind.Automatic => "StatusAutoException",
                ClientEventReadKind.Queue => "StatusQueueException",
                ClientEventReadKind.Wait => "StatusOnlineException",
                ClientEventReadKind.Details => "StatusDetailsException",
                _ => "StatusException"
            };
            return ClashOfRimText.Key("ClashOfRim.Events." + key,
                (failure.Code ?? string.Empty).Named("TYPE"), (failure.Message ?? string.Empty).Named("MESSAGE"));
        }
        string prefix = update.Stage == ClientEventReadStage.Details ? (automatic ? "StatusAutoDetails" : "StatusDetails")
            : update.Request.Kind == ClientEventReadKind.Wait ? "StatusOnlineWait" : automatic ? "StatusAutoQueue" : "StatusQueue";
        return ClashOfRimText.Key("ClashOfRim.Events." + prefix + (failure.Kind == ClientEventReadFailureKind.Rejected ? "Rejected" : "Failed"),
            (failure.Code ?? string.Empty).Named("CODE"), (failure.Message ?? string.Empty).Named("MESSAGE"));
    }
    internal static void LogGiftDetails(string source, IReadOnlyCollection<ModEventDetailDto>? details)
    {
        if (details is null)
        {
            ClashLog.Message($"[ClashOfRim][{source}] details=null.");
            return;
        }

        ClashLog.Message($"[ClashOfRim][{source}] detailCount={details.Count}.");
        foreach (ModEventDetailDto detail in details.Where(ItemDeliveryClientProcessor.IsGiftDetail).Take(5))
        {
            ClashLog.Message(
                $"[ClashOfRim][{source}] gift event={detail.EventId} status={detail.Status} targetMap={detail.TargetContext?.MapUniqueId ?? "<null>"} landingMode={detail.TargetContext?.LandingMode ?? "<null>"} payloadLength={detail.PayloadSummary?.Length ?? 0} payloadPreview={Preview(detail.PayloadSummary, 512)}");
        }
    }

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
