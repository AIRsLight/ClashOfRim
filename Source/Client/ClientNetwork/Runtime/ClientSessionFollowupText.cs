using AIRsLight.ClashOfRim.Modules.World;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionFollowupText : IClientSessionFollowupText
{
    public string Text(string key) => ClashOfRimText.Key(key);
}
