using System.Globalization;

namespace AIRsLight.ClashOfRim.Save;

public static class WorldTileText
{
    public static bool TryParse(string? value, out int tile, out int layerId)
    {
        tile = layerId = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string[] parts = value.Split(',', 2);
        return int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out tile) && tile >= 0
            && (parts.Length == 1 || int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out layerId) && layerId >= 0);
    }
}
