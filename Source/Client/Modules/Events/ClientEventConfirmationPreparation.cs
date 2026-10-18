using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Events;

// Admission and preparation precede both single and batch event confirmation.
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
        var started = Begin(operation, status);
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

    private ClientEventPreparationResult Begin(LocalMutationKind operation, string status)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var blocked = mutations.BlockingMutation(operation);
        if (blocked is not null)
        {
            return new(operation, Failure: new(ClientEventPreparationFailureKind.Blocked, Blocked: blocked));
        }
        var context = session.ReadContext();
        var admission = mutations.CheckAdmission(context.IsConfigured, !string.IsNullOrWhiteSpace(context.CurrentSnapshotId), operation);
        if (admission != LocalMutationAdmission.Allowed)
            return new(operation, Failure: new(ClientEventPreparationFailureKind.Admission, Admission: admission));
        if (!synchronization.TryAcquireSnapshot(out var lease))
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

internal enum ClientEventPreparationFailureKind { Blocked, Admission, Busy, Build, Obsolete }
internal sealed record ClientEventPreparationFailure(ClientEventPreparationFailureKind Kind,
    LocalMutationState? Blocked = null, LocalMutationAdmission Admission = LocalMutationAdmission.Allowed,
    SnapshotUploadFailure? SnapshotFailure = null);
internal sealed record ClientEventPreparationResult(LocalMutationKind Operation, ClientSnapshotTransaction.Attempt? Attempt = null,
    ModSnapshotPackageBuildResult? Build = null, ClientEventPreparationFailure? Failure = null);
