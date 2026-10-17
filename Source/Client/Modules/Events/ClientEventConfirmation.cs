using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Events;

// Owns confirmation execution and publication. Domain preparation and accepted effects
// remain with their callers; none of those effects may escape this captured owner.
internal sealed class ClientEventConfirmation
{
    private readonly ClientSessionLifecycle session;
    private readonly IClientSnapshotGame game;
    private readonly ClientSnapshotPublication publication;
    private readonly ClientEventInbox inbox;
    private readonly IClientEventConfirmationProtocol protocol;
    internal ClientEventConfirmation(ClientSessionLifecycle session, IClientSnapshotGame game,
        ClientSnapshotPublication publication, ClientEventInbox inbox, IClientEventConfirmationProtocol protocol)
    { this.session = session; this.game = game; this.publication = publication; this.inbox = inbox; this.protocol = protocol; }

    internal Attempt Capture(ClientSynchronizationCoordinator.Lease admission)
    {
        using var guard = ClientSessionWork.EnterPublication();
        return new(session.CaptureOwnership(), session.ReadContext(), game.BindCurrentGame(), admission);
    }

    internal ClientEventConfirmationCommand Single(Attempt attempt, string prefix, string eventId, string? sourceEventId,
        string? baseSnapshotId, ModSnapshotPackageMetadataDto snapshot, byte[] payload, string applicationResult) => new(
            $"{prefix}:{attempt.Context.UserId}:{attempt.Context.ColonyId}:{eventId}:{snapshot.SnapshotId}",
            eventId, sourceEventId, attempt.Context.UserId, attempt.Context.ColonyId,
            string.IsNullOrWhiteSpace(baseSnapshotId) ? attempt.Context.CurrentSnapshotId ?? string.Empty : baseSnapshotId!, snapshot, payload, applicationResult);

    internal ClientEventBatchConfirmationCommand Batch(Attempt attempt, string prefix, ModSnapshotPackageMetadataDto snapshot,
        byte[] payload, IReadOnlyList<ModConfirmEventApplicationEntryDto> applications)
    {
        var entries = applications.Select(e => new ModConfirmEventApplicationEntryDto
        { EventId = e.EventId, SourceEventId = e.SourceEventId, ClientApplicationResult = e.ClientApplicationResult }).ToArray();
        string digest = string.Join(",", entries.Select(e => e.EventId).OrderBy(id => id, StringComparer.Ordinal));
        return new($"{prefix}:{attempt.Context.UserId}:{attempt.Context.ColonyId}:{digest}:{snapshot.SnapshotId}",
            attempt.Context.CurrentSnapshotId ?? string.Empty, snapshot, payload, entries);
    }

    internal Task ConfirmAsync(Attempt attempt, ClientEventConfirmationCommand command,
        Action<ClientEventConfirmationFailure<ModConfirmEventApplicationResponseDto>> failed,
        Action<ModConfirmEventApplicationResponseDto> accepted) => ExecuteAsync(attempt,
            () => protocol.ConfirmAsync(attempt.Context, command, attempt.Owner.Token), r => r.Result,
            failed, response =>
            {
                if (command.UserId == attempt.Context.UserId && command.ColonyId == attempt.Context.ColonyId)
                    Publish(attempt, response.AppliedSnapshotId, response.NextLineageToken);
                inbox.ForgetGroup(command.EventId);
                attempt.Admission.Dispose();
                accepted(response);
            });

    internal Task ConfirmBatchAsync(Attempt attempt, ClientEventBatchConfirmationCommand command,
        Action<ClientEventConfirmationFailure<ModConfirmEventApplicationsResponseDto>> failed,
        Func<ModConfirmEventApplicationsResponseDto, string?>? selectSnapshot,
        Action<ModConfirmEventApplicationsResponseDto, string?> accepted) => ExecuteAsync(attempt,
            () => protocol.ConfirmBatchAsync(attempt.Context, command, attempt.Owner.Token), r => r.Result,
            failed, response =>
            {
                string? snapshotId = selectSnapshot?.Invoke(response) ?? response.AppliedSnapshotId;
                Publish(attempt, snapshotId, response.NextLineageToken);
                attempt.Admission.Dispose();
                accepted(response, snapshotId);
            });

