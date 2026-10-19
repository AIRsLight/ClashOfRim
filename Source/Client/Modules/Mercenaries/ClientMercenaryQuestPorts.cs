using System;

namespace AIRsLight.ClashOfRim.Modules.Mercenaries;

internal enum ClientMercenaryQuestNotice { Arrival, Recall, HostileBehavior, ShuttleDestroyed, Death }
internal readonly record struct ClientMercenaryRecallResult(bool Recalled, bool Waiting);
internal interface IClientMercenaryQuest
{
    bool IsCurrent { get; }
    string ContractId { get; }
    string QuestTag { get; }
    object? PawnIdentity { get; }
    object? ShuttleTarget { get; }
    bool HasPawn { get; }
    bool PawnSpawned { get; }
    bool PawnLoaded { get; }
    bool ShuttleGone { get; }
    bool ShuttleDestroyed { get; }
    long ExpiresAt { get; }
    long GameTicks { get; }
    bool Completed { get; set; }
    bool RecallStarted { get; set; }
    bool ShuttleWaiting { get; set; }
    bool ShuttleLoaded { get; set; }
    bool ArrivalSent { get; set; }
    bool HostileReported { get; set; }
    bool DeathReported { get; set; }
    bool ShuttleDestroyedReported { get; set; }
    int ReportedOvertimeDays { get; set; }
    ClientMercenaryRecallResult Recall(Func<bool> current);
    void ClearShuttle();
    void ReleasePawn();
    void Notify(ClientMercenaryQuestNotice notice, object? target, Func<bool> current);
    void End(bool success, bool sendLetter, bool playSound);
}
