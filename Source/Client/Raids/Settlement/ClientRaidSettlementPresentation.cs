using System;
using System.Linq;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal sealed class ClientRaidSettlementPresentation : IClientRaidSettlementPresentation
{
    private readonly Action<string> status;
    internal ClientRaidSettlementPresentation(Action<string> status) => this.status = status;
    public string Text(string key, params (string Name, object? Value)[] args) => ClashOfRimText.Key(key, args.Select(a => (a.Value ?? "").Named(a.Name)).ToArray());
    public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => ClientLocalMutationPresentation.AdmissionFailure(admission, pending);
    public string SupportFailure(ClientRaidSupportLossFailure failure) => ClientRaidSupportLossPresentation.Failure(failure);
    public void Status(string message) => status(message);
    public void Message(string message, bool? accepted) => Messages.Message(message,
        accepted == true ? MessageTypeDefOf.PositiveEvent : accepted == false ? MessageTypeDefOf.NegativeEvent : MessageTypeDefOf.RejectInput, historical: false);
    public void Packaging()
    {
        ServerEntryProgressWindow.CloseCurrent();
        LongEventHandler.SetCurrentEventText(ClashOfRimText.Key("ClashOfRim.Raid.StatusSettlementSavingLong"));
    }
    public void Diagnose(Exception exception) => Log.Warning("[ClashOfRim] Raid settlement failed: " + exception);
}
