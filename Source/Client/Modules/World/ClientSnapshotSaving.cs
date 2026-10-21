using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.World;

internal enum ClientSnapshotSaveKind { Manual, Menu, Autosave }
internal enum ClientSnapshotSaveStage { Blocked, NoSession, Busy, Uploading, Succeeded, Failed, Exception }
internal sealed record ClientSnapshotSaveNotice(ClientSnapshotSaveKind Kind, ClientSnapshotSaveStage Stage,
    string? Snapshot = null, SnapshotUploadFailure? Failure = null, LocalMutationState? Mutation = null, Exception? Exception = null);
internal interface IClientSnapshotSavingPresentation
{
    string Format(ClientSnapshotSaveNotice notice);
    void Status(string message);
    void Message(string message, bool success);
    void Diagnose(ClientSnapshotSaveNotice notice, string message);
}

// User saves and autosaves share capture/upload/publication. The caller kind only
// selects notification and catalog followups; the uploader remains the sole save owner.
internal sealed class ClientSnapshotSaving
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientSnapshotUploader snapshots;
    private readonly ClientColonyRegistration registration;
    private readonly ClientColonyAppearance appearance;
    private readonly IClientSnapshotSavingPresentation view;
    private readonly ClientRequestTracker<Boundary> requests;

    internal ClientSnapshotSaving(ClientSessionLifecycle session, ClientSynchronizationCoordinator synchronization,
        ClientLocalMutationCoordinator mutations, ClientSnapshotUploader snapshots, IClientSnapshotGame game,
        IClientWorldIdentity identity, IClientWorldConfigurationRuntime world, ClientColonyRegistration registration,
        ClientColonyAppearance appearance, IClientSnapshotSavingPresentation view)
    {
        this.session = session; this.synchronization = synchronization; this.mutations = mutations; this.snapshots = snapshots;
        this.registration = registration; this.appearance = appearance; this.view = view;
        requests = new(session, () => new(game.BindCurrentGame(), world.Bind(), identity.WorldId),
            bound => bound.Game.IsCurrent && bound.World.IsCurrent && identity.WorldId == bound.WorldId, static () => { });
    }
    internal bool CanUseMenu => session.HasSnapshotSession(session.ReadContext());
    internal bool InterceptsAutosave => session.HasConfiguredSession;

    internal MenuCommand? PrepareMenu()
    {
        using var p = ClientSessionWork.EnterPublication();
        // Keep replacing vanilla save/load during temporary unavailability;
        // executing the captured choice still requires a usable original session.
        return CanUseMenu ? new(this, requests.Capture()) : null;
    }
    internal sealed class MenuCommand
    {
        private readonly ClientSnapshotSaving owner;
        private readonly ClientRequestTracker<Boundary>.Scope scope;
        private bool consumed;
        internal MenuCommand(ClientSnapshotSaving owner, ClientRequestTracker<Boundary>.Scope scope) { this.owner = owner; this.scope = scope; }
        internal bool IsCurrent => !consumed && !owner.session.IsUnavailable && owner.CanUseMenu
            && owner.requests.TryApply(scope, static () => { });
        internal Task StartAsync(Action? onSavedExit = null)
        {
            using var p = ClientSessionWork.EnterPublication();
            if (!IsCurrent) return Task.CompletedTask;
            consumed = true;
            return owner.StartAsync(ClientSnapshotSaveKind.Menu, onSavedExit);
        }
    }
    internal bool TryAutosave(out Task work)
    {
        using var p = ClientSessionWork.EnterPublication();
        bool intercept = InterceptsAutosave;
        work = intercept ? StartAsync(ClientSnapshotSaveKind.Autosave) : Task.CompletedTask;
        return intercept;
    }

    internal Task StartAsync(ClientSnapshotSaveKind kind, Action? onSavedExit = null)
    {
        using var p = ClientSessionWork.EnterPublication();
        var scope = requests.Capture(); var context = session.ReadContext();
        bool Step(Action action)
        {
            bool current = false;
            requests.TryApply(scope, () => { if (session.Owns(scope.Owner, context, true)) { action(); current = true; } });
            return current && session.Owns(scope.Owner, context, true) && requests.TryApply(scope, static () => { });
        }
        var blocked = mutations.BlockingMutation();
        if (blocked is not null) { Present(new(kind, ClientSnapshotSaveStage.Blocked, Mutation: blocked), Step); return Task.CompletedTask; }
        if (kind != ClientSnapshotSaveKind.Manual && !session.HasSnapshotSession(context))
        { Present(new(kind, ClientSnapshotSaveStage.NoSession), Step); return Task.CompletedTask; }
        ClientSynchronizationCoordinator.Lease? lease = null;
        try
        {
            if (!synchronization.TryAcquireSnapshot(out lease))
            { Present(new(kind, ClientSnapshotSaveStage.Busy), Step); return Task.CompletedTask; }
            if (!Step(static () => { }) || lease?.IsCurrent != true) { lease?.Dispose(); return Task.CompletedTask; }
            var request = requests.Begin(lease, replace: true)!;
            return Run(request, kind, onSavedExit);
        }
        catch { lease?.Dispose(); throw; }
    }

    private async Task Run(ClientRequestTracker<Boundary>.Request request, ClientSnapshotSaveKind kind, Action? onSavedExit)
    {
        string? expected = request.Context.CurrentSnapshotId, publishing = null;
        bool released = false;
        bool Current()
        {
            using var p = ClientSessionWork.EnterPublication();
            if (!requests.TryApply(request, static () => { }) || session.IsUnavailable || mutations.IsPending
                || !released && request.Admission?.IsCurrent != true) return false;
            string? snapshot = session.ReadContext().CurrentSnapshotId;
            return snapshot == expected || publishing is not null && snapshot == publishing;
        }
        bool Step(Action action)
        {
            using var p = ClientSessionWork.EnterPublication();
            if (!Current()) return false;
            action(); return Current();
        }
        Task Publish(Action action) => ClientGamePublication.EnqueueAsync(request.Scope.Boundary.Game.Enqueue,
            request.Scope.Owner.Token, Step, action);
        void Release() { released = true; request.Admission?.Dispose(); }
        try
        {
            Present(new(kind, ClientSnapshotSaveStage.Uploading), Step);
            if (!Current()) return;
            await ClientSessionWork.Run(async () =>
            {
                if (!Current()) return;
                var result = await snapshots.UploadAsync(false, null, kind == ClientSnapshotSaveKind.Autosave
                    ? ModSnapshotUploadKinds.AutoSave : ModSnapshotUploadKinds.ManualUpload, null, request.Admission,
                    request.Scope.Owner.Token, new(Current, id => publishing = id)).ConfigureAwait(false);
                if (!Current()) return;
                if (result.Failure is null)
                {
                    if (session.ReadContext().CurrentSnapshotId != result.AcceptedSnapshotId) return;
                    expected = result.AcceptedSnapshotId; publishing = null;
                }
                Release();
                await Publish(() =>
                {
                    var notice = new ClientSnapshotSaveNotice(kind, result.Failure is null ? ClientSnapshotSaveStage.Succeeded : ClientSnapshotSaveStage.Failed,
                        result.AcceptedSnapshotId, result.Failure);
                    string? message = Present(notice, Step);
                    if (result.Failure is not null || message is null) return;
                    if (kind == ClientSnapshotSaveKind.Manual)
                    { Step(() => _ = registration.RequestAsync(ClientColonyRegistrationKind.Extensions, message)); return; }
                    ModColonyAppearanceDto? captured = null;
                    if (!Step(() => captured = appearance.Capture())) return;
                    if (!Step(() => _ = registration.StartCatalogAsync(captured))) return;
                    if (kind == ClientSnapshotSaveKind.Menu && onSavedExit is not null && Current())
                    {
                        var exit = session.EndOwned(request.Scope.Owner, session.ReadContext(), requireSnapshot: true, sendLogout: true);
                        if (exit is null) return;
                        var bound = request.Scope.Boundary;
                        try
                        {
                            session.ContinueExit(exit, () => { if (bound.Game.IsCurrent && bound.World.IsCurrent) onSavedExit(); });
                        }
                        catch (Exception ex)
                        { session.ContinueExit(exit, () => view.Diagnose(new(kind, ClientSnapshotSaveStage.Exception, Exception: ex), "Snapshot saved; exit failed.")); }
                    }
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (Current())
            {
                Release();
                await Publish(() => Present(new(kind, ClientSnapshotSaveStage.Exception, Exception: ex), Step)).ConfigureAwait(false);
            }
        }
        finally { requests.End(request); request.Admission?.Dispose(); }
    }
    private string? Present(ClientSnapshotSaveNotice notice, Func<Action, bool> step)
    {
        string message = "";
        if (!step(() => message = view.Format(notice)) || !step(() => view.Status(message))) return null;
        bool skipped = notice.Stage is ClientSnapshotSaveStage.Blocked or ClientSnapshotSaveStage.NoSession or ClientSnapshotSaveStage.Busy;
        if (notice.Stage == ClientSnapshotSaveStage.Exception || notice.Kind == ClientSnapshotSaveKind.Autosave && (skipped || notice.Stage == ClientSnapshotSaveStage.Failed))
            if (!step(() => view.Diagnose(notice, message))) return null;
        if (notice.Stage != ClientSnapshotSaveStage.Uploading && !(notice.Kind == ClientSnapshotSaveKind.Autosave && skipped))
            if (!step(() => view.Message(message, notice.Stage == ClientSnapshotSaveStage.Succeeded))) return null;
        return message;
    }
    internal sealed record Boundary(IClientSnapshotGameSession Game, IClientWorldConfigurationSession World, string WorldId);
}
