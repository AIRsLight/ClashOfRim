using System;
using System.Linq;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using Verse;

namespace AIRsLight.ClashOfRim.Mercenaries;

internal sealed class ClientMercenaryPresentation : IClientMercenaryPresentation
{
    public string Text(string key, params (string Name, object? Value)[] arguments) =>
        ClashOfRimText.Key("ClashOfRim.Mercenary." + key, arguments.Select(a => (a.Value ?? string.Empty).Named(a.Name)).ToArray());
    public void Diagnose(Exception exception) => Log.Warning("[ClashOfRim] Mercenary operation failed: " + exception);
}
