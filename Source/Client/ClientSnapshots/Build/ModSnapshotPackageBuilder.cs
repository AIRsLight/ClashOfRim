using System;
using AIRsLight.ClashOfRim.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

// Public compatibility facade: package data is built without engine dependencies;
// legacy callers still receive localized failure messages.
public static class ModSnapshotPackageBuilder
{
    public const string CurrentPackageVersion = ClientSnapshotPackageBuilder.CurrentPackageVersion;
    public const string PayloadEncoding = ClientSnapshotPackageBuilder.PayloadEncoding;

    public static ModSnapshotPackageBuildResult FromSaveBytes(
        byte[] originalPayload, string sourceLabel, string ownerId, string colonyId, DateTime utcNow) =>
        ClientSnapshotPackagePresentation.ToPublicResult(
            ClientSnapshotPackageBuilder.FromSaveBytes(originalPayload, sourceLabel, ownerId, colonyId, utcNow));
}
