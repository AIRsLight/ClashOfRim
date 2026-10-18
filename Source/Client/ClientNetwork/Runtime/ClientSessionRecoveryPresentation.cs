using System;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.World;
using Verse;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionRecoveryPresentation : IClientSessionRecoveryPresentation
{
    private readonly Action<string> login, presence;
    internal ClientSessionRecoveryPresentation(Action<string> login, Action<string> presence)
    { this.login = login; this.presence = presence; }
    public string Text(string key, params (string Name, object? Value)[] args)
        => ClashOfRimText.Key(key, args.Select(a => (a.Value is string text ? ClashOfRimText.SafeArgument(text) : a.Value).Named(a.Name)).ToArray());
    public void LoginStatus(string text) => login(text);
    public void PresenceStatus(string text) => presence(text);
    public void CloseMainTab() => Find.MainTabsRoot?.SetCurrentTab(null);
    public IClientSessionRecoveryDialog Show(string title, string message, Func<bool> current,
        Func<IClientSessionRecoveryDialog, Task> reconnect, Action<IClientSessionRecoveryDialog> exit)
    {
        Find.WindowStack.WindowOfType<SessionExpiredWindow>()?.Close();
        var window = new SessionExpiredWindow(title, message, current, w => _ = reconnect(w), exit);
        Find.WindowStack.Add(window);
        return window;
    }
    public void Diagnose(string text, bool warning)
    {
        if (warning) Log.Warning("[ClashOfRim][SessionReconnect] " + text);
        else ClashLog.Message("[ClashOfRim][SessionReconnect] " + text);
    }
}
