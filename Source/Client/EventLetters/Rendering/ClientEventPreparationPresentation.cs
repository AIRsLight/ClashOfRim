using System;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.EventLetters;

internal static class ClientEventPreparationPresentation
{
    internal static bool ReportFailure(ClientEventPreparationResult result, Action<string> setStatus,
        Func<string, string>? buildFailureText, Action retry, ClientSnapshotFailureRecovery recovery)
    {
        if (result.Failure is not { } failure) return false;
        if (failure.Kind is ClientEventPreparationFailureKind.Obsolete or ClientEventPreparationFailureKind.InvalidPreparedData) return true;
        string message = failure.Kind switch
        {
            ClientEventPreparationFailureKind.Blocked => ClientLocalMutationPresentation.Blocked(failure.Blocked!),
            ClientEventPreparationFailureKind.Admission => ClientLocalMutationPresentation.AdmissionFailure(failure.Admission, null),
            ClientEventPreparationFailureKind.Busy => ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy"),
            _ => buildFailureText!(ClientSnapshotPreparationPresentation.FailureReason(failure.SnapshotFailure!))
        };
        setStatus(message);
        if (failure.Kind == ClientEventPreparationFailureKind.Build)
            Log.Warning($"[ClashOfRim][EventConfirm] snapshot preparation failed operation={result.Operation} code={failure.SnapshotFailure!.Code} message={message}");
        if (failure.Kind is ClientEventPreparationFailureKind.Blocked or ClientEventPreparationFailureKind.Busy)
            Messages.Message(message, MessageTypeDefOf.RejectInput, historical: false);
        else recovery.Show(result.Operation, message, retry);
        return true;
    }
}