    private void Publish(Attempt attempt, string? snapshotId, string? lineageToken)
    {
        if (!string.IsNullOrWhiteSpace(snapshotId))
            publication.Publish(attempt.Game, snapshotId!, lineageToken, Array.Empty<ModSnapshotAchievementCandidateDto>());
    }

    private async Task ExecuteAsync<T>(Attempt attempt, Func<Task<ClashOfRimClientNetworkResult<T>>> send,
        Func<T, ModProtocolResponseDto?> serverResult, Action<ClientEventConfirmationFailure<T>> failed, Action<T> accepted)
    {
        try
        {
            if (!TryApply(attempt, static () => { })) return;
            ClashOfRimClientNetworkResult<T> result = await send().ConfigureAwait(false);
            if (!result.Success || result.Response is null)
            {
                TryApply(attempt, () => failed(new(ClientEventConfirmationFailureKind.Transport, result)));
                return;
            }
            ModProtocolResponseDto? response = serverResult(result.Response);
            if (response is { Accepted: false })
            {
                TryApply(attempt, () => failed(new(ClientEventConfirmationFailureKind.Rejected, result, response)));
                return;
            }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = attempt.Owner.Token.Register(() => completion.TrySetCanceled(attempt.Owner.Token));
            if (!TryApply(attempt, () => attempt.Game.Enqueue(() =>
                {
                    try
                    {
                        TryApply(attempt, () =>
                        {
                            // Publication retains admission; the domain callback can start
                            // the next queued refresh only after publication releases it.
                            accepted(result.Response);
                        });
                        completion.TrySetResult();
                    }
                    catch (Exception ex) { completion.TrySetException(ex); throw; }
                }, () => completion.TrySetResult()))) return;
            await completion.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A discarded identity/generation is silent, including cancellation exceptions.
            TryApply(attempt, () => failed(new(ClientEventConfirmationFailureKind.Exception, Exception: ex)), requireBaseSnapshot: false);
        }
        finally { attempt.Admission.Dispose(); }
    }

    private bool TryApply(Attempt attempt, Action action, bool requireBaseSnapshot = true)
    {
        bool applied = false;
        ClientSessionWork.TryApply(() =>
        {
            if (!session.Owns(attempt.Owner) || !attempt.Admission.IsCurrent) return;
            var current = session.ReadContext();
            var expected = attempt.Context;
            if (current.ServerBaseUrl != expected.ServerBaseUrl || current.UserId != expected.UserId
                || current.ColonyId != expected.ColonyId || current.AuthToken != expected.AuthToken
                || (requireBaseSnapshot && current.CurrentSnapshotId != expected.CurrentSnapshotId)) return;
            action();
            applied = true;
        });
        return applied;
    }

    internal sealed record Attempt(ClientSessionLifecycle.Ownership Owner, ClashOfRimClientNetworkContext Context,
        IClientSnapshotGameSession Game, ClientSynchronizationCoordinator.Lease Admission);
}

internal enum ClientEventConfirmationFailureKind { Transport, Rejected, Exception }
internal sealed record ClientEventConfirmationFailure<T>(ClientEventConfirmationFailureKind Kind,
    ClashOfRimClientNetworkResult<T>? Network = null, ModProtocolResponseDto? Rejection = null, Exception? Exception = null)
{
    internal bool CanRetry => Kind switch
    {
        ClientEventConfirmationFailureKind.Transport => ClientNetworkFailurePolicy.CanRetry(Network?.ErrorCode, Network?.Message),
        ClientEventConfirmationFailureKind.Exception => Exception is not null && ClientNetworkFailurePolicy.CanRetry(Exception),
        _ => false
    };
}
