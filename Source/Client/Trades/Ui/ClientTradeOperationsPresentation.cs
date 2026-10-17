using System;
using System.Linq;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Trades;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

internal sealed class ClientTradeOperationsPresentation : IClientTradeOperationsPresentation
{
    public string Text(string key, params (string Name, object? Value)[] arguments) =>
        ClashOfRimText.Key(key, arguments.Select(a => (a.Value ?? "").Named(a.Name)).ToArray());
    public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => ClientLocalMutationPresentation.AdmissionFailure(admission, pending);
    public void Message(string message, bool rejected) => Messages.Message(message, rejected ? MessageTypeDefOf.RejectInput : MessageTypeDefOf.PositiveEvent, historical: false);
    public void Diagnose(Exception exception) => Log.Warning("[ClashOfRim] Trade local transaction failed: " + exception);
}
