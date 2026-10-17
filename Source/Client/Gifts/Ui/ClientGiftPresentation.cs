using System;
using System.Linq;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Gifts;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Gifts;

internal sealed class ClientGiftPresentation : IClientGiftPresentation
{
    private readonly Action<string> status;
    internal ClientGiftPresentation(Action<string> status) => this.status = status;
    public string Text(string key, params (string Name, object? Value)[] arguments) =>
        ClashOfRimText.Key(key, arguments.Select(a => (a.Value ?? string.Empty).Named(a.Name)).ToArray());
    public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => ClientLocalMutationPresentation.AdmissionFailure(admission, pending);
    public void Status(string message) => status(message);
    public void Message(string message, bool rejected, bool forced = false) => Messages.Message(message,
        rejected ? MessageTypeDefOf.RejectInput : forced ? MessageTypeDefOf.NegativeEvent : MessageTypeDefOf.PositiveEvent, historical: false);
    public void Diagnose(Exception exception) => Log.Warning("[ClashOfRim] Gift delivery failed: " + exception);
}
