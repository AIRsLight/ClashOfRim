using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Events;

internal sealed record ClientEventConfirmationCommand(string IdempotencyKey, string EventId, string? SourceEventId,
    string UserId, string ColonyId, string BaseSnapshotId, ModSnapshotPackageMetadataDto Snapshot, byte[] Payload,
    string ApplicationResult, System.TimeSpan? HttpTimeout = null);
internal sealed record ClientEventBatchConfirmationCommand(string IdempotencyKey, string BaseSnapshotId,
    ModSnapshotPackageMetadataDto Snapshot, byte[] Payload, IReadOnlyCollection<ModConfirmEventApplicationEntryDto> Applications);

internal interface IClientEventConfirmationProtocol
{
    Task<ClashOfRimClientNetworkResult<ModConfirmEventApplicationResponseDto>> ConfirmAsync(
        ClashOfRimClientNetworkContext context, ClientEventConfirmationCommand command, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModConfirmEventApplicationsResponseDto>> ConfirmBatchAsync(
        ClashOfRimClientNetworkContext context, ClientEventBatchConfirmationCommand command, CancellationToken token);
}

// Single and prepared-identity submissions have the same wire contract. Only the target differs.
internal sealed class ClientEventConfirmationProtocol : IClientEventConfirmationProtocol
{
    private readonly ClientProtocolTransport transport;
    private readonly System.Func<string> notConfiguredMessage;
    internal ClientEventConfirmationProtocol(ClientProtocolTransport transport, System.Func<string> notConfiguredMessage)
    { this.transport = transport; this.notConfiguredMessage = notConfiguredMessage; }

    public Task<ClashOfRimClientNetworkResult<ModConfirmEventApplicationResponseDto>> ConfirmAsync(
        ClashOfRimClientNetworkContext context, ClientEventConfirmationCommand command, CancellationToken token)
    {
        if (!context.IsConfigured) return NotConfigured<ModConfirmEventApplicationResponseDto>();
        command.Snapshot.SnapshotUploadKind = ModSnapshotUploadKinds.EventApplicationConfirmation;
        return transport.ForContext(context).PostSnapshotMultipartAsync<ModConfirmEventApplicationRequestDto, ModConfirmEventApplicationResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmEventApplication).Route,
            new() { IdempotencyKey = command.IdempotencyKey, EventId = command.EventId, SourceEventId = command.SourceEventId,
                UserId = command.UserId ?? string.Empty, ColonyId = command.ColonyId ?? string.Empty,
                BaseSnapshotId = command.BaseSnapshotId, ConfirmedSnapshot = command.Snapshot,
                ClientApplicationResult = command.ApplicationResult, AuthToken = context.AuthToken }, command.Payload, token);
    }

    public Task<ClashOfRimClientNetworkResult<ModConfirmEventApplicationsResponseDto>> ConfirmBatchAsync(
        ClashOfRimClientNetworkContext context, ClientEventBatchConfirmationCommand command, CancellationToken token)
    {
        if (!context.IsConfigured) return NotConfigured<ModConfirmEventApplicationsResponseDto>();
        command.Snapshot.SnapshotUploadKind = ModSnapshotUploadKinds.BatchEventApplicationConfirmation;
        return transport.ForContext(context).PostSnapshotMultipartAsync<ModConfirmEventApplicationsRequestDto, ModConfirmEventApplicationsResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmEventApplications).Route,
            new() { IdempotencyKey = command.IdempotencyKey, UserId = context.UserId, ColonyId = context.ColonyId,
                BaseSnapshotId = command.BaseSnapshotId, ConfirmedSnapshot = command.Snapshot,
                Applications = command.Applications?.ToList() ?? new(), AuthToken = context.AuthToken }, command.Payload, token);
    }

    private Task<ClashOfRimClientNetworkResult<T>> NotConfigured<T>() => Task.FromResult(
        ClashOfRimClientNetworkResult<T>.Failed("NotConfigured", notConfiguredMessage()));
}
