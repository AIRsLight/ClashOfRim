using System;
using System.Globalization;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Events;

internal enum ClientEventDecisionKind { RejectGift, RejectSupport, AcceptDiplomacy, RejectDiplomacy, ReportFailure }
internal enum ClientEventDecisionStage { AdmissionFailed, Busy, Started, TransportFailed, Rejected, Exception, Accepted }
internal sealed record ClientEventDecisionNotice(ClientEventDecisionKind Kind, ClientEventDecisionStage Stage, string EventId,
    string? Code = null, string? Message = null, Exception? Exception = null,
    LocalMutationAdmission Admission = LocalMutationAdmission.Allowed, LocalMutationState? Pending = null,
    ClientEventDecisionReceipt? Receipt = null);
internal sealed record ClientEventDecisionReceipt(ModProtocolResponseDto? Result, string? ReturnEventId = null,
    bool ReturnEventCreated = false, string? RelationKind = null, int AffectedEventCount = 0);
internal interface IClientEventDecisionPresentation { void Report(ClientEventDecisionNotice notice); }
internal interface IClientEventDecisionRuntime
{
    void Enqueue(Action action, Action onDiscard);
    void MarkHandled(string eventId);
    void ApplyDiplomacyRelation(string userId, string? relationKind, ServerEventType eventType);
}

