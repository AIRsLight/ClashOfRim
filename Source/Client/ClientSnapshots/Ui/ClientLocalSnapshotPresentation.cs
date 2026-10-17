using System;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

internal sealed class ClientLocalSnapshotPresentation : IClientLocalSnapshotPresentation
{
    private readonly Action<string> status;
    internal ClientLocalSnapshotPresentation(Action<string> status) => this.status = status;
    public string Busy => ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
    public string BaseSnapshotChanged => ClashOfRimText.Key("ClashOfRim.SnapshotConfirmation.BaseSnapshotChanged");
    public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => ClientLocalMutationPresentation.AdmissionFailure(admission, pending);
    public ModSnapshotUploadResult Result(ClientSnapshotUploadResult result) => ClientSnapshotUploadPresentation.ToPublicResult(result);
    public string Failed(string status, string? code, string? reason) => ClashOfRimText.Key("ClashOfRim.LocalMutation.ConfirmFailed",
        status.Named("STATUS"), (code ?? "").Named("CODE"), (reason ?? "").Named("MESSAGE"));
    public string Succeeded(string? snapshotId) => ClashOfRimText.Key("ClashOfRim.LocalMutation.ConfirmSucceeded", (snapshotId ?? "").Named("SNAPSHOT"));
    public string Exception(string status, Exception exception) => ClashOfRimText.Key("ClashOfRim.LocalMutation.ConfirmException",
        status.Named("STATUS"), exception.GetType().Name.Named("TYPE"), exception.Message.Named("MESSAGE"));
    public void DefaultStatus(string message) => status(message);
    public void Message(string message, bool accepted, bool bestEffort) => Messages.Message(message,
        accepted ? MessageTypeDefOf.PositiveEvent : bestEffort ? MessageTypeDefOf.NeutralEvent : MessageTypeDefOf.RejectInput, historical: false);
    public void Diagnose(LocalMutationKind operation, string message, Exception? exception) =>
        Log.Warning("[ClashOfRim] Local mutation confirmation: " + operation + ": " + message + (exception is null ? "" : "\n" + exception));
}
