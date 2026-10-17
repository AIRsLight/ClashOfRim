using static AIRsLight.ClashOfRim.Network.Modules.Transfers.ItemTransferMapper;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult StorePawnPackage(StorePawnPackageRequest request, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.Owner is null
            || string.IsNullOrWhiteSpace(request.Owner.UserId)
            || request.PawnPackage is null)
        {
            return Results.Ok(new StorePawnPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.UploadMissingFields")),
                pawnPackageId: null,
                pawnGlobalId: null));
        }

        if (!TryToPawnExchangePackage(request.PawnPackage, out PawnExchangePackage? package, out string packageFailure)
            || package is null)
        {
            return Results.Ok(new StorePawnPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.Invalid", ("MESSAGE", packageFailure))),
                pawnPackageId: null,
                pawnGlobalId: null));
        }

        try
        {
            StoredPawnPackageRecord record = state.PawnPackages.Store(
                request.IdempotencyKey,
                request.Owner.UserId,
                request.Owner.ColonyId,
                request.Owner.SnapshotId,
                package,
                DateTimeOffset.UtcNow);
            return Results.Ok(new StorePawnPackageResponse(
                ProtocolResponse.Ok(T("PawnPackage.Stored")),
                record.PackageId,
                record.PawnGlobalId));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Results.Ok(new StorePawnPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.Invalid", ("MESSAGE", ex.Message))),
                pawnPackageId: null,
                pawnGlobalId: null));
        }
    }

    private static IResult GetPawnPackage(GetPawnPackageRequest request, ClashOfRimNetworkState state)
    {
        if (request.Requester is null
            || string.IsNullOrWhiteSpace(request.Requester.UserId)
            || string.IsNullOrWhiteSpace(request.PawnPackageId))
        {
            return Results.Ok(new GetPawnPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.DownloadMissingFields")),
                request.PawnPackageId,
                pawnPackage: null));
        }

        if (!state.PawnPackages.TryGetPackage(request.PawnPackageId, out PawnExchangePackage? package, out string message)
            || package is null)
        {
            return Results.Ok(new GetPawnPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, message),
                request.PawnPackageId,
                pawnPackage: null));
        }

        return Results.Ok(new GetPawnPackageResponse(
            ProtocolResponse.Ok(T("PawnPackage.Returned")),
            request.PawnPackageId,
            ToPawnExchangePackageDto(package)));
    }

    private static async Task<IResult> StorePawnPackagePreview(
        HttpRequest httpRequest,
        ClashOfRimNetworkState state)
    {
        MultipartSnapshotRequest<StorePawnPackagePreviewRequest>? multipart =
            await ReadMultipartSnapshotRequest<StorePawnPackagePreviewRequest>(httpRequest);
        StorePawnPackagePreviewRequest? request = multipart?.Request;
        if (request?.Owner is null
            || string.IsNullOrWhiteSpace(request.Owner.UserId)
            || string.IsNullOrWhiteSpace(request.PawnPackageId)
            || multipart is null)
        {
            return Results.Ok(new StorePawnPackagePreviewResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("PawnPackage.PreviewMissingFields")),
                request?.PawnPackageId,
                contentSha256: null));
        }

        if (!MultipartSnapshotAuthentication.MatchesPrincipal(
                httpRequest,
                request.Owner.UserId,
                request.Owner.ColonyId))
        {
            return Results.Ok(new StorePawnPackagePreviewResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")),
                request.PawnPackageId,
                contentSha256: null));
        }

        if (!state.PawnPackages.TryGetRecord(request.PawnPackageId, out StoredPawnPackageRecord? package)
            || package is null)
        {
            return Results.Ok(new StorePawnPackagePreviewResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("PawnPackage.NotFound")),
                request.PawnPackageId,
                contentSha256: null));
        }

        if (!string.Equals(package.OwnerUserId, request.Owner.UserId, StringComparison.Ordinal)
            || !string.Equals(package.OwnerColonyId, request.Owner.ColonyId, StringComparison.Ordinal))
        {
            return Results.Ok(new StorePawnPackagePreviewResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")),
                request.PawnPackageId,
                contentSha256: null));
        }

        if (!PawnPackagePreviewValidator.TryValidate(
                multipart.Payload,
                out int width,
                out int height,
                out string contentSha256,
                out string validationError))
        {
            return Results.Ok(new StorePawnPackagePreviewResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    T("PawnPackage.PreviewInvalid", ("MESSAGE", validationError))),
                request.PawnPackageId,
                contentSha256: null));
        }

        try
        {
            state.PawnPackages.StorePreview(new StoredPawnPackagePreviewRecord(
                request.PawnPackageId,
                contentSha256,
                multipart.Payload,
                width,
                height,
                DateTimeOffset.UtcNow));
            return Results.Ok(new StorePawnPackagePreviewResponse(
                ProtocolResponse.Ok(T("PawnPackage.PreviewStored")),
                request.PawnPackageId,
                contentSha256));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Ok(new StorePawnPackagePreviewResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ServerRejected,
                    T("PawnPackage.PreviewStoreFailed", ("MESSAGE", ex.Message))),
                request.PawnPackageId,
                contentSha256: null));
        }
    }

    private static IResult GetPawnPackagePreview(
        HttpRequest httpRequest,
        GetPawnPackagePreviewRequest request,
        ClashOfRimNetworkState state)
    {
        if (request.Requester is null
            || string.IsNullOrWhiteSpace(request.Requester.UserId)
            || string.IsNullOrWhiteSpace(request.PawnPackageId))
        {
            return Results.BadRequest(T("PawnPackage.PreviewMissingFields"));
        }

        if (!MultipartSnapshotAuthentication.MatchesPrincipal(
                httpRequest,
                request.Requester.UserId,
                request.Requester.ColonyId))
        {
            return Results.Unauthorized();
        }

        if (!state.PawnPackages.TryGetPreview(
                request.PawnPackageId,
                out StoredPawnPackagePreviewRecord? preview)
            || preview is null)
        {
            return Results.NotFound(T("PawnPackage.PreviewNotFound"));
        }

        return Results.File(
            preview.PngBytes,
            "image/png",
            enableRangeProcessing: false);
    }

    private static IResult StoreThingPackage(StoreThingPackageRequest request, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.Owner is null
            || string.IsNullOrWhiteSpace(request.Owner.UserId)
            || request.ThingPackage is null)
        {
            return Results.Ok(new StoreThingPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, "thing package upload missing fields"),
                thingPackageId: null,
                fingerprint: null));
        }

        if (!TryToThingStatePackage(request.ThingPackage, out ThingStatePackage? package, out string packageFailure)
            || package is null)
        {
            return Results.Ok(new StoreThingPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, "invalid thing package: " + packageFailure),
                thingPackageId: null,
                fingerprint: null));
        }

        try
        {
            StoredThingPackageRecord record = state.ThingPackages.Store(
                request.IdempotencyKey,
                request.Owner.UserId,
                request.Owner.ColonyId,
                request.Owner.SnapshotId,
                package,
                DateTimeOffset.UtcNow);
            return Results.Ok(new StoreThingPackageResponse(
                ProtocolResponse.Ok("thing package stored"),
                record.PackageId,
                record.Fingerprint));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or IOException)
        {
            return Results.Ok(new StoreThingPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, "invalid thing package: " + ex.Message),
                thingPackageId: null,
                fingerprint: null));
        }
    }

    private static IResult GetThingPackage(GetThingPackageRequest request, ClashOfRimNetworkState state)
    {
        if (request.Requester is null
            || string.IsNullOrWhiteSpace(request.Requester.UserId)
            || string.IsNullOrWhiteSpace(request.ThingPackageId))
        {
            return Results.Ok(new GetThingPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, "thing package download missing fields"),
                request.ThingPackageId,
                thingPackage: null));
        }

        if (!state.ThingPackages.TryGetPackage(request.ThingPackageId, out ThingStatePackage? package, out string message)
            || package is null)
        {
            return Results.Ok(new GetThingPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, message),
                request.ThingPackageId,
                thingPackage: null));
        }

        return Results.Ok(new GetThingPackageResponse(
            ProtocolResponse.Ok("thing package returned"),
            request.ThingPackageId,
            ToThingStatePackageDto(package)));
    }

}
