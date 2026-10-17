using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

internal interface IClientSnapshotFailureView
{
    void Enqueue(Action action);
    void Show(ClientLocalMutationCoordinator.Failure failure, Action? retry, Action exit, Func<bool> belongsToCurrentSession, Action closed);
    void Close(ClientLocalMutationCoordinator.Failure failure);
    void ReturnToMenu();
}

// Owns queued failure presentation and decisions. The existing mutation coordinator
// remains the only authority for pending work and for consuming retry/exit once.
internal sealed class ClientSnapshotFailureRecovery
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly IClientSnapshotGame game;
    private readonly IClientSnapshotFailureView view;
    private Ticket? active;

    internal ClientSnapshotFailureRecovery(ClientSessionLifecycle session, ClientLocalMutationCoordinator mutations,
        ClientSynchronizationCoordinator synchronization, IClientSnapshotGame game, IClientSnapshotFailureView view)
    {
        this.session = session; this.mutations = mutations; this.synchronization = synchronization; this.game = game; this.view = view;
        session.IdentityChanged += _ => Close();
    }

    internal event Action? ReturningToMenu;

    internal void Show(LocalMutationKind operation, string message, Action retry, bool canRetry = false)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var ticket = new Ticket(session.CaptureOwnership(), session.ReadContext(), game.BindCurrentGame(),
            mutations.RecordFailure(operation, message, retry, canRetry));
        active = ticket;
        view.Enqueue(() => Apply(ticket, () =>
        {
            if (!ReferenceEquals(active, ticket) || !mutations.IsCurrent(ticket.Failure)) return;
            view.Show(ticket.Failure, canRetry ? () => Decide(ticket, retry: true) : null,
                () => Decide(ticket, retry: false), () => BelongsToCurrentSession(ticket),
                () => ClientSessionWork.TryApply(() => { if (ReferenceEquals(active, ticket)) active = null; }));
        }));
    }

    internal void Close()
    {
        using var publication = ClientSessionWork.EnterPublication();
        Ticket? ticket = active;
        active = null;
        if (ticket is not null)
            view.Enqueue(() => view.Close(ticket.Failure));
    }

    private void Decide(Ticket ticket, bool retry) => Apply(ticket, () =>
    {
        if (!ReferenceEquals(active, ticket)) return;
        if (retry) mutations.TryRetry(ticket.Failure);
        else mutations.TryExit(ticket.Failure, () =>
        {
            active = null;
            synchronization.Reset();
            ReturningToMenu?.Invoke();
            view.Close(ticket.Failure);
            view.ReturnToMenu();
        });
    });

    private bool BelongsToCurrentSession(Ticket ticket)
    {
        bool belongs = false;
        Apply(ticket, () => belongs = true);
        return belongs;
    }

    private void Apply(Ticket ticket, Action action) => ClientSessionWork.TryApply(() =>
    {
        if (ticket.Game.IsCurrent && session.Owns(ticket.Owner, ticket.Context, requireSnapshot: false)) action();
    });

    private sealed record Ticket(ClientSessionLifecycle.Ownership Owner, ClashOfRimClientNetworkContext Context,
        IClientSnapshotGameSession Game, ClientLocalMutationCoordinator.Failure Failure);
}
