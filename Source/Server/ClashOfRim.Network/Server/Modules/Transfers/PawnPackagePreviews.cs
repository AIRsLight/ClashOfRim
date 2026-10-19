using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Transfers;

internal enum PawnPreviewStatus { Available, InvalidRequest, Unauthorized, NotFound }
internal sealed record PawnPreview(PawnPreviewStatus Status, byte[]? PngBytes = null, string? Message = null);

internal sealed class PawnPackagePreviews(PawnPackageRegistry packages)
{
    internal StorePawnPackagePreviewResponse Store(StorePawnPackagePreviewRequest? request, byte[]? bytes, AuthTokenPrincipal? principal)
    {
        StorePawnPackagePreviewResponse Reject(ProtocolErrorCode code, string message) =>
            new(ProtocolResponse.Reject(code, message), request?.PawnPackageId, contentSha256: null);
        if (request?.Owner is null || string.IsNullOrWhiteSpace(request.Owner.UserId)
            || string.IsNullOrWhiteSpace(request.PawnPackageId) || bytes is null)
            return Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("PawnPackage.PreviewMissingFields"));
        if (principal is null || !SessionPrincipalAuthorization.Matches(principal, request.Owner.UserId, request.Owner.ColonyId))
            return Reject(ProtocolErrorCode.Unauthorized, ServerText.Format("Auth.Failed"));
        if (!packages.TryGetRecord(request.PawnPackageId, out var package) || package is null)
            return Reject(ProtocolErrorCode.EventNotFound, ServerText.Format("PawnPackage.NotFound"));
        if (!string.Equals(package.OwnerUserId, request.Owner.UserId, StringComparison.Ordinal)
            || !string.Equals(package.OwnerColonyId, request.Owner.ColonyId, StringComparison.Ordinal))
            return Reject(ProtocolErrorCode.Unauthorized, ServerText.Format("Auth.Failed"));
        if (!PawnPackagePreviewValidator.TryValidate(bytes, out int width, out int height, out string hash, out string error))
            return Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("PawnPackage.PreviewInvalid", ("MESSAGE", error)));
        try
        {
            packages.StorePreview(new StoredPawnPackagePreviewRecord(request.PawnPackageId, hash, bytes, width, height, DateTimeOffset.UtcNow));
            return new(ProtocolResponse.Ok(ServerText.Format("PawnPackage.PreviewStored")), request.PawnPackageId, hash);
        }
        catch (InvalidOperationException ex)
        {
            return Reject(ProtocolErrorCode.ServerRejected, ServerText.Format("PawnPackage.PreviewStoreFailed", ("MESSAGE", ex.Message)));
        }
    }

    internal PawnPreview Read(GetPawnPackagePreviewRequest request, AuthTokenPrincipal? principal)
    {
        if (request.Requester is null || string.IsNullOrWhiteSpace(request.Requester.UserId) || string.IsNullOrWhiteSpace(request.PawnPackageId))
            return new(PawnPreviewStatus.InvalidRequest, Message: ServerText.Format("PawnPackage.PreviewMissingFields"));
        if (principal is null || !SessionPrincipalAuthorization.Matches(principal, request.Requester.UserId, request.Requester.ColonyId))
            return new(PawnPreviewStatus.Unauthorized);
        if (!packages.TryGetPreview(request.PawnPackageId, out var preview) || preview is null)
            return new(PawnPreviewStatus.NotFound, Message: ServerText.Format("PawnPackage.PreviewNotFound"));
        return new(PawnPreviewStatus.Available, preview.PngBytes);
    }
}
