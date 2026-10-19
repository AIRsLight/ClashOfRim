using System.Linq;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using Verse;

namespace AIRsLight.ClashOfRim.CompatibilityClient;

internal sealed class ClientCompatibilityOverridePresentation : IClientCompatibilityOverridePresentation, IClientCompatibilityReportText
{
    public string Text(string key, params (string Name, object? Value)[] args)
        => ClashOfRimText.Key(key, args.Select(arg => arg.Value.Named(arg.Name)).ToArray());
    public void Diagnose(string message) => Log.Warning("[ClashOfRim][Compatibility] " + message);
}
