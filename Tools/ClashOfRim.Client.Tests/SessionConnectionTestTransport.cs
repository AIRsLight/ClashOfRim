using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;

// Shared controllable stream boundary; connection ownership and notification
// dispatch remain the real modules in both suites.
internal sealed class SessionConnectionTestTransport : IClientSessionStreamTransport
{
    internal readonly ConcurrentQueue<Pending> Calls = new();
    internal Action? BeforeStart;
    private readonly SemaphoreSlim available = new(0);
    public Task<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> StreamAsync(ClashOfRimClientNetworkContext context, string sessionId,
        long notificationVersion, long worldVersion, Action<ModSessionStreamEvent> onEvent, CancellationToken cancellationToken)
    {
        BeforeStart?.Invoke();
        var pending = new Pending(context, sessionId, onEvent, cancellationToken, notificationVersion, worldVersion);
        Calls.Enqueue(pending); available.Release(); return pending.Result.Task;
    }
    internal async Task<Pending> Next()
    {
        if (!await available.WaitAsync(TimeSpan.FromSeconds(5)) || !Calls.TryDequeue(out var value)) throw new InvalidOperationException("Stream did not start.");
        return value;
    }
    internal sealed record Pending(ClashOfRimClientNetworkContext Context, string SessionId, Action<ModSessionStreamEvent> Callback,
        CancellationToken Token, long NotificationVersion, long WorldVersion)
    {
        internal readonly TaskCompletionSource<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Emit(string name = "probe", string data = "{}") => Callback(new(name, data));
    }
}
