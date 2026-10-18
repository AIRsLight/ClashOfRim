using System;
using System.Linq;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Support;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Support;

internal sealed class ClientSupportPresentation : IClientSupportPresentation
{
    private readonly Action<string> status;
    internal ClientSupportPresentation(Action<string> status) => this.status = status;
    public string Busy => ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
    public string BaseSnapshotChanged => ClashOfRimText.Key("ClashOfRim.SnapshotConfirmation.BaseSnapshotChanged");
    public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => ClientLocalMutationPresentation.AdmissionFailure(admission, pending);
    public string Text(string key, params (string Name, object? Value)[] arguments) =>
        ClashOfRimText.Key("ClashOfRim.Support." + key, arguments.Select(a => (a.Value ?? string.Empty).Named(a.Name)).ToArray());
    public void Status(string message) => status(message);
    public void Message(string message, bool rejected) => Messages.Message(message, rejected ? MessageTypeDefOf.RejectInput : MessageTypeDefOf.PositiveEvent, historical: false);
    public void Diagnose(Exception exception) => Log.Warning("[ClashOfRim] Support operation failed: " + exception);
}
