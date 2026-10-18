using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientRaidTransport : IClientRaidPreparationTransport, IClientRaidLaunchTransport
{
    public async Task<ClashOfRimClientNetworkResult<ModPrepareRaidResponseDto>> PrepareAsync(ClashOfRimClientNetworkContext context,
        ClientRaidPreparationCommand command, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var transport = new ClientProtocolTransport(http, context, new ClientProtocolEnvironment());
        var protocol = new ClientRaidProtocol(transport, context, () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        var target = command.Target;
        return await protocol.PrepareRaidAsync(command.IdempotencyKey, target.UserId, target.ColonyId, target.WorldObjectId, target.MapId,
            target.Tile, command.Hostile, command.OpponentKind, token).ConfigureAwait(false);
    }

    public async Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> SubmitAsync(ClashOfRimClientNetworkContext context,
        ClientRaidLaunchCommand command, ModSnapshotPackageMetadataDto snapshot, byte[] payload, CancellationToken token)
    {
        using var http = new HttpClient();
        var transport = new ClientProtocolTransport(http, context, new ClientProtocolEnvironment());
        var protocol = new ClientRaidProtocol(transport, context, () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        var target = command.Target;
        return await protocol.CreateRaidWithSnapshotAsync(command.IdempotencyKey, command.PreparationId, target.UserId, target.ColonyId,
            command.DefenderSnapshotId, target.WorldObjectId, target.MapId, target.Tile,
            true, false, 1_000_000, null, command.Force.PawnGlobalKeys, command.Force.CarriedThings,
            snapshot, payload, command.GuardDeploymentId, cancellationToken: token).ConfigureAwait(false);
    }
}
