using AIRsLight.ClashOfRim.Modules.Snapshots;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal bool StartMainMenuServerEntryFlow(string? serverBaseUrl, string? userId, string? password, bool createAccountIfMissing = false)
        => worldEntryStart.TryStart(serverBaseUrl, userId, password, createAccountIfMissing);

    private void StartRestoreAfterRaid(string eventId)
        => _ = snapshotReload.StartAsync(new(sessionLifecycle.ReadContext().CurrentSnapshotId, ClientSnapshotReloadPurpose.Raid));
}
