using AIRsLight.ClashOfRim.Modules.World;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Profile;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal sealed class ClientColonyAbandonTarget : IClientColonyAbandonTarget
{
    private readonly Game? game = Current.Game;
    private readonly World? world = Find.World;
    private readonly MapParent colony;
    internal ClientColonyAbandonTarget(MapParent colony) => this.colony = colony;
    public bool IsCurrent => ReferenceEquals(game, Current.Game) && ReferenceEquals(world, Find.World);
    public bool IsAvailable => IsCurrent && !colony.Destroyed && colony.Faction == Faction.OfPlayer && world?.worldObjects?.AllWorldObjects.Contains(colony) == true;
    public string Label => colony.LabelCap;
    public void ReturnToMenu() { if (IsCurrent) GenScene.GoToMainMenu(); }
}
