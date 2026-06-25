using System;
using AIRsLight.ClashOfRim.ClientSnapshots;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    private Action? pendingUnconfirmedSnapshotRetry;
    private string pendingUnconfirmedSnapshotOperation = string.Empty;
    private string pendingUnconfirmedSnapshotMessage = string.Empty;

    private void ShowUnconfirmedSnapshotFailure(string operation, string message, Action retryUpload, bool allowRetry = false)
    {
        if (retryUpload is null)
        {
            throw new ArgumentNullException(nameof(retryUpload));
        }

        BeginLocalAtomicMutation(operation, message);
        pendingUnconfirmedSnapshotOperation = operation ?? string.Empty;
        pendingUnconfirmedSnapshotMessage = message ?? string.Empty;
        pendingUnconfirmedSnapshotRetry = retryUpload;

        EnqueueClashOfRimMainThreadAction(() =>
        {
            SnapshotConfirmationFailureWindow? existing = Find.WindowStack.WindowOfType<SnapshotConfirmationFailureWindow>();
            existing?.Close();

            Find.WindowStack.Add(new SnapshotConfirmationFailureWindow(
                pendingUnconfirmedSnapshotOperation,
                pendingUnconfirmedSnapshotMessage,
                allowRetry ? RetryPendingUnconfirmedSnapshotUpload : null,
                ReturnToMainMenuAfterUnconfirmedSnapshotFailure));
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

    private void RetryPendingUnconfirmedSnapshotUpload()
    {
        Action? retry = pendingUnconfirmedSnapshotRetry;
        if (retry is null)
        {
            Messages.Message(
                ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.RetryMissing"),
                MessageTypeDefOf.RejectInput,
                historical: false);
            return;
        }

        retry();
    }

    private void ReturnToMainMenuAfterUnconfirmedSnapshotFailure()
    {
        Messages.Message(
            ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.ReturningToMainMenu"),
            MessageTypeDefOf.NeutralEvent,
            historical: false);
        if (snapshotUploadInProgress)
        {
            EndSnapshotUploadTransaction();
        }
        else
        {
            manualSyncInProgress = false;
        }

        ClearLocalAtomicMutation();
        playerColonySiteRegistrationSuppressed = false;
        ClearPendingUnconfirmedSnapshotFailure();
        CloseUnconfirmedSnapshotFailureWindow();
        GenScene.GoToMainMenu();
    }

    private void ClearPendingUnconfirmedSnapshotFailure()
    {
        pendingUnconfirmedSnapshotRetry = null;
        pendingUnconfirmedSnapshotOperation = string.Empty;
        pendingUnconfirmedSnapshotMessage = string.Empty;
    }
}
