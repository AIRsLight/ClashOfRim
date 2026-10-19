using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld.Planet;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void OpenColonyAppearanceWindow(MapParent colony) => colonyAppearance.Open(new ClientColonyAppearanceTarget(colony));
    internal void SaveColonyAppearance(string? mode, string? iconDefName, string? colorDefName, string? colorHex)
        => colonyAppearance.SaveCurrent(new(mode, iconDefName, colorDefName, colorHex));
}
