using System;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal sealed class ClientRaidSettlementRuntime : IClientRaidSettlementRuntime
{
    private readonly Game? game;
    private readonly ActiveRaidBattleSession? battle;
    internal ClientRaidSettlementRuntime(ActiveRaidBattleSession? battle)
    {
        game = Current.Game; this.battle = battle;
        Identity = new(battle?.EventId ?? "", battle?.AttackerUserId ?? "", battle?.AttackerColonyId ?? "", battle?.TargetMapId ?? "");
    }
    public ClientRaidSettlementIdentity Identity { get; }
    public bool IsCurrent => game is not null && battle is not null && ReferenceEquals(Current.Game, game) && ReferenceEquals(ClashOfRimGameComponent.ActiveRaidBattleSession, battle);
    public TimeSpan FinalRemaining => battle?.FinalRemaining ?? TimeSpan.Zero;
    public bool TryBegin() => IsCurrent && RemoteMapSessionController.TryBeginRaidBattleFinish(battle);
    public void KeepFinish() { if (IsCurrent) RemoteMapSessionController.KeepRaidBattleFinishInProgress(battle); }
    public void ResetFinish() { if (IsCurrent) RemoteMapSessionController.ResetRaidBattleFinish(battle); }
    public IClientRaidSupportLossSource SupportLosses => new ClientRaidSupportLossSource(battle!);
    public void EnqueueSaving(Action action, Action discarded) => ClientRemoteMapLongEvent.Enqueue(action, discarded, "ClashOfRim.Raid.StatusSettlementSavingLong");
    public void CloseAccepted(string reason)
    {
        if (!IsCurrent) return;
        RemoteMapSessionController.CloseRaidBattleAfterSettlementAccepted(battle!, reason);
        ClashOfRimGameComponent.ClearActiveRaidBattleSession(Identity.EventId);
    }
    public void CloseExpired(bool closeLocalMap)
    {
        if (!IsCurrent) return;
        if (closeLocalMap) RemoteMapSessionController.CloseRaidBattleForFinalDeadline(battle!);
        else ClashOfRimGameComponent.ClearActiveRaidBattleSession(Identity.EventId);
    }
}
