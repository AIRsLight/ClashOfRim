using System.Linq;
using AIRsLight.ClashOfRim.Modules.World;
using Verse;

namespace AIRsLight.ClashOfRim;

internal sealed class ClientWorldPreparationPresentation : IClientWorldPreparationPresentation
{
    public string Text(string key, params (string Name, object? Value)[] arguments)
        => ClashOfRimText.Key(key, arguments.Select(argument => (argument.Value ?? "").Named(argument.Name)).ToArray());
    public void Diagnose(string message) => Log.Warning("[ClashOfRim] " + message);
}
