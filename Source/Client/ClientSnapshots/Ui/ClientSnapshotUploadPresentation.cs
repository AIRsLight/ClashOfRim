using AIRsLight.ClashOfRim.Modules.Snapshots;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

internal static class ClientSnapshotUploadPresentation
{
    internal static ModSnapshotUploadResult ToPublicResult(ClientSnapshotUploadResult result)
    {
        if (result.Failure is not { } failure) return ModSnapshotUploadResult.Ok(result.AcceptedSnapshotId!, result.SourcePath!);
        string message = failure.Message ?? (failure.Fallback == SnapshotUploadMessage.LineageMismatch
            ? ClashOfRimText.Key("ClashOfRim.SnapshotUpload.LineageMismatch",
                failure.ExpectedSnapshot!.Named("SERVER"), failure.RuntimeSnapshot!.Named("LOCAL"))
            : ClashOfRimText.Key(failure.Fallback switch
            {
                SnapshotUploadMessage.NotConfigured => "ClashOfRim.SnapshotUpload.NotConfigured",
                SnapshotUploadMessage.UploadFailed => "ClashOfRim.SnapshotUpload.UploadFailed",
                SnapshotUploadMessage.ServerRejected => "ClashOfRim.SnapshotUpload.ServerRejected",
                SnapshotUploadMessage.NoRunningGame => "ClashOfRim.SnapshotUpload.NoRunningGame",
                SnapshotUploadMessage.LineageMissing => "ClashOfRim.SnapshotUpload.LineageMissing",
                _ => "ClashOfRim.SnapshotUpload.BuildFailed"
            }));
        return ModSnapshotUploadResult.Failed(failure.Code, message);
    }
}
