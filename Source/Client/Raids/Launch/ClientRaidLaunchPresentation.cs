using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal sealed class ClientRaidLaunchPresentation : IClientRaidLaunchPresentation, IClientRaidCommandPresentation
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
    public void Menu(IReadOnlyList<ClientRaidMenuOption> options)
        => Find.WindowStack.Add(new FloatMenu(options.Select(option => new FloatMenuOption(option.Label, option.Choose)).ToList()));
    public void Confirm(string message, Func<Task> confirmed)
        => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(message, () => _ = confirmed()));
    public void Progress(string message)
    {
        status(message);
        ServerEntryProgressWindow.Show(ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.LoadingTitle"), message, -1f, false);
    }
    public void CloseProgress() => ServerEntryProgressWindow.CloseCurrent();
    public void Packaging() => LongEventHandler.SetCurrentEventText(ClashOfRimText.Key("ClashOfRim.Raid.StatusPrepareSnapshotLong"));
    public void Diagnose(Exception exception) => Log.Warning("[ClashOfRim] Raid launch failed: " + exception);
}
