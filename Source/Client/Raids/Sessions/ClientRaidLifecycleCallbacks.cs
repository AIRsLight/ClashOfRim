using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Raids;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal sealed class ClientRaidLifecycleCallbacks
{
    internal static ClientRaidLifecycleCallbacks? Current { get; private set; }
    internal static void Attach(ClientRaidLifecycleCallbacks? value) => Current = value;
    private readonly ClientRaidLifecycle lifecycle;
    internal ClientRaidLifecycleCallbacks(ClientRaidLifecycle lifecycle) => this.lifecycle = lifecycle;
    private bool Owns(Game game) => ReferenceEquals(Current, this) && ReferenceEquals(game, Verse.Current.Game);
    internal void Advance(Game game) { if (Owns(game)) lifecycle.Advance(); }
    internal Task PollAsync(Game game, ActiveRaidBattleSession? battle)
        => Owns(game) && battle is not null ? lifecycle.PollAsync(new ClientRaidSettlementRuntime(battle)) : Task.CompletedTask;
}
