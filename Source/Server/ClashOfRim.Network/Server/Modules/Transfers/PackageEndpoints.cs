using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.Transfers;

internal static class PackageEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, ItemTransferPackages packages, PawnPackagePreviews previews)
    {
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.StorePawnPackage).Route,
            (StorePawnPackageRequest request) => Results.Ok(packages.StorePawn(request)));
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetPawnPackage).Route,
            (GetPawnPackageRequest request) => Results.Ok(packages.ReadPawn(request)));
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.StoreThingPackage).Route,
            (StoreThingPackageRequest request) => Results.Ok(packages.StoreThing(request)));
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetThingPackage).Route,
            (GetThingPackageRequest request) => Results.Ok(packages.ReadThing(request)));
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.StorePawnPackagePreview).Route,
            (HttpRequest request) => StorePreview(request, previews));
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetPawnPackagePreview).Route,
            (HttpRequest http, GetPawnPackagePreviewRequest request) => ReadPreview(http, request, previews));
    }

    internal static async Task<IResult> StorePreview(HttpRequest http, PawnPackagePreviews previews)
    {
        var multipart = await MultipartSnapshotReader.Read<StorePawnPackagePreviewRequest>(http);
        return Results.Ok(previews.Store(multipart?.Request, multipart?.Payload, MultipartSnapshotAuthentication.Principal(http)));
    }

    internal static IResult ReadPreview(HttpRequest http, GetPawnPackagePreviewRequest request, PawnPackagePreviews previews)
    {
        var preview = previews.Read(request, MultipartSnapshotAuthentication.Principal(http));
        return preview.Status switch
        {
            PawnPreviewStatus.Available => Results.File(preview.PngBytes!, "image/png", enableRangeProcessing: false),
            PawnPreviewStatus.InvalidRequest => Results.BadRequest(preview.Message),
            PawnPreviewStatus.Unauthorized => Results.Unauthorized(),
            PawnPreviewStatus.NotFound => Results.NotFound(preview.Message),
            _ => throw new ArgumentOutOfRangeException(nameof(preview.Status))
        };
    }
}
