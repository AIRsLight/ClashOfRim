using System;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal sealed class ClientColonyAppearanceTarget : IClientColonyAppearanceTarget
{
    private readonly Game? game = Current.Game;
    private readonly World? world = Find.World;
    private readonly MapParent colony;
    internal ClientColonyAppearanceTarget(MapParent colony) => this.colony = colony;
    public bool IsCurrent => ReferenceEquals(game, Current.Game) && ReferenceEquals(world, Find.World);
    public bool IsAvailable => IsCurrent && CanEdit(colony);
    public string Label => colony.LabelCap;
    internal static bool CanEdit(MapParent colony)
    {
        if (colony.Destroyed || colony.Faction != Faction.OfPlayer || Find.WorldObjects?.AllWorldObjects.Contains(colony) != true) return false;
        string? name = colony.def?.defName;
        if (name?.StartsWith("ClashOfRim_", StringComparison.Ordinal) == true) return false;
        return colony.HasMap || name is "Settlement" or "PlayerSettlement";
    }
}
