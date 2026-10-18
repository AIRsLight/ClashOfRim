using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim;

// Legacy UI/bridge entry points; the directory and creation modules own the work.
public sealed partial class ClashOfRimMod
{
    internal void StartRefreshPlayers() => _ = playerDirectory.RefreshAsync(manual: true);
    internal void SelectNextPlayerTarget() => playerDirectory.SelectNext();
    internal void SelectPlayerTarget(ModPlayerSummaryDto player) => diplomacyActions.Select(player);

    internal void StartCreateDiplomacyEvent(string kind, ModPlayerSummaryDto target, string? customMessage = null) =>
        diplomacyActions.Create(kind, target, customMessage, out _);

    internal void StartCreateDiplomacyEvent(string kind, string? customMessage = null, string? targetDisplayName = null) =>
        diplomacyActions.Create(kind, customMessage, targetDisplayName, out _);

}
