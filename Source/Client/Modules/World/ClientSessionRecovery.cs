using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.World;

internal interface IClientSessionRecoveryRuntime
{
    IClientSessionRecoverySession Bind();
    void Enqueue(Action action, Action discard);
    void ReturnToMenu();
}
internal interface IClientSessionRecoverySession
{
    bool IsCurrent { get; }
    bool IsPlaying { get; }
    bool HasGame { get; }
}
internal interface IClientSessionRecoveryDialog
{
    bool IsOpen { get; }
    void CompleteReconnect(bool succeeded, string status);
    void Close();
}
internal interface IClientSessionRecoveryPresentation
{
    string Text(string key, params (string Name, object? Value)[] args);
    void LoginStatus(string text);
    void PresenceStatus(string text);
    void CloseMainTab();
    IClientSessionRecoveryDialog Show(string title, string message, Func<bool> current,
        Func<IClientSessionRecoveryDialog, Task> reconnect, Action<IClientSessionRecoveryDialog> exit);
    void Diagnose(string text, bool warning);
}
internal interface IClientSessionRecoveryEffects
{
    void AdoptQueue(ModEventQueueSummaryDto? queue);
    void RefreshLedger(string reason);
    void RefreshPlayers();
    void RefreshChat();
    void StartPresence();
    void ReconcileRaid(ModActiveRaidRecoveryDto? recovery);
}

