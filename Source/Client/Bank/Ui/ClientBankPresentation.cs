using System;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Bank;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Bank;

internal sealed class ClientBankPresentation : IClientBankPresentation
{
    public string Text(string key, params (string Name, object? Value)[] arguments) =>
        ClashOfRimText.Key("ClashOfRim.Bank." + key, arguments.Select(a => (a.Value ?? string.Empty).Named(a.Name)).ToArray());
    public string Summary(ModBankStatusResponseDto? status) => BankUiUtility.FormatStatus(status);
    public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) =>
        ClientLocalMutationPresentation.AdmissionFailure(admission, pending);
    public void Message(string message, bool rejected) =>
        Messages.Message(message, rejected ? MessageTypeDefOf.RejectInput : MessageTypeDefOf.PositiveEvent, historical: false);
    public void Diagnose(Exception exception) => Log.Warning("[ClashOfRim] Bank operation failed: " + exception);
}
