using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

// Orders whole domain lifecycles, including their recovery wait. A new owner never
// waits for an obsolete HTTP operation that ignores cancellation. Business work
// still obtains its normal mutation/snapshot admission when it reaches the head.
internal sealed class ClientGameWorkQueue
{
    private readonly ClientGameRequestTracker requests;
    private ClientGameRequestTracker.Scope? owner;
    private CancellationTokenSource? lifetime;
    private Task tail = Task.CompletedTask;
    internal ClientGameWorkQueue(ClientGameRequestTracker requests) => this.requests = requests;

    internal void Reset()
    {
        using var p = ClientSessionWork.EnterPublication();
        lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
        owner = null; tail = Task.CompletedTask;
    }

    internal Task Enqueue(ClientGameRequestTracker.Scope scope, Func<CancellationToken, Task> execute, Action finished)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!requests.TryApply(scope, static () => { })) { finished(); return Task.CompletedTask; }
        if (!ReferenceEquals(scope, owner))
        { Reset(); owner = scope; lifetime = CancellationTokenSource.CreateLinkedTokenSource(scope.Owner.Token); }
        CancellationToken token = lifetime!.Token;
        Task previous = tail;
        tail = ClientSessionWork.Run(async () =>
        {
            try
            {
                await previous.WaitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (requests.TryApply(scope, static () => { })) await execute(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally { finished(); }
        });
        return tail;
    }
}
