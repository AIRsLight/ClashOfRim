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

    internal async Task ExecuteAsync<T>(Attempt attempt, Func<Task<ClashOfRimClientNetworkResult<T>>> send,
        Func<T, ModProtocolResponseDto?> serverResult, Action<ClientSnapshotTransactionFailure<T>> failed, Action<T> accepted)
    {
        bool publicationStarted = false;
        try
        {
            if (!TryApply(attempt, static () => { })) return;
            ClashOfRimClientNetworkResult<T> result = await send().ConfigureAwait(false);
            if (!result.Success || result.Response is null)
            {
                TryApply(attempt, () => failed(new(ClientSnapshotTransactionFailureKind.Transport, result)));
                return;
            }
            ModProtocolResponseDto? response = serverResult(result.Response);
            if (response is { Accepted: false })
            {
                TryApply(attempt, () => failed(new(ClientSnapshotTransactionFailureKind.Rejected, result, response)));
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
            TryApply(attempt, () => failed(new(ClientSnapshotTransactionFailureKind.Exception, Exception: ex)), requireBaseSnapshot: !publicationStarted);
        }
        finally { attempt.Admission.Dispose(); }
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
