using System;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

internal static class ClientSnapshotRestorePresentation
{
    internal static string Format(ClientColonySnapshotFailure failure) => failure.Kind switch
    {
        ClientColonySnapshotFailureKind.Metadata => ClashOfRimText.Key("ClashOfRim.DownloadExistingSnapshotFailed",
            failure.Code.Named("CODE"), failure.Message.Named("MESSAGE")),
        ClientColonySnapshotFailureKind.Rejected => ClashOfRimText.Key("ClashOfRim.DownloadExistingSnapshotRejected",
            (failure.Message ?? ClashOfRimText.Key("ClashOfRim.UnknownReason")).Named("REASON")),
        ClientColonySnapshotFailureKind.MissingSnapshot => ClashOfRimText.Key("ClashOfRim.DownloadExistingSnapshotMissingPackage"),
        ClientColonySnapshotFailureKind.Payload => ClashOfRimText.Key("ClashOfRim.DownloadExistingSnapshotPayloadFailed",
            failure.Code.Named("CODE"), (failure.Message ?? ClashOfRimText.Key("ClashOfRim.UnknownReason")).Named("MESSAGE")),
        ClientColonySnapshotFailureKind.Identity => ClashOfRimText.Key("ClashOfRim.DownloadExistingSnapshotMismatch",
            failure.Expected.Named("EXPECTED"), failure.Actual.Named("ACTUAL")),
        ClientColonySnapshotFailureKind.SnapshotId => ClashOfRimText.Key("ClashOfRim.SnapshotRestore.SnapshotIdMismatch",
            failure.Expected.Named("EXPECTED"), failure.Actual.Named("ACTUAL")),
        ClientColonySnapshotFailureKind.Save => Format(failure.SaveFailure!),
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };

    internal static string Format(ClientSnapshotRestoreFailure failure)
    {
        if (failure.Kind == ClientSnapshotRestoreFailureKind.UnsupportedEncoding)
            return ClashOfRimText.Key("ClashOfRim.SnapshotRestore.DecodeFailed",
                ClashOfRimText.Key("ClashOfRim.SnapshotRestore.UnsupportedEncoding", (failure.Message ?? "").Named("ENCODING")).Named("MESSAGE"));
        string key = "ClashOfRim.SnapshotRestore." + failure.Kind;
        return failure.Kind is ClientSnapshotRestoreFailureKind.DecodeFailed or ClientSnapshotRestoreFailureKind.MetadataRewriteFailed
            ? ClashOfRimText.Key(key, failure.Message.Named("MESSAGE")) : ClashOfRimText.Key(key);
    }
}
