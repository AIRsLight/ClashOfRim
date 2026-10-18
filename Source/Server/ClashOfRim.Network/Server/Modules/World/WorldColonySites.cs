using System.Globalization;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal static class WorldColonySites
{
    internal static (string UserId, string ColonyId, string SiteId) Key(PlayerColonySiteDto site)
    {
        string stableSiteId = !string.IsNullOrWhiteSpace(site.WorldObjectId)
            ? site.WorldObjectId!
            : !string.IsNullOrWhiteSpace(site.MapUniqueId)
                ? site.MapUniqueId!
                : "tile:" + site.Tile.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + Math.Max(0, site.TileLayerId).ToString(CultureInfo.InvariantCulture);
        return (site.UserId, site.ColonyId, stableSiteId);
    }

    internal static IReadOnlyList<PlayerColonySiteDto> Current(
        WorldConfigurationDto configuration)
    {
        var bySite = new Dictionary<(string UserId, string ColonyId, string SiteId), PlayerColonySiteDto>();
        foreach (PlayerColonySiteDto site in configuration.PlayerColonySites)
        {
            if (site.Tile >= 0 && !string.IsNullOrWhiteSpace(site.UserId) && !string.IsNullOrWhiteSpace(site.ColonyId))
            {
                bySite[Key(site)] = site;
            }
        }

        return bySite.Values
            .OrderBy(site => site.Tile)
            .ThenBy(site => site.UserId, StringComparer.Ordinal)
            .ThenBy(site => site.ColonyId, StringComparer.Ordinal)
            .ToList();
    }
}
