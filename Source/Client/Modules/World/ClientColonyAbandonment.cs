using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Modules.World;

internal interface IClientColonyAbandonTransport
{
    Task<ClashOfRimClientNetworkResult<ModAbandonPlayerColonyResponseDto>> AbandonAsync(ClashOfRimClientNetworkContext context, string key, CancellationToken token);
}
internal interface IClientColonyAbandonTarget
{
    bool IsCurrent { get; }
    bool IsAvailable { get; }
    string Label { get; }
    void ReturnToMenu();
}
internal interface IClientColonyAbandonPresentation : IClientWorldPreparationPresentation
{
    void Confirm(string colony, Func<Task> confirm);
    void Status(string message);
    void Message(string message, ClientWorldWriteMessage kind);
}

// The confirmation and its terminal receipt belong to the same colony/world.
// Session exit owns all domain cleanup; transient catalogs are not projected into
// a scene that is about to be discarded.
internal sealed class ClientColonyAbandonment
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientRequestTracker<Boundary> requests;
    private readonly IClientColonyAbandonTransport transport;
    private readonly IClientColonyAbandonPresentation view;
    internal ClientColonyAbandonment(ClientSessionLifecycle session, IClientWorldIdentity identity, IClientWorldCatalogRuntime runtime,
        IClientColonyAbandonTransport transport, IClientColonyAbandonPresentation view)
    {
        this.session = session; this.transport = transport; this.view = view;
        requests = new(session, () => new(runtime.Bind(), identity.WorldId), bound => bound.Runtime.IsCurrent && bound.WorldId == identity.WorldId, static () => { });
    }
    internal bool Busy => requests.Busy;
    internal void Open(IClientColonyAbandonTarget target)
    {
        using var publication = ClientSessionWork.EnterPublication();
        Open(requests.Capture(), target);
    }
    internal Action? Command(IClientColonyAbandonTarget target)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var scope = requests.Capture();
        return scope.Context.IsConfigured && Current(scope, target) && target.IsAvailable && Current(scope, target)
            ? () => Open(scope, target) : null;
    }
    private bool Current(ClientRequestTracker<Boundary>.Scope scope, IClientColonyAbandonTarget target)
        => target.IsCurrent && requests.TryApply(scope, static () => { });
    private void Open(ClientRequestTracker<Boundary>.Scope scope, IClientColonyAbandonTarget target)
    {
        using var publication = ClientSessionWork.EnterPublication();
        bool Current() => this.Current(scope, target);
        if (!Current()) return;
        if (!scope.Context.IsConfigured || string.IsNullOrWhiteSpace(scope.Context.AuthToken) || session.IsUnavailable)
        { Reject("ClashOfRim.AbandonColony.StatusNotConfigured", Current); return; }
        if (requests.Busy) { Reject("ClashOfRim.AbandonColony.StatusSubmitting", Current); return; }
        if (!target.IsAvailable || !Current()) return;
        string label = target.Label; bool consumed = false;
        if (Current()) view.Confirm(label, () =>
        {
            using var confirm = ClientSessionWork.EnterPublication();
            if (consumed) return Task.CompletedTask;
            consumed = true;
            if (!Current() || !target.IsAvailable || session.IsUnavailable) return Task.CompletedTask;
            if (requests.Busy) { Reject("ClashOfRim.AbandonColony.StatusSubmitting", Current); return Task.CompletedTask; }
            if (!Current()) return Task.CompletedTask;
            return ExecuteAsync(requests.Begin()!, target);
        });
    }
    private void Reject(string key, Func<bool> current)
    {
        string message = view.Text(key);
        if (current()) view.Message(message, ClientWorldWriteMessage.Reject);
    }
    private async Task ExecuteAsync(ClientRequestTracker<Boundary>.Request request, IClientColonyAbandonTarget target)
    {
        var scope = request.Scope; var context = request.Context;
        ClientSessionLifecycle.InvalidationHold? hold = null;
        bool Current() => requests.TryApply(request, static () => { }) && target.IsCurrent
            && session.Owns(scope.Owner, context, requireSnapshot: true);
        bool Ready() => Current() && target.IsAvailable && Current();
        bool Step(Action action) => Ready() && requests.TryApply(request, action) && Ready();
        async Task Publish(Action action)
        {
            bool consumed = false;
            await ClientGamePublication.EnqueueAsync((run, discard) => scope.Boundary.Runtime.Enqueue(run, () => { consumed = true; discard(); }), scope.Owner.Token,
                run => Current() && requests.TryApply(request, run), () =>
                {
                    if (consumed || !Ready()) return;
                    consumed = true; action();
                }).ConfigureAwait(false);
        }
        async Task Failure(string key, params (string Name, object? Value)[] args)
        {
            await Publish(() =>
            {
                string message = "";
                if (!Step(() => message = view.Text(key, args)) || !Step(() => view.Status(message))) return;
                Step(() => view.Message(message, ClientWorldWriteMessage.Reject));
            }).ConfigureAwait(false);
        }
        try
        {
            hold = session.HoldInvalidation(scope.Owner, context, Current);
            if (hold is null || !Current()) return;
            string submitting = "";
            if (!Step(() => submitting = view.Text("ClashOfRim.AbandonColony.StatusSubmitting")) || !Step(() => view.Status(submitting))) return;
            string key = "abandon-colony:" + context.UserId + ":" + context.ColonyId + ":" + context.CurrentSnapshotId + ":" + Guid.NewGuid().ToString("N");
            await ClientSessionWork.Run(async () =>
            {
                if (!Current()) return;
                var result = await transport.AbandonAsync(context, key, scope.Owner.Token).ConfigureAwait(false);
                if (!Current()) return;
                if (!result.Success || result.Response is null)
                {
                    await Failure("ClashOfRim.AbandonColony.StatusFailed", ("CODE", result.ErrorCode ?? ""), ("MESSAGE", result.Message ?? "")).ConfigureAwait(false); return;
                }
                var protocol = result.Response.Result;
                if (protocol?.Accepted != true)
                {
                    await Failure("ClashOfRim.AbandonColony.StatusRejected", ("CODE", protocol?.ErrorCode.ToString() ?? ""), ("MESSAGE", protocol?.Message ?? "")).ConfigureAwait(false); return;
                }
                await Publish(() =>
                {
                    string message = "";
                    if (!Step(() => message = view.Text("ClashOfRim.AbandonColony.StatusSucceeded"))) return;
                    var exit = session.EndOwned(scope.Owner, context, requireSnapshot: true);
                    if (exit is null || !target.IsCurrent) return;
                    try
                    {
                        if (!session.ContinueExit(exit, () => view.Status(message)) || !target.IsCurrent) return;
                        if (!session.ContinueExit(exit, () => view.Message(message, ClientWorldWriteMessage.Progress)) || !target.IsCurrent) return;
                        session.ContinueExit(exit, target.ReturnToMenu);
                    }
                    catch (Exception ex)
                    {
                        // The original worker is cancelled by EndOwned. Handle scene
                        // failures here with the exit receipt, before returning to it.
                        session.ContinueExit(exit, () => view.Diagnose("Player colony abandon exit exception: " + ex));
                    }
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (Current())
            {
                view.Diagnose("Player colony abandon exception: " + ex);
                await Failure("ClashOfRim.AbandonColony.StatusException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message)).ConfigureAwait(false);
            }
        }
        finally { requests.End(request); hold?.Dispose(); }
    }
    private sealed record Boundary(IClientWorldCatalogSession Runtime, string WorldId);
}
