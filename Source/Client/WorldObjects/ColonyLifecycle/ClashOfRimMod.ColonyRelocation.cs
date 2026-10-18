using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal Command? TryBuildColonyRelocationCommand(Caravan caravan, Command original)
        => colonyRelocationCommands.Build(caravan, original);
    internal Command? TryBuildColonyRelocationCommand(Map map, Command original, bool requiresNoEnemies)
        => colonyRelocationCommands.Build(map, original, requiresNoEnemies);
    internal bool TryBeginImplicitColonyRelocationTakeoff(Map map)
        => map is null || colonyRelocation.Takeoff(new ClientColonyRelocationTarget(map.Parent, map, implicitMove: true));
    internal void StartImplicitColonyRelocationConfirmation(Map map)
    {
        if (map is not null) _ = colonyRelocation.LandAsync(new ClientColonyRelocationTarget(map.Parent, map, implicitMove: true));
    }
}
