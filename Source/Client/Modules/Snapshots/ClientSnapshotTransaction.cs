using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

// Common lifetime for snapshot-backed domain commits: captured ownership, transport completion,
// game-thread publication and the exact synchronization lease. Domains retain their own effects.
internal sealed class ClientSnapshotTransaction
{
    private readonly ClientSessionLifecycle session;
    private readonly IClientSnapshotGame game;
    private readonly ClientSnapshotPublication publication;

    internal ClientSnapshotTransaction(
    ClientSessionLifecycle session, IClientSnapshotGame game, ClientSnapshotPublication publication)
    {
        this.session = session;
        this.game = game;
        this.publication = publication;
    }

    internal Attempt Capture(ClientSynchronizationCoordinator.Lease admission)
    {
        using var guard = ClientSessionWork.EnterPublication();
        return new(session.CaptureOwnership(), session.ReadContext(), game.BindCurrentGame(), admission);
    }

    internal void Publish(Attempt attempt, string? snapshotId, string? lineageToken)
    {
        if (!string.IsNullOrWhiteSpace(snapshotId))
            publication.Publish(attempt.Game, snapshotId!, lineageToken, Array.Empty<ModSnapshotAchievementCandidateDto>());
    }

    internal Task ExecuteAsync<T>(Attempt attempt, Func<Task<ClashOfRimClientNetworkResult<T>>> send,
        Func<T, ModProtocolResponseDto?> serverResult, Action<ClientSnapshotTransactionFailure<T>> failed, Action<T> accepted)
        => ExecuteAsync(attempt, send, serverResult, failure => { failed(failure); return Task.CompletedTask; }, accepted);

    // Async failure effects retain the same lease until their owned game callback completes.
    internal async Task ExecuteAsync<T>(Attempt attempt, Func<Task<ClashOfRimClientNetworkResult<T>>> send,
        Func<T, ModProtocolResponseDto?> serverResult, Func<ClientSnapshotTransactionFailure<T>, Task> failed, Action<T> accepted)
    {
        bool publicationStarted = false;
        try
        {
            if (!TryApply(attempt, static () => { })) return;
            ClashOfRimClientNetworkResult<T> result = await send().ConfigureAwait(false);
            if (!result.Success || result.Response is null)
            {
                await ReportFailure(new(ClientSnapshotTransactionFailureKind.Transport, result)).ConfigureAwait(false);
                return;
            }
            ModProtocolResponseDto? response = serverResult(result.Response);
            if (response is { Accepted: false })
            {
                await ReportFailure(new(ClientSnapshotTransactionFailureKind.Rejected, result, response)).ConfigureAwait(false);
                return;
            }
            await ClientGamePublication.EnqueueAsync(attempt.Game.Enqueue, attempt.Owner.Token,
                action => TryApply(attempt, action), () =>
                {
                    // Retain admission until publication starts on the owning game.
                    publicationStarted = true;
                    accepted(result.Response);
                }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Only our own partial publication may advance the base snapshot before
            // throwing. A late HTTP exception must still match the original base.
            await ReportFailure(new(ClientSnapshotTransactionFailureKind.Exception, Exception: ex)).ConfigureAwait(false);
        }
        finally { attempt.Admission.Dispose(); }
        Task ReportFailure(ClientSnapshotTransactionFailure<T> failure)
        {
            Task work = Task.CompletedTask;
            TryApply(attempt, () => work = failed(failure), requireBaseSnapshot: !publicationStarted);
            return work;
        }
    }

    internal bool TryApply(Attempt attempt, Action action, bool requireBaseSnapshot = true)
    {
        bool applied = false;
        ClientSessionWork.TryApply(() =>
        {
            if (!attempt.Admission.IsCurrent || !attempt.Game.IsCurrent || !session.Owns(attempt.Owner, attempt.Context, requireBaseSnapshot)) return;
            action();
            applied = true;
        });
        return applied;
    }

    // Capture while still owning admission. Dependent domains start after release,
    // but must retain the original identity/Game and the explicitly accepted snapshot.
    internal Func<Action, bool> CaptureCompletion(Attempt attempt, string? acceptedSnapshot, Func<bool>? current = null)
    {
        using var p = ClientSessionWork.EnterPublication();
        bool accepted = TryApply(attempt, static () => { }, requireBaseSnapshot: false)
            && session.ReadContext().CurrentSnapshotId == acceptedSnapshot && current?.Invoke() != false;
        bool Current() => accepted && attempt.Game.IsCurrent && session.Owns(attempt.Owner, attempt.Context, false)
            && session.ReadContext().CurrentSnapshotId == acceptedSnapshot && current?.Invoke() != false;
        return action =>
        {
            using var publication = ClientSessionWork.EnterPublication();
            if (!Current()) return false;
            action(); return Current();
        };
    }

    internal sealed record Attempt(ClientSessionLifecycle.Ownership Owner, ClashOfRimClientNetworkContext Context,
        IClientSnapshotGameSession Game, ClientSynchronizationCoordinator.Lease Admission);
}

internal enum ClientSnapshotTransactionFailureKind { Transport, Rejected, Exception }
internal sealed record ClientSnapshotTransactionFailure<T>(ClientSnapshotTransactionFailureKind Kind,
    ClashOfRimClientNetworkResult<T>? Network = null, ModProtocolResponseDto? Rejection = null, Exception? Exception = null)
{
    internal bool CanRetry => Kind switch
    {
        ClientSnapshotTransactionFailureKind.Transport => ClientNetworkFailurePolicy.CanRetry(Network?.ErrorCode, Network?.Message),
        ClientSnapshotTransactionFailureKind.Exception => Exception is not null && ClientNetworkFailurePolicy.CanRetry(Exception),
        _ => false
    };
}
