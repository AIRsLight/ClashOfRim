using System;
using System.Linq;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.ClientSnapshots;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Mercenaries;

internal sealed class ClientMercenaryPresentation : IClientMercenaryHiringPresentation
{
    public string Idle => ClashOfRimText.Key("ClashOfRim.Status.MercenaryIdle");
    public string BaseSnapshotChanged => ClashOfRimText.Key("ClashOfRim.SnapshotConfirmation.BaseSnapshotChanged");
    public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => ClientLocalMutationPresentation.AdmissionFailure(admission, pending);
    public void Message(string message, bool rejected) => Messages.Message(message, rejected ? MessageTypeDefOf.RejectInput : MessageTypeDefOf.PositiveEvent, historical: false);
    public string Text(string key, params (string Name, object? Value)[] arguments) =>
        ClashOfRimText.Key("ClashOfRim.Mercenary." + key, arguments.Select(a => (a.Value ?? string.Empty).Named(a.Name)).ToArray());
    public void Diagnose(Exception exception) => Log.Warning("[ClashOfRim] Mercenary operation failed: " + exception);
}
