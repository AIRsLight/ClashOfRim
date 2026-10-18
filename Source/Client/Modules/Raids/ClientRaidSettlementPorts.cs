using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Raids;

internal sealed record ClientRaidSettlementIdentity(string EventId, string AttackerUserId, string AttackerColonyId, string TargetMapId);
internal static class ClientRaidSettlementIntent
{
    internal static string Key(string userId, string eventId, string reason) => $"raid-settlement:{userId}:{eventId}:{reason}";
    internal static string Result(string reason) => "RaidBattleReturned:" + reason;
}
internal interface IClientRaidSettlementActions
{
    Task StartAsync(IClientRaidSettlementRuntime runtime, string reason);
    void Expire(IClientRaidSettlementRuntime runtime, bool closeLocalMap = true);
}
internal interface IClientRaidSettlementRuntime
{
    ClientRaidSettlementIdentity Identity { get; }
    bool IsCurrent { get; }
    TimeSpan FinalRemaining { get; }
    bool TryBegin();
    void KeepFinish();
    void ResetFinish();
    IClientRaidSupportLossSource SupportLosses { get; }
    void EnqueueSaving(Action action, Action discarded);
    void CloseAccepted(string reason);
    void CloseExpired(bool closeLocalMap);
}

internal interface IClientRaidSettlementPresentation
{
    string Text(string key, params (string Name, object? Value)[] arguments);
    string Admission(LocalMutationAdmission admission, LocalMutationState? pending);
    string SupportFailure(ClientRaidSupportLossFailure failure);
    void Status(string message);
    void Message(string message, bool? accepted);
    void Packaging();
    void Diagnose(Exception exception);
}
