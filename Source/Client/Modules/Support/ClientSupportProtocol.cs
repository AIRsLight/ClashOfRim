using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Support;

internal sealed class ClientSupportProtocol
{
    private readonly ClientProtocolTransport transport;
    private readonly ClashOfRimClientNetworkContext context;
    private readonly Func<string> notConfigured;
    internal ClientSupportProtocol(ClientProtocolTransport transport, ClashOfRimClientNetworkContext context, Func<string> notConfigured)
    { this.transport = transport.ForContext(context); this.context = context; this.notConfigured = notConfigured; }

    internal Task<ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>> FinishSupportPawnAsync(
        string idempotencyKey,
        string eventId,
        string finishReason,
        string pawnGlobalKey,
        string? pawnName,
        bool pawnDead,
        ModPawnExchangePackageDto? pawnPackage,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModFinishSupportPawnResponseDto>();
        }

        var request = new ModFinishSupportPawnRequestDto
        {
            IdempotencyKey = idempotencyKey,
            EventId = eventId,
            AuthToken = context.AuthToken,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            FinishReason = finishReason,
            PawnGlobalKey = pawnGlobalKey,
            PawnName = pawnName,
            PawnDead = pawnDead,
            PawnPackage = pawnPackage
        };

        return transport.PostAsync<ModFinishSupportPawnRequestDto, ModFinishSupportPawnResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.FinishSupportPawn).Route,
            request,
            cancellationToken);
    }

    internal Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> CreateSupportPawnWithSnapshotAsync(
        string idempotencyKey,
        string targetUserId,
        string targetColonyId,
        string? targetSnapshotId,
        string pawnGlobalKey,
        string pawnName,
        ModCrossMapPawnReferenceDto pawnReference,
        ModPawnExchangePackageDto pawnPackage,
        ModEventTargetContextDto targetContext,
        int sourceTile,
        string? sourceCaravanLoadId,
        bool permanentSupport,
        int? supportDurationDays,
        long? expiresAtGameTicks,
        bool autoReturnOnSettlement,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModEventCreationResponseDto>();
        }

        var supportPawn = new ModCreateSupportPawnRequestDto
        {
            IdempotencyKey = idempotencyKey,
            Actor = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            Target = new ModProtocolIdentityDto
            {
                UserId = targetUserId,
                ColonyId = targetColonyId,
                SnapshotId = targetSnapshotId
            },
            PawnGlobalKey = pawnGlobalKey,
            SourceSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            PawnName = pawnName,
            TemporaryControl = true,
            ExpectedReturnAtUtc = null,
            PawnReference = pawnReference,
            PawnPackage = pawnPackage,
            TargetContext = targetContext,
            SourceTile = sourceTile,
            SourceCaravanLoadId = sourceCaravanLoadId,
            PermanentSupport = permanentSupport,
            SupportDurationDays = supportDurationDays,
            ExpiresAtGameTicks = expiresAtGameTicks,
            AutoReturnOnSettlement = autoReturnOnSettlement
        };
        confirmedSnapshot.SnapshotUploadKind = ModSnapshotUploadKinds.SupportPawnCreationConfirmation;
        var request = new ModCreateSupportPawnWithSnapshotRequestDto
        {
            SupportPawn = supportPawn,
            ConfirmedSnapshot = confirmedSnapshot
        };

        return transport.PostSnapshotMultipartAsync<ModCreateSupportPawnWithSnapshotRequestDto, ModEventCreationResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.CreateSupportPawnWithSnapshot).Route,
            request,
            confirmedPayload,
            cancellationToken);
    }

    private Task<ClashOfRimClientNetworkResult<T>> NotConfigured<T>() =>
        Task.FromResult(ClashOfRimClientNetworkResult<T>.Failed("NotConfigured", notConfigured()));
}
