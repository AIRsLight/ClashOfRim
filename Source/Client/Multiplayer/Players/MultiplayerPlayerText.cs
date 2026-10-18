using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Multiplayer;

namespace AIRsLight.ClashOfRim.Multiplayer.Players;

internal static class MultiplayerPlayerText
{
    internal static string PlayerDisplayName(ModPlayerSummaryDto player) =>
        ClientPlayerPanel.DisplayName(player, ClashOfRimText.Key("ClashOfRim.UnknownPlayer"));
}
