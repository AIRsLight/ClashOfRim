using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld.Planet;
using Verse;
using static AIRsLight.ClashOfRim.ClientWorldRuntimeValues;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal bool TryGenerateWorldFromServerConfiguration(Window createWorldPage)
        => worldGeneration.TryStart(new ClientWorldGenerationPage(createWorldPage));

    internal bool IsBlockedByServerColonySite(int tile, out ModPlayerColonySiteDto? blockingSite)
        => occupiedColonySites.IsBlocked(tile, 0, out blockingSite);

    internal bool IsBlockedByServerColonySite(PlanetTile tile, out ModPlayerColonySiteDto? blockingSite)
    {
        blockingSite = null;
        return tile.Valid && occupiedColonySites.IsBlocked(tile.tileId, ReadPlanetTileLayerId(tile), out blockingSite);
    }

    internal static string FormatBlockedColonySite(ModPlayerColonySiteDto site)
    {
        string owner = string.IsNullOrWhiteSpace(site.UserId)
            ? ClashOfRimText.Key("ClashOfRim.UnknownOtherPlayer")
            : site.UserId;
        string label = string.IsNullOrWhiteSpace(site.Label)
            ? site.ColonyId ?? string.Empty
            : site.Label ?? string.Empty;
        return string.IsNullOrWhiteSpace(label)
            ? $"{owner}@{site.Tile}"
            : $"{owner}/{label}@{site.Tile}";
    }

    internal void SubmitInitialWorldConfigurationIfPending()
        => _ = initialWorldSetup.StartAsync(colonyAppearance.Capture());

    internal bool TryApplyServerStorytellerBaselineAndContinue(Window storytellerPage)
        => storytellerConfiguration.ContinueSetup(new ClientStorytellerPage(storytellerPage));

    internal void LockMultiplayerCommitmentModeIfNeeded() => storytellerConfiguration.LockCommitment();
}
