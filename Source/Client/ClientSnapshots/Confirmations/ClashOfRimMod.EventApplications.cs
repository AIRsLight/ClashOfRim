using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Gifts;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    private void StartAutomaticEventRefresh(string reason) => _ = eventRefresh.StartAutomatic(reason);
    private void StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation() => _ = eventRefresh.ResumeDeferred();
    internal void StartManualPullPendingEvents() => _ = eventRefresh.StartManual(ClientEventReadKind.Queue);
    internal void StartManualWaitForEvents() => _ = eventRefresh.StartManual(ClientEventReadKind.Wait);
    internal void StartManualPullEventDetails() => _ = eventRefresh.StartManual(ClientEventReadKind.Details);

    internal void ProcessFirstGift(GiftClientDecision decision) => eventApplications.ProcessFirstGift(decision);
    internal void StartManualRejectFirstGift() => eventApplications.RejectFirstGift();
    internal void StartManualConfirmFirstGift() => eventApplications.ConfirmFirstGift();

    private void StartConfirmLocalMutationSnapshot(ClientLocalSnapshotRequest request) => _ = localSnapshotConfirmation.StartAsync(request);

    internal void StartManualSnapshotUpload() => _ = snapshotSaving.StartAsync(ClientSnapshotSaveKind.Manual);
}
