using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Diplomacy;

internal sealed class ClientDiplomacyProtocol : IClientDiplomacyCreationTransport
{
    private readonly ClientProtocolTransport transport;
    private readonly Func<string> notConfiguredMessage;
    internal ClientDiplomacyProtocol(ClientProtocolTransport transport, Func<string> notConfiguredMessage)
    { this.transport = transport; this.notConfiguredMessage = notConfiguredMessage; }
    public Task<ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto>> CreateAsync(
        ClashOfRimClientNetworkContext context, ClientDiplomacyCommand command, CancellationToken token) => context.IsConfigured
        ? transport.ForContext(context).PostAsync<ModCreateDiplomacyEventRequestDto, ModDiplomacyEventResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.CreateDiplomacyEvent).Route, new()
            {
                IdempotencyKey = command.IdempotencyKey,
                Actor = new() { UserId = context.UserId, ColonyId = context.ColonyId, SnapshotId = context.CurrentSnapshotId },
                Target = new() { UserId = command.Target.UserId, ColonyId = command.Target.ColonyId, SnapshotId = null },
                Kind = command.Kind, Message = command.Message, ExpiresAtUtc = command.ExpiresAtUtc?.ToString("O", CultureInfo.InvariantCulture), AuthToken = context.AuthToken
            }, token)
        : Task.FromResult(ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto>.Failed("NotConfigured", notConfiguredMessage()));
}
