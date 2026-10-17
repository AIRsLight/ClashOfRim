using System;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    private void ShowUnconfirmedSnapshotFailure(LocalMutationKind operation, string message, Action retryUpload, bool allowRetry = false)
    {
        var failure = localMutations.RecordFailure(operation, message, retryUpload, allowRetry);
        EnqueueClashOfRimMainThreadAction(() =>
        {
            if (!localMutations.IsCurrent(failure)) return;
            SnapshotConfirmationFailureWindow? existing = Find.WindowStack.WindowOfType<SnapshotConfirmationFailureWindow>();
            existing?.Close();

            Find.WindowStack.Add(new SnapshotConfirmationFailureWindow(
                ClientLocalMutationPresentation.OperationName(failure.State.Kind),
                failure.State.Status,
                failure.CanRetry ? () => localMutations.TryRetry(failure) : null,
                () => localMutations.TryExit(failure, ReturnToMainMenuAfterUnconfirmedSnapshotFailure)));
        });
    }

    private static void CloseUnconfirmedSnapshotFailureWindow()
    {
        EnqueueClashOfRimMainThreadAction(() =>
        {
            SnapshotConfirmationFailureWindow? window = Find.WindowStack.WindowOfType<SnapshotConfirmationFailureWindow>();
            window?.Close();
        });
    }

    private void ReturnToMainMenuAfterUnconfirmedSnapshotFailure()
    {
        Messages.Message(
            ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.ReturningToMainMenu"),
            MessageTypeDefOf.NeutralEvent,
            historical: false);
        synchronization.Reset();

        playerColonySiteRegistrationSuppressed = false;
        CloseUnconfirmedSnapshotFailureWindow();
        GenScene.GoToMainMenu();
    }
}
