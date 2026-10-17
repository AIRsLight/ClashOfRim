using System;
using AIRsLight.ClashOfRim.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

internal static class ClientSnapshotPackagePresentation
{
    internal static ModSnapshotPackageBuildResult ToPublicResult(ModSnapshotPackageBuildResult result) =>
        result.ErrorKind is null ? result : ModSnapshotPackageBuildResult.Failed(result.ErrorCode!, Message(result.ErrorKind, result.Message)!);

    internal static string? Message(SnapshotPackageError? error, string? detail) => detail ?? (error is null ? null :
        ClashOfRimText.Key(error.Value switch
        {
            SnapshotPackageError.MissingIdentity => "ClashOfRim.SnapshotPackage.MissingIdentity",
            SnapshotPackageError.MissingSavePayload => "ClashOfRim.SnapshotPackage.MissingSavePayload",
            SnapshotPackageError.MissingRimWorldVersion => "ClashOfRim.SnapshotPackage.MissingRimWorldVersion",
            _ => throw new ArgumentOutOfRangeException(nameof(error))
        }));
}
