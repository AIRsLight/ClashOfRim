using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Raids;

internal sealed class ClientRaidProtocol
{
    private readonly ClientProtocolTransport transport;
    private readonly ClashOfRimClientNetworkContext context;
    private readonly Func<string> notConfigured;
    internal ClientRaidProtocol(ClientProtocolTransport transport, ClashOfRimClientNetworkContext context, Func<string> notConfigured)
    { this.transport = transport.ForContext(context); this.context = context; this.notConfigured = notConfigured; }

    internal Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> CreateRaidWithSnapshotAsync(
        string idempotencyKey,
        string? raidPreparationId,
        string targetUserId,
        string targetColonyId,
        string? targetSnapshotId,
        string targetWorldObjectId,
        string targetMapId,
        int? targetTile,
        bool isHostile,
        bool defenderOnline,
        int defenderWealth,
        string? defenderRaidCooldownUntilUtc,
        IReadOnlyList<string> pawnGlobalKeys,
        IReadOnlyList<ModThingReferenceDto> carriedThings,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        string? guardDeploymentId = null,
        string opponentKind = "Player",
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModEventCreationResponseDto>();
        }

        var raid = new ModCreateRaidRequestDto
        {
            IdempotencyKey = idempotencyKey,
            Attacker = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            Defender = new ModProtocolIdentityDto
            {
                UserId = targetUserId,
                ColonyId = targetColonyId,
                SnapshotId = targetSnapshotId
            },
            IsHostile = isHostile,
            DefenderOnline = defenderOnline,
            DefenderWealth = defenderWealth,
            DefenderRaidCooldownUntilUtc = defenderRaidCooldownUntilUtc,
            RaidPreparationId = raidPreparationId,
            TargetWorldObjectId = targetWorldObjectId,
            TargetMapId = targetMapId,
            TargetTile = targetTile,
            DefenderSnapshotId = targetSnapshotId ?? string.Empty,
            OpponentKind = opponentKind,
            PawnGlobalKeys = pawnGlobalKeys.ToList(),
            CarriedThings = carriedThings.ToList(),
            GuardDeploymentId = guardDeploymentId
        };
        confirmedSnapshot.SnapshotUploadKind = ModSnapshotUploadKinds.RaidCreationConfirmation;
        var request = new ModCreateRaidWithSnapshotRequestDto
        {
            Raid = raid,
            ConfirmedSnapshot = confirmedSnapshot
        };

        return transport.PostSnapshotMultipartAsync<ModCreateRaidWithSnapshotRequestDto, ModEventCreationResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.CreateRaidWithSnapshot).Route,
            request,
            confirmedPayload,
            cancellationToken);
    }

    internal Task<ClashOfRimClientNetworkResult<ModPrepareRaidResponseDto>> PrepareRaidAsync(
        string idempotencyKey,
        string targetUserId,
        string targetColonyId,
        string targetWorldObjectId,
        string targetMapId,
        int? targetTile,
        bool isHostile,
        string opponentKind = "Player",
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModPrepareRaidResponseDto>();
        }

        var request = new ModPrepareRaidRequestDto
        {
            IdempotencyKey = idempotencyKey,
            Attacker = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            Defender = new ModProtocolIdentityDto
            {
                UserId = targetUserId,
                ColonyId = targetColonyId,
                SnapshotId = null
            },
            IsHostile = isHostile,
            TargetWorldObjectId = targetWorldObjectId,
            TargetMapId = targetMapId,
            TargetTile = targetTile,
            OpponentKind = opponentKind
        };

        return transport.PostAsync<ModPrepareRaidRequestDto, ModPrepareRaidResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.PrepareRaid).Route,
            request,
            cancellationToken);
    }

    private Task<ClashOfRimClientNetworkResult<T>> NotConfigured<T>() =>
        Task.FromResult(ClashOfRimClientNetworkResult<T>.Failed("NotConfigured", notConfigured()));
}
