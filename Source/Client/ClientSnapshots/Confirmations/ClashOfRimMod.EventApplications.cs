using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Gifts;
using RimWorld;
using Verse;

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

    internal void StartManualSnapshotUpload()
    {
        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            snapshotUploadStatus = atomicMessage;
            Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction(out var uploadTransaction))
        {
            snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Uploading");

        ClientSessionWork.Run(async () =>
        {
            try
            {
                var service = new ModSnapshotUploadService(settings, uploadTransaction);
                ModSnapshotUploadResult result = await ClientSessionWork.Observe(service.UploadConfiguredSnapshotAsync(
                    snapshotUploadKind: ModSnapshotUploadKinds.ManualUpload));
                using var publication = ClientSessionWork.EnterPublication();
                if (result.Success)
                {
                    snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Succeeded", (result.AcceptedSnapshotId ?? string.Empty).Named("SNAPSHOT"));
                    ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    {
                        Messages.Message(snapshotUploadStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                        RequestWorldConfigurationExtensionSync(ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Succeeded", (result.AcceptedSnapshotId ?? string.Empty).Named("SNAPSHOT")));
                    });
                }
                else
                {
                    snapshotUploadStatus = ClashOfRimText.Key(
                        "ClashOfRim.SnapshotUpload.Failed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    NotifyPlayerMessage(snapshotUploadStatus, MessageTypeDefOf.RejectInput);
                }
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                snapshotUploadStatus = ClashOfRimText.Key(
                    "ClashOfRim.SnapshotUpload.Exception",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Snapshot upload failed: " + ex);
                NotifyPlayerMessage(snapshotUploadStatus, MessageTypeDefOf.RejectInput);
            }
            finally
            {
                uploadTransaction.Dispose();
            }
        });
    }

    internal void StartVanillaMenuSnapshotUpload(Action? onSuccess = null)
    {
        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            snapshotUploadStatus = atomicMessage;
            Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!CanUseVanillaMenuSnapshotUpload)
        {
            snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.Menu.UploadSnapshotNoSession");
            Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction(out var uploadTransaction))
        {
            snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.Menu.UploadSnapshotBusy");
            Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.Menu.UploadSnapshotUploading");

        ClientSessionWork.Run(async () =>
        {
            try
            {
                var service = new ModSnapshotUploadService(settings, uploadTransaction);
                ModSnapshotUploadResult result = await ClientSessionWork.Observe(service.UploadConfiguredSnapshotAsync(
                    snapshotUploadKind: ModSnapshotUploadKinds.ManualUpload));
                using var publication = ClientSessionWork.EnterPublication();
                if (!result.Success)
                {
                    snapshotUploadStatus = ClashOfRimText.Key(
                        "ClashOfRim.Menu.UploadSnapshotFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                        Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false));
                    return;
                }

                snapshotUploadStatus = ClashOfRimText.Key(
                    "ClashOfRim.Menu.UploadSnapshotSucceeded",
                    (result.AcceptedSnapshotId ?? settings.CurrentSnapshotId).Named("SNAPSHOT"));
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    Messages.Message(snapshotUploadStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                    StartRegisterPlayerColonySites();
                    StartSyncWorldConfigurationExtensions();
                    if (onSuccess is not null)
                    {
                        DisconnectMultiplayerSessionForExit();
                    }

                    onSuccess?.Invoke();
                });
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                snapshotUploadStatus = ClashOfRimText.Key(
                    "ClashOfRim.Menu.UploadSnapshotException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Vanilla menu snapshot upload failed: " + ex);
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false));
            }
            finally
            {
                uploadTransaction.Dispose();
            }
        });
    }

    internal void StartAutosaveSnapshotUpload()
    {
        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            snapshotUploadStatus = ClashOfRimText.Key(
                "ClashOfRim.AutosaveSnapshot.SkippedAtomic",
                atomicMessage.Named("REASON"));
            ClashLog.Message("[ClashOfRim] Autosave snapshot upload skipped: " + atomicMessage);
            return;
        }

        if (!CanUseVanillaMenuSnapshotUpload)
        {
            snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.AutosaveSnapshot.SkippedNoSession");
            ClashLog.Message("[ClashOfRim] Autosave snapshot upload skipped: no tracked multiplayer snapshot.");
            return;
        }

        if (!TryBeginSnapshotUploadTransaction(out var uploadTransaction))
        {
            snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.AutosaveSnapshot.SkippedBusy");
            ClashLog.Message("[ClashOfRim] Autosave snapshot upload skipped: sync already running.");
            return;
        }

        snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.AutosaveSnapshot.Uploading");

        ClientSessionWork.Run(async () =>
        {
            try
            {
                var service = new ModSnapshotUploadService(settings, uploadTransaction);
                ModSnapshotUploadResult result = await ClientSessionWork.Observe(service.UploadConfiguredSnapshotAsync(
                    snapshotUploadKind: ModSnapshotUploadKinds.AutoSave));
                using var publication = ClientSessionWork.EnterPublication();
                if (!result.Success)
                {
                    snapshotUploadStatus = ClashOfRimText.Key(
                        "ClashOfRim.AutosaveSnapshot.Failed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    Log.Warning("[ClashOfRim] Autosave snapshot upload failed: " + snapshotUploadStatus);
                    NotifyPlayerMessage(snapshotUploadStatus, MessageTypeDefOf.RejectInput);
                    return;
                }

                snapshotUploadStatus = ClashOfRimText.Key(
                    "ClashOfRim.AutosaveSnapshot.Succeeded",
                    (result.AcceptedSnapshotId ?? settings.CurrentSnapshotId).Named("SNAPSHOT"));
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    Messages.Message(snapshotUploadStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                    StartRegisterPlayerColonySites();
                    StartSyncWorldConfigurationExtensions();
                });
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                snapshotUploadStatus = ClashOfRimText.Key(
                    "ClashOfRim.AutosaveSnapshot.Exception",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Autosave snapshot upload exception: " + ex);
                NotifyPlayerMessage(snapshotUploadStatus, MessageTypeDefOf.RejectInput);
            }
            finally
            {
                uploadTransaction.Dispose();
            }
        });
    }

}
