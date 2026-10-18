using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Raids;

internal interface IClientRaidRecoveredBattle : IClientRaidSettlementRuntime
{
    bool HasMap { get; }
    void RestoreDeadlines(ClientRaidServerDeadlines deadlines);
}
internal interface IClientRaidRecoveryRuntime
{
    bool Ready { get; }
    IClientRaidRecoveredBattle? ReadBattle();
}
internal enum ClientRaidRecoveryState { Waiting, ServerSettled, MissingBattle, InvalidTime, Restored, Exception }
internal sealed record ClientRaidRecoveryNotice(ClientRaidRecoveryState State, string EventId = "", int RemainingSeconds = 0, Exception? Exception = null);
internal interface IClientRaidRecoveryPresentation
{
    string Format(ClientRaidRecoveryNotice notice);
    void Report(ClientRaidRecoveryNotice notice, string message);
}

// Recovery belongs to one loaded game and authoritative snapshot. Readiness is
// advanced by engine updates, never by re-enqueueing into the draining game queue.
internal sealed class ClientRaidRecovery
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientGameRequestTracker requests;
    private readonly ClientSnapshotFailureRecovery failureRecovery;
    private readonly IClientRaidSettlementActions settlements;
    private readonly IClientRaidRecoveryPresentation presentation;
    private Pending? pending;
    private bool awaitingResolution;
    internal ClientRaidRecovery(ClientSessionLifecycle session, IClientSnapshotGame game, ClientSnapshotFailureRecovery failureRecovery,
        IClientRaidSettlementActions settlements, IClientRaidRecoveryPresentation presentation)
    {
        this.session = session; this.failureRecovery = failureRecovery; this.settlements = settlements; this.presentation = presentation;
        requests = new(session, game, () => { pending = null; awaitingResolution = false; });
    }
    // This signal still connects to the authoritative reload workflow pending M7d5b.
    internal event Action<string>? ReloadRequested;
    internal bool BlocksBattleLifecycle => requests.Read(() => awaitingResolution);

    internal void Reconcile(IClientRaidRecoveryRuntime runtime, ModActiveRaidRecoveryDto? recovery)
    {
        using var p = ClientSessionWork.EnterPublication();
        requests.Reset();
        var scope = requests.Capture();
        if (!scope.Game.HasGame) return;
        awaitingResolution = true;
        pending = new(scope, runtime, recovery is null ? null : new(recovery.EventId, recovery.ServerNowUtc,
            recovery.StartedAtUtc, recovery.DeadlineUtc, recovery.FinalDeadlineUtc));
        Advance();
    }
    internal void Advance()
    {
        var job = pending;
        if (job is null) return;
        Apply(job, () =>
        {
            try
            {
                if (!job.Runtime.Ready)
                {
                    if (!job.WaitingReported) { job.WaitingReported = true; Report(new(ClientRaidRecoveryState.Waiting, job.Recovery?.EventId ?? "")); }
                    return;
                }
                pending = null; // Consume before effects or reentrant recovery requests.
                ReconcileReady(job);
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(pending, job)) pending = null;
                Apply(job, () => Fail(new(ClientRaidRecoveryState.Exception, job.Recovery?.EventId ?? "", Exception: ex)));
            }
        });
        if (!session.Owns(job.Scope.Owner, job.Scope.Context, true) && ReferenceEquals(pending, job))
        { pending = null; awaitingResolution = false; }
    }
    private void ReconcileReady(Pending job)
    {
        var battle = job.Runtime.ReadBattle();
        var recovery = job.Recovery;
        if (battle is not null && (!battle.IsCurrent || battle.Identity.AttackerUserId != job.Scope.Context.UserId
            || battle.Identity.AttackerColonyId != job.Scope.Context.ColonyId))
        { Fail(new(ClientRaidRecoveryState.MissingBattle, recovery?.EventId ?? battle.Identity.EventId)); return; }
        if (recovery is null)
        {
            if (battle is null) { awaitingResolution = false; return; }
            Report(new(ClientRaidRecoveryState.ServerSettled, battle.Identity.EventId));
            ReloadRequested?.Invoke(battle.Identity.EventId);
            return;
        }
        if (battle is null || recovery.EventId != battle.Identity.EventId || !battle.HasMap)
        { Fail(new(ClientRaidRecoveryState.MissingBattle, recovery.EventId)); return; }
        var deadlines = ClientRaidServerDeadlines.Read(recovery.Started, recovery.Deadline, recovery.Final);
        if (deadlines is null || !ClientServerTime.TryParseUtc(recovery.Now, out var now))
        { Fail(new(ClientRaidRecoveryState.InvalidTime, recovery.EventId)); return; }
        battle.RestoreDeadlines(deadlines);
        Apply(job, () =>
        {
            if (!battle.IsCurrent) return;
            awaitingResolution = false;
            if (now.UtcDateTime.Ticks >= deadlines.FinalDeadlineUtcTicks) settlements.Expire(battle);
            else if (now.UtcDateTime.Ticks >= deadlines.DeadlineUtcTicks) _ = settlements.StartAsync(battle, "RecoveredExpired");
            else
            {
                var seconds = TimeSpan.FromTicks(deadlines.DeadlineUtcTicks - now.UtcDateTime.Ticks).TotalSeconds;
                Report(new(ClientRaidRecoveryState.Restored, recovery.EventId, (int)Math.Clamp(Math.Ceiling(seconds), 0, int.MaxValue)));
            }
        });
    }
    private void Apply(Pending job, Action action) => requests.TryApply(job.Scope, () =>
    { if (session.Owns(job.Scope.Owner, job.Scope.Context, true)) action(); });
    private string Report(ClientRaidRecoveryNotice notice)
    { string message = presentation.Format(notice); presentation.Report(notice, message); return message; }
    private void Fail(ClientRaidRecoveryNotice notice)
    {
        string message = Report(notice);
        failureRecovery.Show(LocalMutationKind.RaidRestore, message, () => ReloadRequested?.Invoke(notice.EventId));
    }
    private sealed record Recovery(string EventId, string Now, string Started, string Deadline, string Final);
    private sealed class Pending
    {
        internal Pending(ClientGameRequestTracker.Scope scope, IClientRaidRecoveryRuntime runtime, Recovery? recovery)
        { Scope = scope; Runtime = runtime; Recovery = recovery; }
        internal readonly ClientGameRequestTracker.Scope Scope;
        internal readonly IClientRaidRecoveryRuntime Runtime;
        internal readonly Recovery? Recovery;
        internal bool WaitingReported;
    }
}
