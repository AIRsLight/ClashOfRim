using System;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Protocol;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.EventLetters;

internal sealed class ClientEventDecisionPresentation : IClientEventDecisionPresentation
{
    private readonly Action<string> setStatus;
    internal ClientEventDecisionPresentation(Action<string> setStatus) => this.setStatus = setStatus;

    public void Report(ClientEventDecisionNotice notice)
    {
        if (notice.Kind == ClientEventDecisionKind.ReportFailure)
        {
            if (notice.Stage == ClientEventDecisionStage.Accepted)
                ClashLog.Message($"[ClashOfRim][EventFailure] reported event={notice.EventId} affected={notice.Receipt!.AffectedEventCount}");
            else Log.Warning($"[ClashOfRim][EventFailure] report {notice.Stage} event={notice.EventId} code={notice.Code} message={notice.Message} exception={notice.Exception}");
            return;
        }
        string message = Format(notice);
        setStatus(message);
        if (notice.Stage == ClientEventDecisionStage.Exception)
            Log.Warning($"[ClashOfRim][EventDecision] {notice.Kind} failed event={notice.EventId}: {notice.Exception}");
        if (notice.Stage == ClientEventDecisionStage.Accepted && notice.Kind != ClientEventDecisionKind.RejectGift)
            Messages.Message(message, MessageTypeDefOf.NeutralEvent, historical: false);
    }

    internal static string Format(ClientEventDecisionNotice notice)
    {
        bool diplomacy = notice.Kind is ClientEventDecisionKind.AcceptDiplomacy or ClientEventDecisionKind.RejectDiplomacy;
        string prefix = notice.Kind switch
        {
            ClientEventDecisionKind.RejectGift => "ClashOfRim.GiftProcessing.Reject",
            ClientEventDecisionKind.RejectSupport => "ClashOfRim.Support.Reject",
            _ => "ClashOfRim.EventLetter.DiplomacyResponse"
        };
        switch (notice.Stage)
        {
            case ClientEventDecisionStage.AdmissionFailed:
                return ClientLocalMutationPresentation.AdmissionFailure(notice.Admission, notice.Pending);
            case ClientEventDecisionStage.Busy:
                return ClashOfRimText.Key("ClashOfRim.Sync.StatusBusy");
            case ClientEventDecisionStage.Started:
                return ClashOfRimText.Key(diplomacy
                    ? notice.Kind == ClientEventDecisionKind.AcceptDiplomacy ? "ClashOfRim.EventLetter.DiplomacyRespondingAccept" : "ClashOfRim.EventLetter.DiplomacyRespondingReject"
                    : prefix + "Submitting", notice.EventId.Named("EVENTID"));
            case ClientEventDecisionStage.Accepted:
                if (diplomacy)
                {
                    if (notice.Receipt!.Result?.ErrorCode == (int)ProtocolErrorCode.DuplicateRequest)
                        return notice.Receipt.Result.Message ?? string.Empty;
                    return ClashOfRimText.Key(notice.Kind == ClientEventDecisionKind.AcceptDiplomacy
                        ? "ClashOfRim.EventLetter.DiplomacyAccepted" : "ClashOfRim.EventLetter.DiplomacyRejected", notice.EventId.Named("EVENTID"));
                }
                return ClashOfRimText.Key(prefix + "ed", (notice.Receipt!.ReturnEventId ?? string.Empty).Named("RETURNEVENT"), notice.Receipt.ReturnEventCreated.Named("CREATED"));
            default:
                string suffix = notice.Stage switch
                {
                    ClientEventDecisionStage.TransportFailed => "Failed",
                    ClientEventDecisionStage.Rejected => "Rejected",
                    ClientEventDecisionStage.Exception => "Exception",
                    _ => throw new ArgumentOutOfRangeException(nameof(notice))
                };
                return ClashOfRimText.Key(prefix + suffix, (notice.Code ?? string.Empty).Named("CODE"),
                    (notice.Message ?? string.Empty).Named("MESSAGE"), (notice.Exception?.GetType().Name ?? string.Empty).Named("TYPE"));
        }
    }
}

internal sealed class ClientEventDecisionRuntime : IClientEventDecisionRuntime
{
    private readonly Action<string> refreshWorld;
    internal ClientEventDecisionRuntime(Action<string> refreshWorld) => this.refreshWorld = refreshWorld;
    public void Enqueue(Action action, Action onDiscard) => ClientMainThreadDispatcher.Enqueue(action, onDiscard);
    public void MarkHandled(string eventId) => ClashOfRimGameComponent.MarkManualEventHandled(eventId);
    public void ApplyDiplomacyRelation(string userId, string? relationKind, ServerEventType eventType)
    {
        ClientDiplomacyRelationEffects.Apply(userId, relationKind, ClientDiplomacyPresentation.DiplomacyRelationReason(eventType));
        refreshWorld(ClashOfRimText.Key("ClashOfRim.EventLetter.RefreshReasonDiplomacyRelationChanged"));
    }
}
