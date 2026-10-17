using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Events;

// Controlled HTTP boundary shared by reader and refresh workflow tests.
internal sealed class EventReadTestProtocol : IClientEventReadProtocol
{
    internal readonly ConcurrentQueue<string> Calls = new();
    internal readonly ConcurrentQueue<ClashOfRimClientNetworkContext> Contexts = new();
    internal Func<ClashOfRimClientNetworkContext, long, Task<ClashOfRimClientNetworkResult<ModWaitForEventsResponseDto>>>? Wait;
    internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string? FailStage, Failure;
    internal bool WaitChanged = true;
    internal Func<ClashOfRimClientNetworkContext, Task<ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto>>> Queue = _ => Task.FromResult(Ok(new ModPullPendingEventsResponseDto { EventQueue = QueueOf("event") }));
    internal Func<IReadOnlyList<string>, Task<ClashOfRimClientNetworkResult<ModPullEventDetailsResponseDto>>> Detail = ids => Task.FromResult(Ok(new ModPullEventDetailsResponseDto { Events = ids.Select(id => new ModEventDetailDto { EventId = id }).ToList() }));
    public Task<ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto>> PullAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
    {
        Calls.Enqueue("queue"); Contexts.Enqueue(context); Started.TrySetResult();
        return FailStage == "queue" ? Fail(new ModPullPendingEventsResponseDto { Result = new() { Accepted = false, ErrorCode = 9 } }) : Queue(context);
    }
    public Task<ClashOfRimClientNetworkResult<ModWaitForEventsResponseDto>> WaitAsync(ClashOfRimClientNetworkContext context, long version, int seconds, CancellationToken token)
    {
        Calls.Enqueue($"wait:{version}:{seconds}"); Contexts.Enqueue(context); Started.TrySetResult();
        if (Wait is not null) return Wait(context, version);
        return FailStage == "wait" ? Fail(new ModWaitForEventsResponseDto { Result = new() { Accepted = false } })
            : Task.FromResult(Ok(new ModWaitForEventsResponseDto { Changed = WaitChanged, NotificationVersion = version + 1, EventQueue = QueueOf("wait") }));
    }
    public Task<ClashOfRimClientNetworkResult<ModPullEventDetailsResponseDto>> DetailsAsync(ClashOfRimClientNetworkContext context, IReadOnlyList<string> ids, CancellationToken token)
    {
        Calls.Enqueue("details:" + string.Join(",", ids)); Contexts.Enqueue(context);
        return FailStage == "details" ? Fail(new ModPullEventDetailsResponseDto { Result = new() { Accepted = false, ErrorCode = 9 } }) : Detail(ids);
    }
    private Task<ClashOfRimClientNetworkResult<T>> Fail<T>(T rejected) => Failure switch
    {
        "http" => Task.FromResult(ClashOfRimClientNetworkResult<T>.Failed("http", "failure")),
        "null" => Task.FromResult(Ok<T>(default!)),
        "reject" => Task.FromResult(Ok(rejected)),
        _ => throw new InvalidOperationException("read failed")
    };
    internal static ModEventQueueSummaryDto QueueOf(string id) => new() { DirectlyProcessable = new() { new() { EventId = id } } };
    private static ClashOfRimClientNetworkResult<T> Ok<T>(T value) => ClashOfRimClientNetworkResult<T>.Ok(value);
}
