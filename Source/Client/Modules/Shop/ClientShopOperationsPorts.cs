using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Shop;

internal sealed record ClientShopCommand(string Key, ModServerShopListingDto Listing, int PurchaseCount,
    int TotalPrice, int ItemCount, IReadOnlyList<ModThingReferenceDto> Delivered);
internal sealed record ClientShopLocalResult(bool Success, IReadOnlyList<ModThingReferenceDto> Delivered, string Message);

internal interface IClientShopRuntime { IClientShopSource Capture(); }
internal interface IClientShopSource
{
    string? Validate(ClientShopCommand command);
    // Called on the owning game thread. Signal before the first irreversible
    // change, including landing, so a partial exchange can never be replayed.
    ClientShopLocalResult Apply(ClashOfRimClientNetworkContext context, ClientShopCommand command, Action mutationStarting);
}
internal interface IClientShopPackageHydration
{
    Task HydrateAsync(ClashOfRimClientNetworkContext context, IReadOnlyList<ModServerShopListingDto> listings, CancellationToken token);
}
internal interface IClientShopPurchaseTransport : IClientShopPackageHydration
{
    Task<ClashOfRimClientNetworkResult<ModPurchaseServerShopListingResponseDto>> PurchaseAsync(ClashOfRimClientNetworkContext context,
        ClientShopCommand command, ModSnapshotPackageMetadataDto package, byte[] payload, CancellationToken token);
}
internal interface IClientShopOperationsPresentation
{
    string Busy { get; }
    string BaseSnapshotChanged { get; }
    string SnapshotBuildFailed(string? reason);
    void Message(string message, bool rejected);
}
