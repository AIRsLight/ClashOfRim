using System;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Modules.Events;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.EventLetters;

internal sealed class ClientEventApplicationPresentation : IClientEventApplicationPresentation
{
    private readonly Action<bool, string> status;
    internal ClientEventApplicationPresentation(Action<bool, string> status) => this.status = status;

    public string Reason(ClientEventApplicationReason reason) => ClashOfRimText.Key(reason switch
    {
        ClientEventApplicationReason.MissingDetail => "ClashOfRim.EventLetter.RefreshReasonMissingDetail",
        ClientEventApplicationReason.AcceptLetter => "ClashOfRim.EventLetter.ReasonAcceptedByLetter",
        ClientEventApplicationReason.RejectLetter => "ClashOfRim.EventLetter.ReasonRejectedByLetter",
        ClientEventApplicationReason.RejectSupportLetter => "ClashOfRim.EventLetter.ReasonRejectedSupportByLetter",
        _ => "ClashOfRim.GiftProcessing.RejectReason"
    });

    public string Format(ClientEventApplicationNotice notice) => notice.Stage switch
    {
        ClientEventApplicationStage.MissingDetail => ClashOfRimText.Key("ClashOfRim.EventLetter.ActionMissingDetail", notice.EventId.Named("EVENTID")),
        ClientEventApplicationStage.NoGift => ClashOfRimText.Key("ClashOfRim.GiftProcessing.NoGiftDetail"),
        ClientEventApplicationStage.NoPending => ClashOfRimText.Key("ClashOfRim.GiftProcessing.NoPendingConfirmation"),
        ClientEventApplicationStage.AdmissionFailed => ClientLocalMutationPresentation.AdmissionFailure(notice.Admission, notice.Mutation),
        ClientEventApplicationStage.Busy => ClashOfRimText.Key(notice.Automatic ? "ClashOfRim.SnapshotUpload.Busy" :
            notice.Kind is ClientEventApplicationKind.SupportArrival or ClientEventApplicationKind.SupportReturn
                ? "ClashOfRim.EventLetter.SupportAcceptBusy" : "ClashOfRim.EventLetter.GiftAcceptBusy"),
        ClientEventApplicationStage.Applying => ClashOfRimText.Key(notice.Automatic ? "ClashOfRim.Events.StatusAutoApplying" :
            notice.Kind is ClientEventApplicationKind.SupportArrival or ClientEventApplicationKind.SupportReturn
                ? "ClashOfRim.EventLetter.SupportConfirmOperation" : "ClashOfRim.SnapshotConfirmationFailure.OperationGift"),
        ClientEventApplicationStage.Prepared => notice.Automatic ? AutoFailed(notice) : FormatPrepared(notice.Prepared!),
        ClientEventApplicationStage.HydrationFailed => notice.Message ?? string.Empty,
        ClientEventApplicationStage.Applied when !notice.Automatic && notice.Gift is not null => FormatGift(notice.Gift, notice.Pending),
        ClientEventApplicationStage.Failed when !notice.Automatic && notice.Gift is not null => FormatGift(notice.Gift, notice.Pending),
        ClientEventApplicationStage.Failed when notice.Automatic => AutoFailed(notice),
        ClientEventApplicationStage.Exception => ClashOfRimText.Key(notice.Automatic
            ? "ClashOfRim.Events.StatusAutoApplyException" : "ClashOfRim.Events.StatusApplyException",
            (notice.Exception?.GetType().Name ?? string.Empty).Named("TYPE"), (notice.Message ?? string.Empty).Named("MESSAGE")),
        ClientEventApplicationStage.Confirming => ClashOfRimText.Key("ClashOfRim.Events.StatusAutoAppliedConfirming", notice.Count.Named("COUNT")),
        _ => notice.Message ?? string.Empty
    };

    public void Report(ClientEventApplicationNotice notice)
    {
        string message = Format(notice);
        status(notice.Automatic, message);
        // The game-test diagnostics classify this existing preparation failure prefix.
        if (notice.Automatic && notice.Stage == ClientEventApplicationStage.Prepared)
            Log.Warning($"[ClashOfRim][AutoEventApply] prepare failed event={notice.EventId} kind={notice.Prepared?.Kind} message={notice.Message}");
        else if (notice.Stage is ClientEventApplicationStage.Exception or ClientEventApplicationStage.Failed or ClientEventApplicationStage.HydrationFailed)
            Log.Warning($"[ClashOfRim][EventApply] event={notice.EventId} kind={notice.Kind} stage={notice.Stage} message={message} {notice.Exception}");
        else ClashLog.Message($"[ClashOfRim][EventApply] event={notice.EventId} kind={notice.Kind} stage={notice.Stage} message={message}");
        if (notice.Stage == ClientEventApplicationStage.Busy
            || (!notice.Automatic && (notice.Kind is ClientEventApplicationKind.SupportArrival or ClientEventApplicationKind.SupportReturn)
                && (notice.Stage is ClientEventApplicationStage.Applied or ClientEventApplicationStage.Failed)))
            Messages.Message(message, notice.Stage == ClientEventApplicationStage.Applied ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.RejectInput, historical: false);
        if (notice.Stage == ClientEventApplicationStage.Confirming)
            Messages.Message(ClashOfRimText.Key("ClashOfRim.AutoOfflineEventsAppliedMessage", notice.Count.Named("COUNT")), MessageTypeDefOf.PositiveEvent, historical: false);
    }

    private static string AutoFailed(ClientEventApplicationNotice notice) => ClashOfRimText.Key(
        notice.Kind == ClientEventApplicationKind.RaidLoss ? "ClashOfRim.Events.StatusAutoRaidLossFailed" : "ClashOfRim.Events.StatusAutoApplyFailed",
        notice.EventId.Named("EVENTID"), (notice.Message ?? string.Empty).Named("MESSAGE"));

    private static string FormatPrepared(ItemDeliveryClientProcessingResult result) => result.LandingPlan is not null
        ? ClashOfRimText.Key("ClashOfRim.GiftProcessing.AcceptPlan", result.LandingPlan.Items.Count.Named("COUNT"))
        : result.RejectionRequest is not null ? ClashOfRimText.Key("ClashOfRim.GiftProcessing.RejectPlan")
        : ClashOfRimText.Key("ClashOfRim.GiftProcessing.Failed", result.Kind.Named("KIND"), result.Message.Named("MESSAGE"));

    private static string FormatGift(GiftLandingApplicationResult result, int pending) => result.Success
        ? ClashOfRimText.Key("ClashOfRim.GiftLanding.StatusSucceeded", FormatMode(result.LandingMode).Named("MODE"),
            result.PlacedThingCount.Named("THINGCOUNT"), result.PlacedStackCount.Named("STACKCOUNT"), pending.Named("PENDING"))
        : ClashOfRimText.Key("ClashOfRim.GiftLanding.StatusFailed", result.Kind.Named("KIND"), result.Message.Named("MESSAGE"));

    private static string FormatMode(string mode) => mode switch
    {
        "DropPod" => ClashOfRimText.Key("ClashOfRim.GiftLanding.Mode.DropPod"),
        "MapEdge" => ClashOfRimText.Key("ClashOfRim.GiftLanding.Mode.MapEdge"),
        "CenterNear" => ClashOfRimText.Key("ClashOfRim.GiftLanding.Mode.CenterNear"),
        _ => string.IsNullOrWhiteSpace(mode) ? ClashOfRimText.Key("ClashOfRim.NotSpecified") : mode
    };
}