// Owns unavailable UI and in-place recovery. Session invalidation, authentication,
// snapshot checks, temporary credentials and world observation remain in their
// existing domain modules; the engine boundary only supplies windows and a queue.
internal sealed class ClientSessionRecovery
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientSessionReconnect reconnect;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientWorldLoginPublication world;
    private readonly IClientSessionRecoveryRuntime runtime;
    private readonly IClientSessionRecoveryPresentation view;
    private readonly IClientSessionRecoveryEffects effects;
    internal ClientSessionRecovery(ClientSessionLifecycle session, ClientSessionConnection connection, ClientSessionReconnect reconnect,
        ClientLocalMutationCoordinator mutations, ClientWorldLoginPublication world, IClientSessionRecoveryRuntime runtime,
        IClientSessionRecoveryPresentation view, IClientSessionRecoveryEffects effects)
    {
        this.session = session; this.reconnect = reconnect; this.mutations = mutations; this.world = world;
        this.runtime = runtime; this.view = view; this.effects = effects;
        connection.ConnectionLost += Disconnected;
    }
    internal Action? CaptureUnavailable(ClashOfRimClientNetworkContext context, string? requestedSessionId)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var owner = session.CaptureOwnership(); var id = session.SessionId;
        if (!session.Owns(owner, context, false) || (requestedSessionId is not null && requestedSessionId != id)) return null;
        // Keep the accepted login revision even when a replacement reuses every
        // visible credential. Snapshot advancement does not retire the account.
        return () =>
        {
            using var p = ClientSessionWork.EnterPublication();
            if (session.Owns(owner, context, false)) Expired(context.AuthToken, id);
        };
    }
    internal void Expired(string? authToken, string? sessionId = null)
    {
        // An unauthenticated request has no session to invalidate.
        if (string.IsNullOrWhiteSpace(authToken) && string.IsNullOrWhiteSpace(sessionId)) return;
        Unavailable(false, authToken, sessionId);
    }
    private void Disconnected(ClientSessionConnection.Observation source)
        => source.TryApply(() =>
        {
            if (session.SessionId == source.SessionId && session.Owns(session.CaptureOwnership(), source.Context, false))
                Unavailable(true, source.Context.AuthToken, source.SessionId);
        });

    private void Unavailable(bool disconnected, string? auth, string? id)
    {
        using var p = ClientSessionWork.EnterPublication();
        var owner = session.CaptureOwnership(); var context = session.ReadContext(); var bound = runtime.Bind();
        if (!bound.IsCurrent || (disconnected && (!bound.IsPlaying || !bound.HasGame || string.IsNullOrWhiteSpace(context.CurrentSnapshotId)))) return;
        var invalidation = session.BeginUnavailable(auth, id);
        if (invalidation is null) return;
        bool Pending() => bound.IsCurrent && session.Owns(owner, context, false);
        session.WhenUnavailableReady(invalidation, () =>
        {
            if (!Pending()) return;
            runtime.Enqueue(() =>
            {
                using var publication = ClientSessionWork.EnterPublication();
                if (!Pending() || session.CompleteUnavailableOwned(invalidation) is not { } cleared) return;
                var notice = new Notice(cleared, bound);
                bool Step(Action action) { if (!Current(notice)) return false; action(); return Current(notice); }
                string title = "", message = "", status = "";
                string prefix = "ClashOfRim.SessionExpired." + (disconnected ? "ConnectionLost" : "");
                if (!Step(() => title = view.Text(prefix + "Title")) || !Step(() => message = view.Text(prefix + "Message"))
                    || !Step(() => status = view.Text(prefix + "Status")) || !Step(() => view.LoginStatus(status))
                    || !Step(() => view.PresenceStatus(status)) || !Step(view.CloseMainTab)) return;
                var dialog = view.Show(title, message, () => Current(notice),
                    window => ReconnectAsync(notice, window), window => Exit(notice, window));
                if (!Current(notice)) dialog.Close();
            }, static () => { });
        });
    }
    private bool Current(Notice notice)
    {
        using var p = ClientSessionWork.EnterPublication();
        return notice.Runtime.IsCurrent && (notice.Accepted is { } accepted ? session.Owns(accepted)
            : session.Owns(notice.Cleared.Owner, notice.Cleared.Context, false));
    }
    private void Exit(Notice notice, IClientSessionRecoveryDialog dialog)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (Current(notice) && dialog.IsOpen) runtime.ReturnToMenu();
        else dialog.Close();
    }
    private Task ReconnectAsync(Notice notice, IClientSessionRecoveryDialog dialog)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!Current(notice) || !dialog.IsOpen) { dialog.Close(); return Task.CompletedTask; }
        var blocked = reconnect.TryBegin(new(notice.Runtime.IsPlaying, notice.Runtime.HasGame, mutations.IsPending), out var attempt);
        if (attempt is null)
        {
            string reason = view.Text(BlockKey(blocked));
            if (Current(notice) && dialog.IsOpen) dialog.CompleteReconnect(false, reason);
            return Task.CompletedTask;
        }
        // Run owns disposal even when scheduling or world observation capture fails.
        return Run(notice, dialog, attempt);
    }
    private async Task Run(Notice notice, IClientSessionRecoveryDialog dialog, ClientSessionReconnect.Attempt attempt)
    {
        ClientWorldMarkers.Observation? observation = null;
        ClientWorldLoginPublication.Accepted? accepted = null;
        bool finished = false;
        bool Current()
        {
            if (finished || !notice.Runtime.IsCurrent) return false;
            if (attempt.Published?.Publication is { } proof)
                return session.Owns(proof) && !session.IsUnavailable
                    && session.ReadContext().CurrentSnapshotId == attempt.Expected.CurrentSnapshotId
                    && (accepted is null || world.IsCurrent(accepted));
            return dialog.IsOpen && session.Owns(attempt.Owner, attempt.Expected, false)
                && (observation is null || world.IsCurrent(observation));
        }
        bool Apply(Action action)
        {
            using var p = ClientSessionWork.EnterPublication();
            if (!Current()) return false;
            action(); return true;
        }
        bool Step(Action action) => Apply(action) && Apply(static () => { });
        string? Text(string key, params (string Name, object? Value)[] args)
        { string? text = null; return Step(() => text = view.Text(key, args)) ? text : null; }
        async Task Publish(Action action)
        {
            int consumed = 0;
            await ClientGamePublication.EnqueueAsync((run, discard) =>
            {
                try { runtime.Enqueue(run, () => { Interlocked.Exchange(ref consumed, 1); discard(); }); }
                catch { Interlocked.Exchange(ref consumed, 1); throw; }
            }, attempt.Owner.Token, Apply, () => { if (Interlocked.Exchange(ref consumed, 1) == 0) action(); }).ConfigureAwait(false);
        }
        try
        {
            observation = world.Capture();
            if (!Step(() => view.Diagnose("Starting in-place reconnect: user=" + attempt.Expected.UserId
                + ", colony=" + attempt.Expected.ColonyId + ", snapshot=" + attempt.Expected.CurrentSnapshotId, warning: false))) return;
            await ClientSessionWork.Run(async () =>
            {
                if (!Apply(static () => { })) return;
                await attempt.PrepareAsync().ConfigureAwait(false);
                await Publish(() =>
                {
                    SessionReconnectResult result;
                    try { result = attempt.Complete(notice.Runtime.IsPlaying, notice.Runtime.HasGame && notice.Runtime.IsCurrent, mutations.IsPending); }
                    finally
                    {
                        if (attempt.Published is { Publication: { } proof } published)
                        {
                            notice.Accepted = proof;
                            accepted = world.Adopt(observation, proof, published.Login!);
                        }
                    }
                    if (result.Outcome == SessionReconnectOutcome.Obsolete || !Current()) return;
                    if (result.Outcome != SessionReconnectOutcome.Succeeded)
                    {
                        string key = result.Outcome switch
                        {
                            SessionReconnectOutcome.LocalStateBlocked => BlockKey(result.BlockReason),
                            SessionReconnectOutcome.IdentityChanged => "ClashOfRim.SessionExpired.ReconnectIdentityChanged",
                            SessionReconnectOutcome.SnapshotChanged => "ClashOfRim.SessionExpired.ReconnectSnapshotChanged",
                            SessionReconnectOutcome.Rejected => "ClashOfRim.SessionExpired.ReconnectRejected",
                            _ => "ClashOfRim.SessionExpired.ReconnectNetworkFailed"
                        };
                        var failed = Text(key, ("MESSAGE", result.Message ?? ""));
                        if (failed is not null && Step(() => view.Diagnose("Reconnect refused: " + result.Outcome + ": " + result.Message, warning: true)))
                            Step(() => dialog.CompleteReconnect(false, failed));
                        return;
                    }
                    if (accepted is null) return;
                    if (!Step(() => view.Diagnose("In-place reconnect accepted: user=" + attempt.Expected.UserId
                        + ", colony=" + attempt.Expected.ColonyId + ", snapshot=" + attempt.Expected.CurrentSnapshotId
                        + ", session=" + result.Login!.SessionId + ", activeRaidRecovery=" + (result.Snapshot!.ActiveRaidRecovery?.EventId ?? "<none>"), warning: false))) return;
                    string? success = Text("ClashOfRim.SessionExpired.ReconnectSucceeded");
                    if (success is null || !Step(() => view.LoginStatus(success)) || !Step(() => view.PresenceStatus(success))
                        || !Step(() => effects.AdoptQueue(result.Login!.EventQueue)) || !Step(() => world.Publish(accepted, "session-reconnect"))
                        || !Step(() => dialog.CompleteReconnect(true, success))) return;
                    string? reason = Text("ClashOfRim.SessionExpired.ReconnectReason");
                    if (reason is null || !Step(() => effects.RefreshLedger(reason)) || !Step(effects.RefreshPlayers)
                        || !Step(effects.RefreshChat) || !Step(effects.StartPresence)) return;
                    Step(() => effects.ReconcileRaid(result.Snapshot!.ActiveRaidRecovery));
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!Apply(static () => { })) return;
            await Publish(() =>
            {
                if (!Step(() => view.Diagnose("Session reconnect completion failed: " + ex, warning: true))) return;
                string? failure = Text("ClashOfRim.SessionExpired.ReconnectNetworkFailed", ("MESSAGE", ex.Message));
                if (failure is null) return;
                if (attempt.Published is not null && (!Step(() => view.LoginStatus(failure)) || !Step(() => view.PresenceStatus(failure)))) return;
                if (dialog.IsOpen) Step(() => dialog.CompleteReconnect(false, failure));
            }).ConfigureAwait(false);
        }
        finally
        {
            using (ClientSessionWork.EnterPublication()) finished = true;
            await attempt.DisposeAsync().ConfigureAwait(false);
        }
    }
    private static string BlockKey(SessionReconnectBlockReason reason) => reason switch
    {
        SessionReconnectBlockReason.NotInPlayableSession => "ClashOfRim.SessionExpired.ReconnectNotPlaying",
        SessionReconnectBlockReason.MissingIdentity => "ClashOfRim.SessionExpired.ReconnectMissingIdentity",
        SessionReconnectBlockReason.SynchronizationBusy => "ClashOfRim.SessionExpired.ReconnectBusy",
        SessionReconnectBlockReason.AtomicMutationPending => "ClashOfRim.SessionExpired.ReconnectAtomicPending",
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };
    private sealed class Notice
    {
        internal readonly ClientSessionLifecycle.Exit Cleared;
        internal readonly IClientSessionRecoverySession Runtime;
        // The same dialog may offer a retry after our login was published but a
        // later callback failed. Never rebind it to an unrelated current identity.
        internal ClientSessionLifecycle.Login? Accepted;
        internal Notice(ClientSessionLifecycle.Exit cleared, IClientSessionRecoverySession runtime)
        { Cleared = cleared; Runtime = runtime; }
    }
}
