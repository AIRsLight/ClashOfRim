using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Gifts;

internal sealed class ClientGiftProtocol
{
    private readonly ClientProtocolTransport transport;
    private readonly Func<string> notConfigured;
    internal ClientGiftProtocol(ClientProtocolTransport transport, Func<string> notConfigured)
    { this.transport = transport; this.notConfigured = notConfigured; }

    internal Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> CreateGiftWithSnapshotAsync(
        ClashOfRimClientNetworkContext context,
        string idempotencyKey,
        string targetUserId,
        string targetColonyId,
        string? targetSnapshotId,
        IReadOnlyList<ModThingReferenceDto> things,
        string? message,
        ModEventTargetContextDto? targetContext,
        string? deliveryKind,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return Task.FromResult(ClashOfRimClientNetworkResult<ModEventCreationResponseDto>.Failed("NotConfigured", notConfigured()));
        }

        var gift = new ModCreateGiftRequestDto
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
            Things = things.ToList(),
            Message = message,
            TargetContext = targetContext,
            DeliveryKind = deliveryKind
        };
        confirmedSnapshot.SnapshotUploadKind = ModSnapshotUploadKinds.GiftCreationConfirmation;
        var request = new ModCreateGiftWithSnapshotRequestDto
        {
            Gift = gift,
            ConfirmedSnapshot = confirmedSnapshot
        };

        return transport.ForContext(context).PostSnapshotMultipartAsync<ModCreateGiftWithSnapshotRequestDto, ModEventCreationResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.CreateGiftWithSnapshot).Route,
            request,
            confirmedPayload,
            cancellationToken);
    }

}
