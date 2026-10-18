using System;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal sealed class ClientColonyRelocationCommands
{
    [ThreadStatic] private static bool readingVanilla;
    internal static Command VanillaCommand(Caravan caravan)
    {
        bool previous = readingVanilla; readingVanilla = true;
        try { return SettleInEmptyTileUtility.SettleCommand(caravan); }
        finally { readingVanilla = previous; }
    }
    private readonly ClientColonyRelocation relocation;
    internal ClientColonyRelocationCommands(ClientColonyRelocation relocation) => this.relocation = relocation;
    internal Command? Build(Caravan caravan, Command original)
        => readingVanilla || caravan.Faction != Faction.OfPlayer || !caravan.IsPlayerControlled ? null : Build(new ClientColonyRelocationTarget(caravan), original);
    internal Command? Build(Map map, Command original, bool requiresNoEnemies)
        => map.IsPlayerHome || !Find.Maps.Contains(map) ? null : Build(new ClientColonyRelocationTarget(map.Parent, map, requiresNoEnemies), original);
    private Command? Build(ClientColonyRelocationTarget target, Command original)
    {
        var command = relocation.Command(target, ClientColonyRelocationTarget.NonLimitFailure(original));
        if (command is null) return null;
        var result = new Command_Action
        {
            defaultLabel = ClashOfRimText.Key("ClashOfRim.ColonyRelocation.CommandLabel"),
            defaultDesc = ClashOfRimText.Key("ClashOfRim.ColonyRelocation.CommandDesc"),
            icon = original.icon, Order = original.Order, action = command.Open
        };
        if (command.Disabled is { } failure) result.Disable(failure);
        return result;
    }
}
