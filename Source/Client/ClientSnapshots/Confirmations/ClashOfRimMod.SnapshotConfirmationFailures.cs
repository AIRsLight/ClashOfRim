using System;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim;

// Temporary entry points for domains still awaiting M5-M8 migration.
public sealed partial class ClashOfRimMod
{
    private void ShowUnconfirmedSnapshotFailure(LocalMutationKind operation, string message, Action retryUpload, bool allowRetry = false)
        => snapshotFailureRecovery.Show(operation, message, retryUpload, allowRetry);

    private void CloseUnconfirmedSnapshotFailureWindow() => snapshotFailureRecovery.Close();
}
