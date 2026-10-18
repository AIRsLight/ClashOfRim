using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Maps;
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
        if (ClientRemoteSnapshotDownload.TryGetOpenPayload(new RemoteSnapshotDownloadResult(metadata, payload), out openPayload, out var failure))
        {
            failureReason = string.Empty;
            return true;
        }
        failureReason = failure!.Kind switch
        {
            ClientRemoteSnapshotFailureKind.Metadata => ClashOfRimText.Key(failureKeys.MetadataFailed,
                failure.Code.Named("CODE"), failure.Message.Named("MESSAGE")),
            ClientRemoteSnapshotFailureKind.MissingSnapshot => ClashOfRimText.Key(failureKeys.MissingSnapshot),
            _ => ClashOfRimText.Key(failureKeys.PayloadFailed, failure.Code.Named("CODE"),
                (failure.Message ?? ClashOfRimText.Key("ClashOfRim.UnknownReason")).Named("MESSAGE"))
        };
        return false;
    }
}
