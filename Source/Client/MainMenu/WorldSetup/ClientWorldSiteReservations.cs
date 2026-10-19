using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld.Planet;

namespace AIRsLight.ClashOfRim.MainMenu;

// Only engine tile conversion and presentation live here. The existing occupied
// site module owns cache identity, geometry checks and neighbouring-site rules.
internal sealed class ClientWorldSiteReservations
{
    internal static ClientWorldSiteReservations? Current { get; private set; }
    internal static void Attach(ClientWorldSiteReservations? value) => Current = value;
    private readonly ClientOccupiedColonySites sites;
    internal ClientWorldSiteReservations(ClientOccupiedColonySites sites) => this.sites = sites;
    internal bool IsBlocked(PlanetTile tile, out ModPlayerColonySiteDto? blockingSite)
    {
        blockingSite = null;
        return tile.Valid && sites.IsBlocked(tile.tileId, ClientWorldRuntimeValues.ReadPlanetTileLayerId(tile), out blockingSite);
    }
    internal static string Format(ModPlayerColonySiteDto site)
    {
        string owner = string.IsNullOrWhiteSpace(site.UserId) ? ClashOfRimText.Key("ClashOfRim.UnknownOtherPlayer") : site.UserId;
        string label = string.IsNullOrWhiteSpace(site.Label) ? site.ColonyId ?? string.Empty : site.Label!;
        return string.IsNullOrWhiteSpace(label) ? $"{owner}@{site.Tile}" : $"{owner}/{label}@{site.Tile}";
    }
}
