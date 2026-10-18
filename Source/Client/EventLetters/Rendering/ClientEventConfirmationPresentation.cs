using AIRsLight.ClashOfRim.Modules.Snapshots;
using System;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Events;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.EventLetters;

internal sealed class ClientEventConfirmationPresentation : IClientEventConfirmationPresentation
{
    private readonly Action<ClientEventConfirmationPurpose, string> setStatus;
    internal ClientEventConfirmationPresentation(Action<ClientEventConfirmationPurpose, string> setStatus) => this.setStatus = setStatus;

    public string Format(ClientEventConfirmationNotice notice)
    {
        string prefix = Prefix(notice.Purpose);
        switch (notice.Stage)
        {
            case ClientEventConfirmationStage.Empty:
                return notice.Purpose == ClientEventConfirmationPurpose.Trade
                    ? ClashOfRimText.Key(prefix + "Failed", "MissingEventId".Named("CODE"), string.Empty.Named("MESSAGE"))
                    : ClashOfRimText.Key(prefix + "Empty");
            case ClientEventConfirmationStage.Uploading:
                return ClashOfRimText.Key(prefix + "Uploading", notice.EventId.Named("EVENTID"), notice.Count.Named("COUNT"));
            case ClientEventConfirmationStage.PreparationFailed:
                var failure = notice.PreparationFailure!;
                return failure.Kind switch
                {
                    ClientEventPreparationFailureKind.Blocked => ClientLocalMutationPresentation.Blocked(failure.Blocked!),
                    ClientEventPreparationFailureKind.Admission => ClientLocalMutationPresentation.AdmissionFailure(failure.Admission, null),
                    ClientEventPreparationFailureKind.Busy => ClashOfRimText.Key(notice.Purpose == ClientEventConfirmationPurpose.SupportArrival
                        ? "ClashOfRim.EventLetter.SupportConfirmBusy" : "ClashOfRim.SnapshotUpload.Busy"),
                    _ => BuildFailure(notice.Purpose, prefix, ClientSnapshotPreparationPresentation.FailureReason(failure.SnapshotFailure!))
                };
            case ClientEventConfirmationStage.Failed:
                var problem = notice.Problem!;
                string suffix = problem.Kind switch
                {
                    ClientSnapshotTransactionFailureKind.Transport => "Failed",
                    ClientSnapshotTransactionFailureKind.Rejected => "Rejected",
                    _ => "Exception"
                };
                string key = IsItemArrival(notice.Purpose) ? "ClashOfRim.Events.StatusSingleConfirm" + suffix : prefix + suffix;
                return ClashOfRimText.Key(key, problem.Code.Named("CODE"), problem.Message.Named("MESSAGE"),
                    problem.Validation.Named("VALIDATION"), (problem.Exception?.GetType().Name ?? string.Empty).Named("TYPE"),
                    (IsItemArrival(notice.Purpose) ? ClashOfRimText.Key(prefix + suffix) : string.Empty).Named("PREFIX"));
            case ClientEventConfirmationStage.Accepted:
                return ClashOfRimText.Key(prefix + (notice.Purpose == ClientEventConfirmationPurpose.AutomaticEvents ? "Completed" : "Succeeded"),
                    notice.EventId.Named("EVENTID"), (notice.SnapshotId ?? string.Empty).Named("SNAPSHOT"),
                    notice.Pending.Named("PENDING"), notice.Count.Named("ACCEPTED"), notice.Rejected.Named("REJECTED"));
            default: throw new ArgumentOutOfRangeException(nameof(notice));
        }
    }

    public void Report(ClientEventConfirmationNotice notice, string message)
    {
        setStatus(notice.Purpose, message);
        if (notice.Stage == ClientEventConfirmationStage.PreparationFailed)
        {
            if (notice.PreparationFailure!.Kind is ClientEventPreparationFailureKind.Blocked or ClientEventPreparationFailureKind.Busy)
                Messages.Message(message, MessageTypeDefOf.RejectInput, historical: false);
            else if (notice.PreparationFailure.Kind == ClientEventPreparationFailureKind.Build)
                Log.Warning($"[ClashOfRim][EventConfirm] snapshot preparation failed purpose={notice.Purpose} code={notice.PreparationFailure.SnapshotFailure!.Code} message={message}");
        }
        else if (notice.Stage == ClientEventConfirmationStage.Failed)
        {
            if (notice.Problem!.Exception is not null)
                Log.Warning($"[ClashOfRim][EventConfirm] failed purpose={notice.Purpose} event={notice.EventId} code={notice.Problem.Code} message={message} exception={notice.Problem.Exception}");
        }
        else if (notice.Stage == ClientEventConfirmationStage.Accepted)
        {
            if (IsItemArrival(notice.Purpose)) Messages.Message(message, MessageTypeDefOf.PositiveEvent, historical: false);
            if (notice.Purpose == ClientEventConfirmationPurpose.AutomaticEvents)
                ClashLog.Message($"[ClashOfRim][AutoEventConfirm] completed accepted={notice.Count} rejected={notice.Rejected} appliedSnapshot={notice.SnapshotId ?? "<null>"}.");
        }
    }

    private static string BuildFailure(ClientEventConfirmationPurpose purpose, string prefix, string reason) => IsItemArrival(purpose)
        ? $"{ClashOfRimText.Key(prefix + "BuildFailed")}：{reason}"
        : ClashOfRimText.Key(prefix + "BuildFailed", "SaveToMemoryFailed".Named("CODE"), reason.Named("MESSAGE"), reason.Named("REASON"));

    private static bool IsItemArrival(ClientEventConfirmationPurpose purpose) =>
        purpose is ClientEventConfirmationPurpose.Gift or ClientEventConfirmationPurpose.SupportArrival;

    private static string Prefix(ClientEventConfirmationPurpose purpose) => purpose switch
    {
        ClientEventConfirmationPurpose.Gift => "ClashOfRim.GiftProcessing.Confirm",
        ClientEventConfirmationPurpose.SupportArrival => "ClashOfRim.EventLetter.SupportConfirm",
        ClientEventConfirmationPurpose.Trade => "ClashOfRim.Trade.StatusConfirm",
        ClientEventConfirmationPurpose.AutomaticEvents => "ClashOfRim.Events.StatusAutoConfirm",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose))
    };
}

internal sealed class ClientEventConfirmationEffects : IClientEventConfirmationEffects
{
    public void MarkHandled(string eventId) => ClashOfRimGameComponent.MarkManualEventHandled(eventId);
}
