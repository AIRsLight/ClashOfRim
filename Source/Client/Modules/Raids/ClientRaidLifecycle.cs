using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Raids;

internal interface IClientRaidLifecycleBattle : IClientRaidSettlementRuntime
{
    bool IsExpired { get; }
    bool FinishInProgress { get; }
}

// Loaded-game recovery owns readiness; normal expiry and the final deadline
// both enter the existing settlement workflow, with final expiry taking priority.
internal sealed class ClientRaidLifecycle
{
    private readonly IClientSnapshotGame game;
    private readonly ClientSnapshotReload reload;
    private readonly ClientRaidRecovery recovery;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly IClientRaidSettlementActions settlements;
    internal ClientRaidLifecycle(IClientSnapshotGame game, ClientSnapshotReload reload, ClientRaidRecovery recovery,
        ClientSynchronizationCoordinator synchronization, IClientRaidSettlementActions settlements)
    { this.game = game; this.reload = reload; this.recovery = recovery; this.synchronization = synchronization; this.settlements = settlements; }
    internal void Advance()
    {
        using var p = ClientSessionWork.EnterPublication();
        var source = game.BindCurrentGame();
        reload.Advance();
        if (source.IsCurrent) recovery.Advance();
    }
    internal Task PollAsync(IClientRaidLifecycleBattle battle)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!battle.IsCurrent || recovery.BlocksBattleLifecycle || reload.BlocksGameLifecycle) return Task.CompletedTask;
        if (battle.FinalRemaining <= TimeSpan.Zero) { settlements.Expire(battle); return Task.CompletedTask; }
        return !battle.FinishInProgress && battle.IsExpired && !synchronization.SnapshotInProgress
            ? settlements.StartAsync(battle, "Expired") : Task.CompletedTask;
    }
}
