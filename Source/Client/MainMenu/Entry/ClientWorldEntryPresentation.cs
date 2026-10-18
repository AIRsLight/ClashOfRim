using System;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim;

internal sealed class ClientWorldEntryPresentation : IClientWorldEntryPresentation, IClientWorldPreparationPresentation
{
    private readonly Action<string> status;
    private readonly Action<string, string> registrationWindow;
    private readonly ClientCompatibilityPresentation compatibility;
    internal ClientWorldEntryPresentation(Action<string> status, Action<string, string> registrationWindow, ClientCompatibilityPresentation compatibility)
    { this.status = status; this.registrationWindow = registrationWindow; this.compatibility = compatibility; }
    public string Text(string key, params (string Name, object? Value)[] arguments)
        => ClashOfRimText.Key(key, arguments.Select(argument => (argument.Value ?? "").Named(argument.Name)).ToArray());
    public void Diagnose(string message) => Log.Warning("[ClashOfRim] " + message);
    public void Reject(string message) => Messages.Message(message, MessageTypeDefOf.RejectInput, historical: false);
    public void Failure(string message)
    {
        status(message); Messages.Message(message, MessageTypeDefOf.RejectInput, historical: false);
        ServerEntryProgressWindow.Show(Text("ClashOfRim.ServerEntry.FailedTitle"), message, 1f, true);
    }
    public void Progress(string message, float progress)
    { status(message); ServerEntryProgressWindow.Show(Text("ClashOfRim.ServerEntry.Title"), message, progress, false); }
    public void Scenario(string? message)
    {
        if (message is not null) { status(message); Messages.Message(message, MessageTypeDefOf.NeutralEvent, historical: false); }
        CloseProgress(); Find.WindowStack.Add(new Page_SelectScenario());
    }
    public void CloseProgress() => ServerEntryProgressWindow.CloseCurrent();
    public void ConfirmRegistration(string account, Action accept)
        => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(Text("ClashOfRim.ServerEntry.AccountMissingPrompt", ("USER", account)),
            accept, destructive: false, title: Text("ClashOfRim.ServerEntry.AccountMissingTitle")));
    public void Register(string server, string account) => registrationWindow(server, account);
    public void Compatibility(ModLoginResponseDto response, string? language, bool languageAccepted, Func<bool> current, Action? accept = null, Action? cancel = null)
        => compatibility.Show(response, languageAccepted, language, accept, cancel, current);
    public void Enqueue(Action action, Action discard) => ClientMainThreadDispatcher.Enqueue(action, discard);
}
