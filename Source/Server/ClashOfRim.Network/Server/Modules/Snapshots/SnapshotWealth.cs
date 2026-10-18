using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal static class SnapshotWealth
{
    internal static int? Total(SaveSnapshotIndex index)
    {
        float total = 0f;
        bool hasWealth = false;
        foreach (var map in index.Maps)
        {
            if (map.WealthTotal is null) continue;
            hasWealth = true;
            total += Math.Max(0f, map.WealthTotal.Value);
        }
        return hasWealth ? (int)Math.Round(total, MidpointRounding.AwayFromZero) : null;
    }
}
