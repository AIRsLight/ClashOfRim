using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.Modules.Support;

internal interface IClientSupportQuest
{
    bool IsCurrent { get; }
    string EventId { get; }
    bool Completed { get; }
    bool Permanent { get; }
    bool HasAssignment { get; }
    bool Expired { get; }
    bool FinishRequested { get; }
    string FinishReason { get; }
    void Complete();
    void MarkDeparting(string reason);
}

// Persisted quest fields stay in the engine part. This module owns transitions;
// departure, outbox capture, confirmation and reporting remain in SupportReturns.
internal sealed class ClientSupportQuestLifecycle
{
    private readonly ClientGameRequestTracker requests;
    private readonly ClientSupportReturns returns;
    internal ClientSupportQuestLifecycle(ClientSessionLifecycle session, IClientSnapshotGame game, ClientSupportReturns returns)
    { requests = new(session, game, static () => { }); this.returns = returns; }

    internal Task TickAsync(IClientSupportQuest quest)
    {
        using var p = ClientSessionWork.EnterPublication();
        var call = Capture(quest);
        if (!call.IsCurrent || quest.Completed || string.IsNullOrWhiteSpace(call.EventId)) return Task.CompletedTask;
        if (quest.Permanent || !quest.HasAssignment)
        {
            if (call.IsCurrent) quest.Complete();
            return Task.CompletedTask;
        }
        if (quest.FinishRequested)
        {
            string reason = string.IsNullOrWhiteSpace(quest.FinishReason) ? "QuestExpired" : quest.FinishReason;
            return call.IsCurrent ? returns.ConfirmAsync(call.EventId, reason) ?? Task.CompletedTask : Task.CompletedTask;
        }
        if (quest.Expired) Depart(call, "QuestExpired");
        return Task.CompletedTask;
    }
    internal bool TryStartEarlyReturn(IClientSupportQuest quest)
    {
        using var p = ClientSessionWork.EnterPublication();
        return Depart(Capture(quest), "EarlyReturn");
    }
    private bool Depart(Call call, string reason)
    {
        var quest = call.Quest;
        if (!call.IsCurrent || quest.Completed || quest.Permanent || quest.FinishRequested
            || !returns.TryStartDeparture(call.EventId, reason) || !call.IsCurrent) return false;
        quest.MarkDeparting(reason);
        return true;
    }
    private Call Capture(IClientSupportQuest quest)
    {
        var scope = requests.Capture();
        return new(this, quest, scope, quest.EventId);
    }
    private readonly record struct Call(ClientSupportQuestLifecycle Owner, IClientSupportQuest Quest, ClientGameRequestTracker.Scope Scope, string EventId)
    {
        internal bool IsCurrent => Quest.IsCurrent && Quest.EventId == EventId && Owner.requests.TryApply(Scope, static () => { });
    }
}
