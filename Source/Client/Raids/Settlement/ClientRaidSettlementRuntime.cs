using System;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal sealed class ClientRaidSettlementRuntime : IClientRaidRecoveredBattle, IClientRaidLifecycleBattle
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
    public bool IsExpired => battle?.IsExpired == true;
    public bool FinishInProgress => battle?.FinishInProgress == true;
    public bool HasMap => IsCurrent && RemoteSessionMapUtility.FindActiveSessionMap(ActiveRemoteMapSession.FromRaidBattle(battle!))?.Map is not null;
    public void RestoreDeadlines(ClientRaidServerDeadlines deadlines)
    {
        if (!IsCurrent) return;
        ClientRaidBattleDeadlines.Apply(battle!, deadlines);
        battle!.FinishInProgress = false;
        ClashOfRimGameComponent.SetActiveRaidBattleSession(battle);
    }
    public bool TryBegin() => IsCurrent && ClientRemoteSessionRuntime.TryBeginRaidBattleFinish(battle);
    public void KeepFinish() { if (IsCurrent) ClientRemoteSessionRuntime.KeepRaidBattleFinishInProgress(battle); }
    public void ResetFinish() { if (IsCurrent) ClientRemoteSessionRuntime.ResetRaidBattleFinish(battle); }
    public IClientRaidSupportLossSource SupportLosses => new ClientRaidSupportLossSource(battle!);
    public void EnqueueSaving(Action action, Action discarded) => ClientRemoteMapLongEvent.Enqueue(action, discarded, "ClashOfRim.Raid.StatusSettlementSavingLong");
    public bool CloseAccepted(string reason)
    {
        if (!IsCurrent) return false;
        ClientRemoteSessionRuntime.CloseRaidBattleAfterSettlementAccepted(battle!, reason);
        if (IsCurrent) ClashOfRimGameComponent.ClearActiveRaidBattleSession(Identity.EventId);
        return OwnsClosure;
    }
    public bool CloseExpired(bool closeLocalMap)
    {
        if (!IsCurrent) return false;
        if (closeLocalMap) ClientRemoteSessionRuntime.CloseRaidBattleForFinalDeadline(battle!);
        else ClashOfRimGameComponent.ClearActiveRaidBattleSession(Identity.EventId);
        return IsCurrent || OwnsClosure;
    }
    private bool OwnsClosure => ReferenceEquals(Current.Game, game)
        && ClashOfRimGameComponent.ActiveRaidBattleSession is null && ClashOfRimGameComponent.ActiveRemoteMapSession is null;
}
