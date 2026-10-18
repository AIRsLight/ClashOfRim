using AIRsLight.ClashOfRim.Modules.Raids;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal sealed class ClientRaidCommandSource : IClientRaidCommandSource
{
    private readonly Game? game = Current.Game;
    private readonly World? world = Find.World;
    private readonly IRaidAttackForceSource? source;
    internal ClientRaidCommandSource(IRaidAttackForceSource? source) => this.source = source;
    internal static ClientRaidCommandSource ForCaravan(Caravan? caravan)
        => new(caravan is null ? null : new CaravanRaidAttackForceSource(caravan));
    public bool IsCurrent => ReferenceEquals(game, Current.Game) && ReferenceEquals(world, Find.World);
    public bool IsAvailable => source?.IsAvailable == true;
    public int PawnCount => source?.AttackPawns.Count ?? 0;
    public IClientRaidLaunchRuntime CreateLaunch() => new ClientRaidLaunchRuntime(source!);
}
