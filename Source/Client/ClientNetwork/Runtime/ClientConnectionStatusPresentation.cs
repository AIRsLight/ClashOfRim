using System;
using System.Linq;
using AIRsLight.ClashOfRim.Modules.Presence;
using Verse;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientConnectionStatusPresentation : IClientConnectionStatusPresentation
{
    private readonly Action<string> status;
    internal ClientConnectionStatusPresentation(Action<string> status) => this.status = status;
    public string Text(string key, params (string Name, object? Value)[] args)
        => ClashOfRimText.Key(key, args.Select(a => (a.Value is string text ? ClashOfRimText.SafeArgument(text) : a.Value).Named(a.Name)).ToArray());
    public void Status(string text) => status(text);
    public void Diagnose(string text, bool warning)
    {
        if (warning) Log.Warning("[ClashOfRim] " + text);
        else ClashLog.Message("[ClashOfRim] " + text);
    }
}
