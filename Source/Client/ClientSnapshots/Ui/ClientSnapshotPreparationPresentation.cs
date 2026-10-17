using AIRsLight.ClashOfRim.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

internal static class ClientSnapshotPreparationPresentation
{
    internal static string FailureReason(SnapshotUploadFailure failure) => failure.Fallback == SnapshotUploadMessage.BuildFailed
        ? $"{failure.Code} {ClientSnapshotPackagePresentation.Message(failure.PackageError, failure.Message)}"
        : failure.Message ?? $"{failure.Code} {failure.Code}";
}
