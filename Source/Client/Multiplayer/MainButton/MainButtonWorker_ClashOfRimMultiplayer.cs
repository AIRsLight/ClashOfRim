using RimWorld;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Multiplayer;

public sealed class MainButtonWorker_ClashOfRimMultiplayer : MainButtonWorker_ToggleTab
{
    private static ClientSessionLifecycle? session;
    internal static void Attach(ClientSessionLifecycle value) => session = value;
    private static bool ShouldShowMultiplayerMainButton => session?.HasConfiguredSession == true;
    public override bool Visible =>
        base.Visible
        && ShouldShowMultiplayerMainButton;
}
