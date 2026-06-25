using AIRsLight.ClashOfRim.ClientNetwork;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public static class RemoteSnapshotDownloadValidator
{
    public static bool TryGetOpenPayload(
        ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto> metadata,
        ClashOfRimClientNetworkResult<byte[]>? payload,
        RemoteSnapshotDownloadFailureKeys failureKeys,
        out RemoteSnapshotOpenPayload? openPayload,
        out string failureReason)
    {
        openPayload = null;
        failureReason = string.Empty;
        if (!metadata.Success || metadata.Response is null || metadata.Response.Result?.Accepted != true)
        {
            failureReason = ClashOfRimText.Key(
                failureKeys.MetadataFailed,
                metadata.ErrorCode.Named("CODE"),
                (metadata.Response?.Result?.Message ?? metadata.Message).Named("MESSAGE"));
            return false;
        }

        ModSnapshotPackageMetadataDto? package = metadata.Response.Package;
        string? snapshotId = metadata.Response.SnapshotId ?? package?.SnapshotId;
        if (package is null || string.IsNullOrWhiteSpace(snapshotId))
        {
            failureReason = ClashOfRimText.Key(failureKeys.MissingSnapshot);
            return false;
        }

        if (payload is null || !payload.Success || payload.Response is null)
        {
            failureReason = ClashOfRimText.Key(
                failureKeys.PayloadFailed,
                (payload?.ErrorCode ?? "MissingPayload").Named("CODE"),
                (payload?.Message ?? ClashOfRimText.Key("ClashOfRim.UnknownReason")).Named("MESSAGE"));
            return false;
        }

        openPayload = new RemoteSnapshotOpenPayload(snapshotId!, package, payload.Response);
        return true;
    }
}
