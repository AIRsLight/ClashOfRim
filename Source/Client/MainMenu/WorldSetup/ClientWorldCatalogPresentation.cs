using System;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using Verse;

namespace AIRsLight.ClashOfRim;

internal sealed class ClientWorldCatalogPresentation : IClientWorldCatalogPresentation
{
    private readonly Action<string> status;
    internal ClientWorldCatalogPresentation(Action<string> status) => this.status = status;
    public string Text(string key, params (string Name, object? Value)[] arguments)
        => ClashOfRimText.Key(key, arguments.Select(argument => (argument.Value ?? "").Named(argument.Name)).ToArray());
    public void Status(string message) => status(message);
    public void Diagnose(string message) => Log.Warning("[ClashOfRim] " + message);
    public string Summary(ModWorldConfigurationDto configuration)
    {
        var items = ClashOfRimCompatibilityApi.GetWorldConfigurationExtensionSummary(configuration);
        return items.Count == 0 ? Text("ClashOfRim.None")
            : string.Join(", ", items.Select(item => string.IsNullOrWhiteSpace(item.Label) ? item.Value : item.Label + " " + item.Value));
    }
}
