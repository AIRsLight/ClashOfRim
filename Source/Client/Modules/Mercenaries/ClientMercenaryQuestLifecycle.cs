using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Quests;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.Modules.Mercenaries;

// Owns quest transitions. Existing incident/confirmation modules keep their
// queue, debt activation, idempotent reports and snapshot recovery.
internal sealed class ClientMercenaryQuestLifecycle
{
    private readonly ClientGameRequestTracker requests;
    private readonly ClientMercenaryIncidents incidents;
    private readonly ClientMercenaryConfirmation confirmation;
    internal ClientMercenaryQuestLifecycle(ClientSessionLifecycle session, IClientSnapshotGame game,
        ClientMercenaryIncidents incidents, ClientMercenaryConfirmation confirmation)
    { requests = new(session, game, static () => { }); this.incidents = incidents; this.confirmation = confirmation; }

    internal Task TickAsync(IClientMercenaryQuest quest)
    {
        using var p = ClientSessionWork.EnterPublication();
        var call = Capture(quest);
        if (!call.IsCurrent || quest.Completed) return Task.CompletedTask;
        if (quest.ShuttleWaiting && SettleShuttle(call, out var settled)) return settled;
        if (!quest.HasPawn)
        {
            if (!call.Step(() => quest.Completed = true)) return Task.CompletedTask;
            quest.End(false, true, true);
            return call.OwnsIdentity ? confirmation.ConfirmAsync() : Task.CompletedTask;
        }
        if (!quest.ArrivalSent && quest.PawnSpawned)
        {
            if (!call.Step(() => quest.ArrivalSent = true)
                || !call.Step(() => quest.Notify(ClientMercenaryQuestNotice.Arrival, quest.PawnIdentity, () => call.IsCurrent))) return Task.CompletedTask;
        }
        Task recall = Task.CompletedTask;
        if (!quest.RecallStarted && ClientQuestTiming.IsExpired(quest.ExpiresAt, quest.GameTicks)) TryRecall(call, out recall);
        if (!call.IsCurrent || quest.Completed || !quest.ShuttleWaiting) return recall;
        if (SettleShuttle(call, out settled)) return settled;
        return Overtime(call);
    }
    internal Task SignalAsync(IClientMercenaryQuest quest, string? tag)
    {
        using var p = ClientSessionWork.EnterPublication();
        var call = Capture(quest);
        if (!call.IsCurrent || quest.Completed || string.IsNullOrWhiteSpace(call.Tag)) return Task.CompletedTask;
        if (string.Equals(tag, call.Tag + ".SurgeryViolation", StringComparison.Ordinal)) return Hostile(call);
        if (!quest.ShuttleWaiting) return Task.CompletedTask;
        if (string.Equals(tag, call.Tag + ".SentSatisfied", StringComparison.Ordinal)) return Complete(call);
        return string.Equals(tag, call.Tag + ".Destroyed", StringComparison.Ordinal) || string.Equals(tag, call.Tag + ".SentUnsatisfied", StringComparison.Ordinal)
            ? Destroyed(call) : Task.CompletedTask;
    }
    internal bool TryStartRecall(IClientMercenaryQuest quest, out Task work)
    {
        using var p = ClientSessionWork.EnterPublication();
        return TryRecall(Capture(quest), out work);
    }
    internal Task HostileAsync(IClientMercenaryQuest quest)
    { using var p = ClientSessionWork.EnterPublication(); return Hostile(Capture(quest)); }
    internal Task KilledAsync(IClientMercenaryQuest quest, object pawn)
    {
        using var p = ClientSessionWork.EnterPublication();
        var call = Capture(quest);
        if (!call.IsCurrent || quest.Completed || quest.DeathReported || !ReferenceEquals(call.Pawn, pawn)) return Task.CompletedTask;
        if (!call.Step(() => { quest.DeathReported = true; quest.Completed = true; })) return Task.CompletedTask;
        Task report = incidents.ReportAsync(call.Contract, "Death");
        if (call.Step(() => quest.Notify(ClientMercenaryQuestNotice.Death, pawn, () => call.IsCurrent))) quest.End(false, false, true);
        return report;
    }
    private bool TryRecall(Call call, out Task work)
    {
        work = Task.CompletedTask; var q = call.Quest;
        if (!call.IsCurrent || q.Completed || q.RecallStarted || !q.HasPawn || !call.Step(() => q.RecallStarted = true)) return false;
        var result = q.Recall(() => call.IsCurrent);
        if (!call.IsCurrent || q.Completed) return result.Recalled;
        if (!call.Step(() => q.Notify(ClientMercenaryQuestNotice.Recall, q.ShuttleTarget ?? call.Pawn, () => call.IsCurrent))) return result.Recalled;
        if (q.Completed) return result.Recalled;
        q.ShuttleWaiting = result.Recalled && result.Waiting;
        if (result.Recalled && !result.Waiting) work = Complete(call);
        return result.Recalled;
    }
    private bool SettleShuttle(Call call, out Task work)
    {
        work = Task.CompletedTask; var q = call.Quest;
        if (!call.IsCurrent || !q.ShuttleWaiting || q.Completed) return false;
        if (q.PawnLoaded && !call.Step(() => q.ShuttleLoaded = true)) return false;
        if (q.ShuttleLoaded && q.ShuttleGone) { work = Complete(call); return true; }
        if (!q.ShuttleLoaded && q.ShuttleDestroyed) { work = Destroyed(call); return true; }
        return false;
    }
    private Task Complete(Call call)
    {
        var q = call.Quest;
        if (!call.IsCurrent || q.Completed || !call.Step(() => { q.ShuttleWaiting = false; q.ClearShuttle(); q.Completed = true; })) return Task.CompletedTask;
        q.End(true, false, false);
        // Ending may remove the quest from the UI. Finalize under the captured
        // identity/Game, rather than requiring it to remain an active quest.
        return call.OwnsIdentity ? confirmation.ConfirmAsync(call.Contract, "mercenary-completed:" + call.Contract) : Task.CompletedTask;
    }
    private Task Hostile(Call call)
    {
        var q = call.Quest;
        if (!call.IsCurrent || q.Completed || q.HostileReported || !call.Step(() => q.HostileReported = true)) return Task.CompletedTask;
        Task report = incidents.ReportAsync(call.Contract, "HostileBehavior");
        call.Step(() => q.Notify(ClientMercenaryQuestNotice.HostileBehavior, call.Pawn, () => call.IsCurrent));
        return report;
    }
    private Task Destroyed(Call call)
    {
        var q = call.Quest;
        if (!call.IsCurrent || q.Completed || q.ShuttleDestroyedReported) return Task.CompletedTask;
        object? target = q.ShuttleTarget ?? call.Pawn;
        if (!call.Step(() => { q.ShuttleDestroyedReported = true; q.ShuttleWaiting = false; q.ClearShuttle(); q.Completed = true; })
            || !call.Step(q.ReleasePawn)) return Task.CompletedTask;
        Task report = incidents.ReportAsync(call.Contract, "ShuttleDestroyed", "mercenary-shuttle-destroyed:" + call.Contract);
        if (call.Step(() => q.Notify(ClientMercenaryQuestNotice.ShuttleDestroyed, target, () => call.IsCurrent))) q.End(false, false, true);
        return report;
    }
    private Task Overtime(Call call)
    {
        var q = call.Quest;
        int days = ClientQuestTiming.OverdueDays(q.ExpiresAt, q.GameTicks);
        if (q.ReportedOvertimeDays >= days) return Task.CompletedTask;
        var reports = new List<Task>();
        while (q.ReportedOvertimeDays < days && call.IsCurrent)
        {
            q.ReportedOvertimeDays++;
            reports.Add(incidents.ReportAsync(call.Contract, "OvertimeService", $"mercenary-overtime:{call.Contract}:{q.ReportedOvertimeDays}"));
        }
        return Task.WhenAll(reports);
    }
    private Call Capture(IClientMercenaryQuest quest)
    {
        var scope = requests.Capture();
        return new(this, quest, scope, quest.ContractId, quest.QuestTag, quest.PawnIdentity);
    }
    private readonly record struct Call(ClientMercenaryQuestLifecycle Owner, IClientMercenaryQuest Quest, ClientGameRequestTracker.Scope Scope, string Contract, string Tag, object? Pawn)
    {
        internal bool OwnsIdentity => Quest.ContractId == Contract && Quest.QuestTag == Tag && ReferenceEquals(Quest.PawnIdentity, Pawn)
            && Owner.requests.TryApply(Scope, static () => { });
        internal bool IsCurrent => Quest.IsCurrent && OwnsIdentity;
        internal bool Step(Action action) { if (!IsCurrent) return false; action(); return IsCurrent; }
    }
}
