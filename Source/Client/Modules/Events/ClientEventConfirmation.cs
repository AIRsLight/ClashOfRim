using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Events;

// Owns event commands and receipt effects. The shared snapshot transaction owns
// transport completion, publication admission and captured session/Game checks.
internal sealed class ClientEventConfirmation
{
    private readonly ClientSnapshotTransaction transactions;
    private readonly ClientEventInbox inbox;
    private readonly IClientEventConfirmationProtocol protocol;
    internal ClientEventConfirmation(ClientSessionLifecycle session, IClientSnapshotGame game,
        ClientSnapshotPublication publication, ClientEventInbox inbox, IClientEventConfirmationProtocol protocol)
    { transactions = new ClientSnapshotTransaction(session, game, publication); this.inbox = inbox; this.protocol = protocol; }

    internal ClientEventConfirmationCommand Single(ClientSnapshotTransaction.Attempt attempt, string prefix, string eventId, string? sourceEventId,
        string? baseSnapshotId, ModSnapshotPackageMetadataDto snapshot, byte[] payload, string applicationResult) => new(
            $"{prefix}:{attempt.Context.UserId}:{attempt.Context.ColonyId}:{eventId}:{snapshot.SnapshotId}",
            eventId, sourceEventId, attempt.Context.UserId, attempt.Context.ColonyId,
            string.IsNullOrWhiteSpace(baseSnapshotId) ? attempt.Context.CurrentSnapshotId ?? string.Empty : baseSnapshotId!, snapshot, payload, applicationResult);

    internal ClientEventBatchConfirmationCommand Batch(ClientSnapshotTransaction.Attempt attempt, string prefix, ModSnapshotPackageMetadataDto snapshot,
        byte[] payload, IReadOnlyList<ModConfirmEventApplicationEntryDto> applications)
    {
        var entries = CopyEntries(applications);
        string digest = string.Join(",", entries.Select(e => e.EventId).OrderBy(id => id, StringComparer.Ordinal));
        return new($"{prefix}:{attempt.Context.UserId}:{attempt.Context.ColonyId}:{digest}:{snapshot.SnapshotId}",
            attempt.Context.CurrentSnapshotId ?? string.Empty, snapshot, payload, entries);
    }

    internal static ModConfirmEventApplicationEntryDto[] CopyEntries(IReadOnlyList<ModConfirmEventApplicationEntryDto> applications) =>
        applications.Select(e => new ModConfirmEventApplicationEntryDto
        { EventId = e.EventId, SourceEventId = e.SourceEventId, ClientApplicationResult = e.ClientApplicationResult }).ToArray();

    internal Task ConfirmAsync(ClientSnapshotTransaction.Attempt attempt, ClientEventConfirmationCommand command,
        Action<ClientSnapshotTransactionFailure<ModConfirmEventApplicationResponseDto>> failed,
        Action<ModConfirmEventApplicationResponseDto> accepted) => transactions.ExecuteAsync(attempt,
            () => protocol.ConfirmAsync(attempt.Context, command, attempt.Owner.Token), r => r.Result,
            failed, response =>
            {
                if (command.UserId == attempt.Context.UserId && command.ColonyId == attempt.Context.ColonyId)
                    transactions.Publish(attempt, response.AppliedSnapshotId, response.NextLineageToken);
                inbox.ForgetGroup(command.EventId);
                attempt.Admission.Dispose();
                accepted(response);
            });

    internal Task ConfirmBatchAsync(ClientSnapshotTransaction.Attempt attempt, ClientEventBatchConfirmationCommand command,
        Action<ClientSnapshotTransactionFailure<ModConfirmEventApplicationsResponseDto>> failed,
        Func<ModConfirmEventApplicationsResponseDto, string?>? selectSnapshot,
        Action<ModConfirmEventApplicationsResponseDto, string?> accepted) => transactions.ExecuteAsync(attempt,
            () => protocol.ConfirmBatchAsync(attempt.Context, command, attempt.Owner.Token), r => r.Result,
            failed, response =>
            {
                string? snapshotId = selectSnapshot?.Invoke(response) ?? response.AppliedSnapshotId;
                transactions.Publish(attempt, snapshotId, response.NextLineageToken);
                attempt.Admission.Dispose();
                accepted(response, snapshotId);
            });

    internal ClientSnapshotTransaction.Attempt Capture(ClientSynchronizationCoordinator.Lease admission) => transactions.Capture(admission);
    internal bool TryApply(ClientSnapshotTransaction.Attempt attempt, Action action, bool requireBaseSnapshot = true) =>
        transactions.TryApply(attempt, action, requireBaseSnapshot);
}
