using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Packages;

internal interface IClientPackageReader
{
    Task<ClashOfRimClientNetworkResult<ModGetPawnPackageResponseDto>> GetPawnPackageAsync(string packageId, CancellationToken cancellationToken = default);
    Task<ClashOfRimClientNetworkResult<ModGetThingPackageResponseDto>> GetThingPackageAsync(string packageId, CancellationToken cancellationToken = default);
}

// A reader belongs to one captured network context. Package hydration has no
// capability to upload packages, change identity, or issue unrelated commands.
internal sealed class ClientPackageReader : IClientPackageReader
{
    private readonly ClientProtocolTransport transport;
    private readonly ClashOfRimClientNetworkContext context;
    private readonly Func<string> notConfiguredMessage;

    internal ClientPackageReader(ClientProtocolTransport transport, ClashOfRimClientNetworkContext context, Func<string> notConfiguredMessage)
    {
        this.transport = transport.ForContext(context);
        this.context = context;
        this.notConfiguredMessage = notConfiguredMessage;
    }

    public Task<ClashOfRimClientNetworkResult<ModGetPawnPackageResponseDto>> GetPawnPackageAsync(string packageId, CancellationToken cancellationToken = default) =>
        Send<ModGetPawnPackageRequestDto, ModGetPawnPackageResponseDto>(ProtocolMessageKind.GetPawnPackage,
            new() { Requester = Requester(), PawnPackageId = packageId }, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModGetThingPackageResponseDto>> GetThingPackageAsync(string packageId, CancellationToken cancellationToken = default) =>
        Send<ModGetThingPackageRequestDto, ModGetThingPackageResponseDto>(ProtocolMessageKind.GetThingPackage,
            new() { Requester = Requester(), ThingPackageId = packageId }, cancellationToken);

    private ModProtocolIdentityDto Requester() => new()
    { UserId = context.UserId, ColonyId = context.ColonyId, SnapshotId = context.CurrentSnapshotId };

    private Task<ClashOfRimClientNetworkResult<TResponse>> Send<TRequest, TResponse>(ProtocolMessageKind kind, TRequest request, CancellationToken token) =>
        context.IsConfigured
            ? transport.PostAsync<TRequest, TResponse>(ProtocolContractManifest.Find(kind).Route, request, token)
            : Task.FromResult(ClashOfRimClientNetworkResult<TResponse>.Failed("NotConfigured", notConfiguredMessage()));
}
