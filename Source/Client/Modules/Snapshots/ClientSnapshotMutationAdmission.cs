using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using System.Threading;
using System.Threading.Tasks;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

// Shared admission for commands that change the game before submitting a snapshot.
// Retries retain the original identity, base and Game, replacing only their exact lease.
internal sealed class ClientSnapshotMutationAdmission
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientSnapshotTransaction transactions;
    internal ClientSnapshotMutationAdmission(ClientSessionLifecycle session, ClientSynchronizationCoordinator synchronization,
        ClientLocalMutationCoordinator mutations, ClientSnapshotTransaction transactions)
    { this.session = session; this.synchronization = synchronization; this.mutations = mutations; this.transactions = transactions; }

    internal bool CanBegin
    {
        get
        {
            using var publication = ClientSessionWork.EnterPublication();
            return Check(null) == LocalMutationAdmission.Allowed && !synchronization.IsBusy;
        }
    }

    internal ClientSnapshotAdmissionResult Begin(LocalMutationKind? continuation = null)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var check = Check(continuation);
        if (check != LocalMutationAdmission.Allowed) return new(null, ClientSnapshotAdmissionFailure.Admission, check);
        if (!synchronization.TryBeginManual(out var lease)) return new(null, ClientSnapshotAdmissionFailure.Busy);
        try { return new(transactions.Capture(lease)); }
        catch { lease.Dispose(); throw; }
    }

    internal ClientSnapshotAdmissionResult Retry(ClientSnapshotTransaction.Attempt original, LocalMutationKind operation)
    {
        using var publication = ClientSessionWork.EnterPublication();
        if (!original.Game.IsCurrent || !session.Owns(original.Owner, original.Context, false))
            return new(null, ClientSnapshotAdmissionFailure.Obsolete);
        if (!session.Owns(original.Owner, original.Context, true))
            return new(null, ClientSnapshotAdmissionFailure.BaseSnapshotChanged);
        var admitted = Begin(operation);
        return admitted.Attempt is { } attempt ? new(original with { Admission = attempt.Admission }) : admitted;
    }

    // Queued domain work waits without owning a lease. Recheck pending changes
    // after acquiring snapshot admission, since another mutation may have won.
    internal async Task<ClientSnapshotAdmissionResult> WaitAsync(CancellationToken token)
    {
        while (true)
        {
            await mutations.WaitUntilClearAsync(token).ConfigureAwait(false);
            var lease = await synchronization.AcquireSnapshotAsync(token).ConfigureAwait(false);
            using var p = ClientSessionWork.EnterPublication();
            if (mutations.IsPending) { lease.Dispose(); continue; }
            var check = Check(null);
            if (check != LocalMutationAdmission.Allowed)
            { lease.Dispose(); return new(null, ClientSnapshotAdmissionFailure.Admission, check); }
            try { return new(transactions.Capture(lease)); }
            catch { lease.Dispose(); throw; }
        }
    }

    private LocalMutationAdmission Check(LocalMutationKind? continuation)
    {
        var context = session.ReadContext();
        return mutations.CheckAdmission(context.IsConfigured, !string.IsNullOrWhiteSpace(context.CurrentSnapshotId), continuation);
    }
}

internal enum ClientSnapshotAdmissionFailure { None, Admission, Busy, Obsolete, BaseSnapshotChanged }
internal sealed record ClientSnapshotAdmissionResult(ClientSnapshotTransaction.Attempt? Attempt,
    ClientSnapshotAdmissionFailure Failure = ClientSnapshotAdmissionFailure.None,
    LocalMutationAdmission Admission = LocalMutationAdmission.Allowed);
