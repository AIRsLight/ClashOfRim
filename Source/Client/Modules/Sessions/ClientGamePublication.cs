using System;
using System.Threading;
using System.Threading.Tasks;

namespace AIRsLight.ClashOfRim.Modules.Sessions;

// The supplied ownership check is used both before enqueue and on the game thread.
// Cancellation or discarded game work must also finish the asynchronous caller.
internal static class ClientGamePublication
{
    internal static async Task EnqueueAsync(Action<Action, Action> enqueue, CancellationToken token,
        Func<Action, bool> tryApply, Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = token.Register(() => completion.TrySetCanceled(token));
        if (!tryApply(() => enqueue(() =>
        {
            try { tryApply(action); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); throw; }
        }, () => completion.TrySetResult()))) return;
        await completion.Task.ConfigureAwait(false);
    }
}
