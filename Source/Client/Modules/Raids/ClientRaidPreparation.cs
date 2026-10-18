using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.RemoteMaps;

namespace AIRsLight.ClashOfRim.Modules.Raids;

// Preparation has no local game effects. Only its accepted preparation ID can
// authorize the target download; the returned package is then handed to launch.
internal sealed class ClientRaidPreparation
{
    private readonly IClientRaidPreparationTransport transport;
    private readonly IClientRemoteSnapshotTransport snapshots;
    internal ClientRaidPreparation(IClientRaidPreparationTransport transport, IClientRemoteSnapshotTransport snapshots)
    { this.transport = transport; this.snapshots = snapshots; }

    internal async Task<ClientRaidPreparationResult> PrepareAsync(ClashOfRimClientNetworkContext context,
        ClientRaidPreparationCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = await transport.PrepareAsync(context, command, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!result.Success || result.Response is null)
            return new(null, new(ClientRaidPreparationFailureKind.Transport, result.ErrorCode, result.Message));
        var response = result.Response;
        if (response.Result is { Accepted: false } rejected)
            return new(null, new(ClientRaidPreparationFailureKind.Rejected, rejected.ErrorCode, rejected.Message));
        if (string.IsNullOrWhiteSpace(response.RaidPreparationId))
            return new(null, new(ClientRaidPreparationFailureKind.MissingPreparation));

        string eventId = string.IsNullOrWhiteSpace(response.RaidEventId) ? LocalEventId(command.IdempotencyKey) : response.RaidEventId!;
        string preparationId = response.RaidPreparationId!;
        TimeSpan duration = Duration(response.RaidMaxDurationMinutes, TimeSpan.FromMinutes(15));
        TimeSpan grace = Duration(response.RaidTimeoutGraceMinutes, TimeSpan.FromMinutes(2));
        var guards = response.GuardDeployment is null ? null : ClientProtocolJson.Deserialize<ModRaidGuardDeploymentDto>(
            ClientProtocolJson.Serialize(response.GuardDeployment), type => type);
        var snapshot = await snapshots.DownloadAsync(context, new ClientSnapshotDownloadTarget(command.Target.UserId,
            command.Target.ColonyId, preparationId, ClientRemoteMapModes.RaidPreparation), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!ClientRemoteSnapshotDownload.TryGetOpenPayload(snapshot, out var payload, out var failure))
            return new(null, new(ClientRaidPreparationFailureKind.Download, Download: failure));
        return new(new(eventId, preparationId, duration, grace, guards, payload!), null);
    }

    private static string LocalEventId(string key) => "raid:" + string.Concat((key ?? string.Empty).Select(ch => char.IsLetterOrDigit(ch) ? ch : '-'));
    private static TimeSpan Duration(double? minutes, TimeSpan fallback) =>
        minutes is null || double.IsNaN(minutes.Value) || double.IsInfinity(minutes.Value) || minutes.Value <= 0d
            ? fallback : TimeSpan.FromMinutes(minutes.Value);
}

internal sealed record ClientRaidTarget(string UserId, string ColonyId, string WorldObjectId, string MapId, int? Tile);
internal sealed record ClientRaidPreparationCommand(string IdempotencyKey, ClientRaidTarget Target, bool Hostile = true, string OpponentKind = "Player");
internal sealed record ClientPreparedRaid(string EventId, string PreparationId, TimeSpan BattleDuration, TimeSpan SettlementGraceDuration,
    ModRaidGuardDeploymentDto? Guards, RemoteSnapshotOpenPayload Snapshot);
internal enum ClientRaidPreparationFailureKind { Transport, Rejected, MissingPreparation, Download }
internal sealed record ClientRaidPreparationFailure(ClientRaidPreparationFailureKind Kind, object? Code = null, string? Message = null,
    ClientRemoteSnapshotFailure? Download = null);
internal sealed record ClientRaidPreparationResult(ClientPreparedRaid? Prepared, ClientRaidPreparationFailure? Failure);

internal interface IClientRaidPreparationTransport
{
    Task<ClashOfRimClientNetworkResult<ModPrepareRaidResponseDto>> PrepareAsync(ClashOfRimClientNetworkContext context,
        ClientRaidPreparationCommand command, CancellationToken token);
}
