using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Packages;

internal interface IClientPackageWriter
{
    Task<ClashOfRimClientNetworkResult<ModStorePawnPackageResponseDto>> StorePawnPackageAsync(string key, ModPawnExchangePackageDto package, CancellationToken cancellationToken = default);
    Task<ClashOfRimClientNetworkResult<ModStorePawnPackagePreviewResponseDto>> StorePawnPackagePreviewAsync(string packageId, byte[] preview, CancellationToken cancellationToken = default);
    Task<ClashOfRimClientNetworkResult<ModStoreThingPackageResponseDto>> StoreThingPackageAsync(string key, ModThingStatePackageDto package, CancellationToken cancellationToken = default);
}

internal sealed class ClientPackageWriter : IClientPackageWriter
{
    private readonly ClientProtocolTransport transport;
    private readonly ClashOfRimClientNetworkContext context;
    private readonly Func<string> notConfigured;
    internal ClientPackageWriter(ClientProtocolTransport transport, ClashOfRimClientNetworkContext context, Func<string> notConfigured)
    { this.transport = transport.ForContext(context); this.context = context; this.notConfigured = notConfigured; }

    public Task<ClashOfRimClientNetworkResult<ModStorePawnPackageResponseDto>> StorePawnPackageAsync(
        string idempotencyKey,
        ModPawnExchangePackageDto pawnPackage,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModStorePawnPackageResponseDto>();
        }

        var request = new ModStorePawnPackageRequestDto
        {
            IdempotencyKey = idempotencyKey,
            Owner = Identity(),
            PawnPackage = pawnPackage
        };

        return transport.PostAsync<ModStorePawnPackageRequestDto, ModStorePawnPackageResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.StorePawnPackage).Route,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModStorePawnPackagePreviewResponseDto>> StorePawnPackagePreviewAsync(
        string pawnPackageId,
        byte[] previewPng,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModStorePawnPackagePreviewResponseDto>();
        }

        var request = new ModStorePawnPackagePreviewRequestDto
        {
            Owner = Identity(),
            PawnPackageId = pawnPackageId
        };

        return transport.PostSnapshotMultipartAsync<ModStorePawnPackagePreviewRequestDto, ModStorePawnPackagePreviewResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.StorePawnPackagePreview).Route,
            request,
            previewPng,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModStoreThingPackageResponseDto>> StoreThingPackageAsync(
        string idempotencyKey,
        ModThingStatePackageDto thingPackage,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModStoreThingPackageResponseDto>();
        }

        var request = new ModStoreThingPackageRequestDto
        {
            IdempotencyKey = idempotencyKey,
            Owner = Identity(),
            ThingPackage = thingPackage
        };

        return transport.PostAsync<ModStoreThingPackageRequestDto, ModStoreThingPackageResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.StoreThingPackage).Route,
            request,
            cancellationToken);
    }

    private Task<ClashOfRimClientNetworkResult<T>> NotConfigured<T>() =>
        Task.FromResult(ClashOfRimClientNetworkResult<T>.Failed("NotConfigured", notConfigured()));
    private ModProtocolIdentityDto Identity() => new() { UserId = context.UserId, ColonyId = context.ColonyId, SnapshotId = context.CurrentSnapshotId };
}