// These commands change server event decisions, not local snapshots. All commands
// capture one identity before dispatch; a response never borrows a later login.
internal sealed class ClientEventDecisions : IClientEventApplicationDecisions
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly IClientSnapshotGame game;
    private readonly IClientEventDecisionProtocol protocol;
    private readonly IClientEventDecisionPresentation presentation;
    private readonly IClientEventDecisionRuntime runtime;

    internal ClientEventDecisions(ClientSessionLifecycle session, ClientSynchronizationCoordinator synchronization,
        ClientLocalMutationCoordinator mutations, IClientSnapshotGame game, IClientEventDecisionProtocol protocol,
        IClientEventDecisionPresentation presentation, IClientEventDecisionRuntime runtime)
    {
        this.session = session; this.synchronization = synchronization; this.mutations = mutations;
        this.game = game; this.protocol = protocol; this.presentation = presentation; this.runtime = runtime;
    }

    internal event Action? FailureReported;

    public bool RejectGift(string eventId, string? reason, out Task work) => Start(ClientEventDecisionKind.RejectGift, eventId,
        a => protocol.RejectGiftAsync(a.Context, eventId, reason, a.Owner.Token),
        r => new(r.Result, r.ReturnEventId, r.ReturnEventCreated), null, out work);

    public bool RejectSupport(string eventId, string? reason, out Task work) => Start(ClientEventDecisionKind.RejectSupport, eventId,
        a => protocol.RejectSupportAsync(a.Context, eventId, reason, a.Owner.Token),
        r => new(r.Result, r.ReturnEventId, r.ReturnEventCreated), null, out work);

    public bool RespondDiplomacy(string eventId, string actorUserId, ServerEventType eventType, bool accepted, string? reason, out Task work) =>
        Start(accepted ? ClientEventDecisionKind.AcceptDiplomacy : ClientEventDecisionKind.RejectDiplomacy, eventId,
            a => protocol.RespondDiplomacyAsync(a.Context, eventId, accepted, reason, a.Owner.Token),
            r => new(r.Result, RelationKind: r.RelationKind),
            r =>
            {
                // The receipt may be a duplicate of an earlier, different decision.
                // Apply authoritative relation data instead of deriving it from the clicked button.
                if (accepted || r.RelationKind is not null) runtime.ApplyDiplomacyRelation(actorUserId, r.RelationKind, eventType);
            }, out work);

    public Task ReportFailureAsync(string eventId, string? sourceEventId, string reason)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var context = session.ReadContext();
        if (string.IsNullOrWhiteSpace(eventId) || !context.IsConfigured) return Task.CompletedTask;
        var attempt = Capture(context, null);
        var report = new ClientEventFailureReport(
            $"event-application-failure:{context.UserId}:{eventId}:{DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)}",
            eventId, sourceEventId, context.CurrentSnapshotId ?? string.Empty, reason);
        return ClientSessionWork.Run(() => ExecuteAsync(attempt, ClientEventDecisionKind.ReportFailure, eventId,
            () => protocol.ReportFailureAsync(context, report, attempt.Owner.Token),
            r => new(r.Result, AffectedEventCount: r.AffectedEventCount), null));
    }

    private bool Start<T>(ClientEventDecisionKind kind, string eventId, Func<Attempt, Task<ClashOfRimClientNetworkResult<T>>> send,
        Func<T, ClientEventDecisionReceipt> receipt, Action<ClientEventDecisionReceipt>? accepted, out Task work)
    {
        using var publication = ClientSessionWork.EnterPublication();
        work = Task.CompletedTask;
        var context = session.ReadContext();
        var admission = mutations.CheckAdmission(context.IsConfigured, !string.IsNullOrWhiteSpace(context.CurrentSnapshotId));
        if (admission != LocalMutationAdmission.Allowed)
        {
            presentation.Report(new(kind, ClientEventDecisionStage.AdmissionFailed, eventId, Admission: admission, Pending: mutations.Current));
            return false;
        }
        if (!synchronization.TryBeginManual(out var lease))
        {
            presentation.Report(new(kind, ClientEventDecisionStage.Busy, eventId));
            return false;
        }
        try
        {
            var attempt = Capture(context, lease);
            presentation.Report(new(kind, ClientEventDecisionStage.Started, eventId));
            work = ClientSessionWork.Run(() => ExecuteAsync(attempt, kind, eventId, () => send(attempt), receipt, accepted));
            return true;
        }
        catch { lease.Dispose(); throw; }
    }

    private Attempt Capture(ClashOfRimClientNetworkContext context, ClientSynchronizationCoordinator.Lease? lease) =>
        new(session.CaptureOwnership(), context, game.BindCurrentGame(), lease);

    private async Task ExecuteAsync<T>(Attempt attempt, ClientEventDecisionKind kind, string eventId,
        Func<Task<ClashOfRimClientNetworkResult<T>>> send, Func<T, ClientEventDecisionReceipt> project,
        Action<ClientEventDecisionReceipt>? accepted)
    {
        try
        {
            if (!TryApply(attempt, static () => { })) return;
            var result = await send().ConfigureAwait(false);
            if (!result.Success || result.Response is null)
            {
                TryApply(attempt, () => presentation.Report(new(kind, ClientEventDecisionStage.TransportFailed, eventId, result.ErrorCode, result.Message)));
                return;
            }
            var receipt = project(result.Response);
            if (receipt.Result is { Accepted: false } rejection)
            {
                TryApply(attempt, () => presentation.Report(new(kind, ClientEventDecisionStage.Rejected, eventId,
                    rejection.ErrorCode.ToString(CultureInfo.InvariantCulture), rejection.Message)));
                return;
            }
            if (kind == ClientEventDecisionKind.ReportFailure)
            {
                TryApply(attempt, () =>
                {
                    presentation.Report(new(kind, ClientEventDecisionStage.Accepted, eventId, Receipt: receipt));
                    FailureReported?.Invoke();
                });
                return;
            }
            await ClientGamePublication.EnqueueAsync(runtime.Enqueue, attempt.Owner.Token, action => TryApply(attempt, action), () =>
            {
                attempt.Admission!.Dispose();
                accepted?.Invoke(receipt);
                runtime.MarkHandled(eventId);
                presentation.Report(new(kind, ClientEventDecisionStage.Accepted, eventId, Receipt: receipt));
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TryApply(attempt, () => presentation.Report(new(kind, ClientEventDecisionStage.Exception, eventId,
                ex.GetType().Name, ex.Message, ex)));
        }
        finally { attempt.Admission?.Dispose(); }
    }

    private bool TryApply(Attempt attempt, Action action)
    {
        bool applied = false;
        ClientSessionWork.TryApply(() =>
        {
            if (attempt.Admission is { IsCurrent: false } || !attempt.Game.IsCurrent
                || !session.Owns(attempt.Owner, attempt.Context, requireSnapshot: attempt.Admission is not null)) return;
            action(); applied = true;
        });
        return applied;
    }

    private sealed record Attempt(ClientSessionLifecycle.Ownership Owner, ClashOfRimClientNetworkContext Context,
        IClientSnapshotGameSession Game, ClientSynchronizationCoordinator.Lease? Admission);
}
