using System;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal sealed class ClientRaidLaunchPresentation : IClientRaidLaunchPresentation
{
    private readonly Action<string> status;
    internal ClientRaidLaunchPresentation(Action<string> status) => this.status = status;
    public string Text(string key, params (string Name, object? Value)[] arguments) =>
        ClashOfRimText.Key(key, arguments.Select(a => (a.Value ?? string.Empty).Named(a.Name)).ToArray());
    public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => ClientLocalMutationPresentation.AdmissionFailure(admission, pending);
    public string PreparationFailure(ClientRaidPreparationFailure failure) => ClientRaidPreparationPresentation.Failure(failure);
    public string TargetLabel(ModWorldMapMarkerDto target) => ClientWorldTargetPresentation.Label(target);
    public void Status(string message) => status(message);
    public void Message(string message, bool rejected) => Messages.Message(message, rejected ? MessageTypeDefOf.RejectInput : MessageTypeDefOf.ThreatBig, historical: false);
    public void Progress(string message)
    {
        status(message);
        ServerEntryProgressWindow.Show(ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.LoadingTitle"), message, -1f, false);
    }
    public void CloseProgress() => ServerEntryProgressWindow.CloseCurrent();
    public void Packaging() => LongEventHandler.SetCurrentEventText(ClashOfRimText.Key("ClashOfRim.Raid.StatusPrepareSnapshotLong"));
    public void Diagnose(Exception exception) => Log.Warning("[ClashOfRim] Raid launch failed: " + exception);
}
