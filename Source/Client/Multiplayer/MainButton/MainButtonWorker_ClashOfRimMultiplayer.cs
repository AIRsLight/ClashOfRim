using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer;

public sealed class MainButtonWorker_ClashOfRimMultiplayer : MainButtonWorker_ToggleTab
{
    public override bool Visible =>
        base.Visible
        && LoadedModManager.GetMod<ClashOfRimMod>()?.ShouldShowMultiplayerMainButton == true;
}
