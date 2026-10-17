using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Events;

internal interface IClientEventReadProtocol
{
    Task<ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto>> PullAsync(ClashOfRimClientNetworkContext context, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModWaitForEventsResponseDto>> WaitAsync(ClashOfRimClientNetworkContext context, long knownVersion, int timeoutSeconds, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModPullEventDetailsResponseDto>> DetailsAsync(ClashOfRimClientNetworkContext context, IReadOnlyList<string> ids, CancellationToken token);
}

// Owns event request construction. HTTP, JSON and session-expiry handling stay in the shared transport.
internal sealed class ClientEventProtocol : IClientEventReadProtocol
{
    private readonly ClientProtocolTransport transport;
    private readonly Func<string> notConfiguredMessage;

    internal ClientEventProtocol(ClientProtocolTransport transport, Func<string> notConfiguredMessage)
    { this.transport = transport; this.notConfiguredMessage = notConfiguredMessage; }

    public Task<ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto>> PullAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
    {
        if (!context.IsConfigured) return NotConfigured<ModPullPendingEventsResponseDto>();
        return transport.ForContext(context).PostAsync<ModPullPendingEventsRequestDto, ModPullPendingEventsResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.PullPendingEvents).Route,
            new() { AuthToken = context.AuthToken, UserId = context.UserId, ColonyId = context.ColonyId,
                CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty }, token);
    }

    public Task<ClashOfRimClientNetworkResult<ModWaitForEventsResponseDto>> WaitAsync(ClashOfRimClientNetworkContext context,
        long knownVersion, int timeoutSeconds, CancellationToken token)
    {
        if (!context.IsConfigured) return NotConfigured<ModWaitForEventsResponseDto>();
        return transport.ForContext(context).PostAsync<ModWaitForEventsRequestDto, ModWaitForEventsResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.WaitForEvents).Route,
            new() { AuthToken = context.AuthToken, UserId = context.UserId, ColonyId = context.ColonyId,
                CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty, KnownNotificationVersion = knownVersion,
                TimeoutSeconds = timeoutSeconds }, token);
    }

    public Task<ClashOfRimClientNetworkResult<ModPullEventDetailsResponseDto>> DetailsAsync(ClashOfRimClientNetworkContext context,
        IReadOnlyList<string> ids, CancellationToken token)
    {
        if (!context.IsConfigured) return NotConfigured<ModPullEventDetailsResponseDto>();
        return transport.ForContext(context).PostAsync<ModPullEventDetailsRequestDto, ModPullEventDetailsResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.PullEventDetails).Route,
            new() { AuthToken = context.AuthToken, UserId = context.UserId, ColonyId = context.ColonyId,
                CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty, EventIds = ids.ToList() }, token);
    }

    private Task<ClashOfRimClientNetworkResult<T>> NotConfigured<T>() => Task.FromResult(
        ClashOfRimClientNetworkResult<T>.Failed("NotConfigured", notConfiguredMessage()));
}
