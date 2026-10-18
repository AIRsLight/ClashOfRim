using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.World;

internal sealed class ClientWorldEntryStart
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly IClientSnapshotGame game;
    private readonly IClientWorldEntryStore store;
    private readonly IClientWorldEntryPresentation view;
    private readonly ClientWorldPreparation preparation;
    private readonly ClientWorldEntry entry;
    // Only the synchronous identity transition needs this guard. Async work is
    // owned by the shared synchronization lease and preparation request tracker.
    private bool starting;
    internal ClientWorldEntryStart(ClientSessionLifecycle session, ClientSynchronizationCoordinator synchronization,
        IClientSnapshotGame game, IClientWorldEntryStore store, IClientWorldEntryPresentation view,
        ClientWorldPreparation preparation, ClientWorldEntry entry)
    { this.session = session; this.synchronization = synchronization; this.game = game; this.store = store;
        this.view = view; this.preparation = preparation; this.entry = entry; }

    internal bool TryStart(string? server, string? user, string? password, bool createAccount = false)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (starting) return false;
        starting = true;
        ClientSynchronizationCoordinator.Lease? operation = null;
        ClientGameRequestTracker.Request? request = null;
        ClientSessionLifecycle.Ownership? owner = null;
        var source = game.BindCurrentGame();
        try
        {
            if (!ClashOfRimServerUrlUtility.TryNormalizeHttpBaseUrl(server, out string normalizedServer))
            { view.Reject(view.Text("ClashOfRim.ServerEntry.StatusAddressMissing")); return false; }
            string normalizedUser = (user ?? "").Trim();
            if (normalizedUser.Length == 0)
            { view.Reject(view.Text("ClashOfRim.EnterUserId")); return false; }
            if (synchronization.IsBusy)
            { view.Reject(view.Text("ClashOfRim.Sync.StatusBusy")); return false; }

            // End owns the only session generation reset and all registered domain
            // cleanup. Entry must not replay that cleanup after it has completed.
            var exit = session.EndOwned(session.CaptureOwnership(), session.ReadContext());
            if (exit is null) return false;
            owner = exit.Owner;
            if (!source.IsCurrent) return false;
            if (!synchronization.TryBeginManual(out operation))
            { view.Reject(view.Text("ClashOfRim.Sync.StatusBusy")); return false; }
            store.Initialize(normalizedServer, normalizedUser, password ?? "");
            var context = session.ReadContext();
            if (!session.Owns(owner) || !source.IsCurrent || !operation.IsCurrent
                || context.ServerBaseUrl != normalizedServer || context.UserId != normalizedUser
                || context.ColonyId.Length != 0 || !string.IsNullOrEmpty(context.AuthToken)) return false;
            // Capture before scheduling; a delayed worker cannot adopt later settings.
            request = preparation.Begin();
            string message = view.Text("ClashOfRim.RequestingWorldSession", ("USER", normalizedUser), ("COLONY", ""));
            if (!session.Owns(owner, context, false) || !source.IsCurrent || !operation.IsCurrent) return false;
            view.Progress(message, -1f);
            if (!session.Owns(owner, context, false) || !source.IsCurrent || !operation.IsCurrent) return false;
            var captured = request;
            _ = operation.RunContinuation(next => Run(captured, createAccount, next));
            request = null; // The asynchronous continuation owns it now.
            return true;
        }
        catch (Exception ex)
        {
            if ((owner is null || session.Owns(owner)) && source.IsCurrent)
            {
                string message = view.Text("ClashOfRim.ServerEntry.StatusFlowException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message));
                if ((owner is null || session.Owns(owner)) && source.IsCurrent) view.Failure(message);
            }
            return false;
        }
        finally
        {
            if (request is not null) preparation.Discard(request);
            operation?.Dispose();
            starting = false;
        }
    }
    private async Task Run(ClientGameRequestTracker.Request request, bool createAccount, ClientSynchronizationCoordinator.Lease operation)
    {
        var prepared = await preparation.PrepareAsync(createAccount, request).ConfigureAwait(false);
        if (prepared is null) return;
        if (!operation.IsCurrent) { preparation.Discard(prepared); return; }
        operation.QueueContinuation(view.Enqueue, next =>
            preparation.Complete(prepared, outcome => { _ = entry.Handle(outcome.Result, outcome.Failure, next); }),
            completed: () => preparation.Discard(prepared));
    }
}
