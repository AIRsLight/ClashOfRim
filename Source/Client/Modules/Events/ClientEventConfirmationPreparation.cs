using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Events;

// Admission and preparation precede both single and batch confirmation. Prepared
// raid submissions reuse their existing lease and package, without capturing again.
internal sealed class ClientEventConfirmationPreparation
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientEventConfirmation confirmation;
    internal ClientEventConfirmationPreparation(ClientSessionLifecycle session, ClientSynchronizationCoordinator synchronization,
        ClientLocalMutationCoordinator mutations, ClientEventConfirmation confirmation)
    { this.session = session; this.synchronization = synchronization; this.mutations = mutations; this.confirmation = confirmation; }

    internal ClientEventPreparationResult Current(LocalMutationKind operation, string status)
    {
        var started = Begin(operation, status, requireSnapshot: true, continuation: null);
        if (started.Attempt is not { } attempt) return started;
        bool handedOff = false;
        try
        {
            bool built = ClientSnapshotPreparation.TryBuild(attempt.Game, attempt.Context.UserId, attempt.Context.ColonyId,
                out var build, out var failure);
            if (!confirmation.TryApply(attempt, static () => { })) return Obsolete(operation);
            if (!built) return new(operation, Failure: new(ClientEventPreparationFailureKind.Build, SnapshotFailure: failure));
            handedOff = true;
            return started with { Build = build };
        }
        catch (Exception ex)
        {
            if (!confirmation.TryApply(attempt, static () => { })) return Obsolete(operation);
            return new(operation, Failure: new(ClientEventPreparationFailureKind.Build,
                SnapshotFailure: new("SaveToMemoryException", $"{ex.GetType().Name} {ex.Message}", SnapshotUploadMessage.SaveFailed)));
        }
        finally { if (!handedOff) attempt.Admission.Dispose(); }
    }

    internal ClientEventPreparationResult Prepared(LocalMutationKind operation, string status,
        ModSnapshotPackageMetadataDto? snapshot, byte[] payload, ClientSynchronizationCoordinator.Lease? continuation)
    {
        if (snapshot is null || payload.Length == 0)
        {
            continuation?.Dispose();
            return new(operation, Failure: new(ClientEventPreparationFailureKind.InvalidPreparedData));
        }
        return Begin(operation == LocalMutationKind.None ? LocalMutationKind.PreparedEvent : operation,
            status, requireSnapshot: false, continuation);
    }

    private ClientEventPreparationResult Begin(LocalMutationKind operation, string status, bool requireSnapshot,
        ClientSynchronizationCoordinator.Lease? continuation)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var blocked = mutations.BlockingMutation(operation);
        if (blocked is not null)
        {
            continuation?.Dispose();
            return new(operation, Failure: new(ClientEventPreparationFailureKind.Blocked, Blocked: blocked));
        }
        if (requireSnapshot)
        {
            var context = session.ReadContext();
            var admission = mutations.CheckAdmission(context.IsConfigured, !string.IsNullOrWhiteSpace(context.CurrentSnapshotId), operation);
            if (admission != LocalMutationAdmission.Allowed)
                return new(operation, Failure: new(ClientEventPreparationFailureKind.Admission, Admission: admission));
        }
        if (!synchronization.TryAcquireSnapshot(out var lease, continuation))
            return new(operation, Failure: new(ClientEventPreparationFailureKind.Busy));
        try
        {
            mutations.Begin(operation, status);
            return new(operation, confirmation.Capture(lease));
        }
        catch { lease.Dispose(); throw; }
    }

    private static ClientEventPreparationResult Obsolete(LocalMutationKind operation) =>
        new(operation, Failure: new(ClientEventPreparationFailureKind.Obsolete));
}

internal enum ClientEventPreparationFailureKind { Blocked, Admission, Busy, Build, InvalidPreparedData, Obsolete }
internal sealed record ClientEventPreparationFailure(ClientEventPreparationFailureKind Kind,
    LocalMutationState? Blocked = null, LocalMutationAdmission Admission = LocalMutationAdmission.Allowed,
    SnapshotUploadFailure? SnapshotFailure = null);
internal sealed record ClientEventPreparationResult(LocalMutationKind Operation, ClientEventConfirmation.Attempt? Attempt = null,
    ModSnapshotPackageBuildResult? Build = null, ClientEventPreparationFailure? Failure = null);
