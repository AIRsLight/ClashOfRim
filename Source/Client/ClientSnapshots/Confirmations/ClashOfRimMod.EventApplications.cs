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

    private sealed class RaidAttackPawnLoad
    {
        public RaidAttackPawnLoad(string globalKey, Pawn pawn)
        {
            GlobalKey = globalKey;
            Pawn = pawn;
        }

        public string GlobalKey { get; }

        public Pawn Pawn { get; }
    }

    private sealed class LocalMutationSnapshotConfirmationRequest
    {
        public LocalMutationKind Operation { get; set; }

        public string UploadingStatus { get; set; } = string.Empty;

        public bool RemoveRaidBattleSessions { get; set; }

        public bool BestEffort { get; set; }

        public Action RetryAction { get; set; } = static () => { };

        public Action<string>? SetStatus { get; set; }

        public Func<ModSnapshotUploadResult, string>? BuildFailureStatus { get; set; }

        public Func<ModSnapshotUploadResult, string>? BuildSuccessStatus { get; set; }

        public Func<Exception, string>? BuildExceptionStatus { get; set; }

        public Action<ModSnapshotUploadResult>? OnSuccessOnMainThread { get; set; }
    }

    private void SetLocalMutationSnapshotConfirmationStatus(LocalMutationSnapshotConfirmationRequest request, string message)
    {
        if (request.SetStatus is null)
        {
            snapshotUploadStatus = message;
            return;
        }

        request.SetStatus(message);
    }

    private void StartConfirmLocalMutationSnapshot(LocalMutationSnapshotConfirmationRequest request)
    {
        if (TryRejectBlockedByLocalAtomicMutation(out string blockedMessage, request.Operation))
        {
            SetLocalMutationSnapshotConfirmationStatus(request, blockedMessage);
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction(out var uploadTransaction))
        {
            string status = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            SetLocalMutationSnapshotConfirmationStatus(request, status);
            Messages.Message(status, request.BestEffort ? MessageTypeDefOf.NeutralEvent : MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(request.Operation, request.UploadingStatus);
        SetLocalMutationSnapshotConfirmationStatus(request, request.UploadingStatus);
        ClientSessionWork.Run(async () =>
        {
            try
            {
                var service = new ModSnapshotUploadService(settings, uploadTransaction);
                ModSnapshotUploadResult result = await ClientSessionWork.Observe(service.UploadConfiguredSnapshotAsync(
                    request.RemoveRaidBattleSessions,
                    snapshotUploadKind: ModSnapshotUploadKinds.EventApplicationConfirmation));
                using var publication = ClientSessionWork.EnterPublication();
                if (!result.Success)
                {
                    string status = request.BuildFailureStatus?.Invoke(result)
                        ?? ClashOfRimText.Key(
                            "ClashOfRim.LocalMutation.ConfirmFailed",
                            request.UploadingStatus.Named("STATUS"),
                            (result.ErrorCode ?? string.Empty).Named("CODE"),
                            (result.Message ?? string.Empty).Named("MESSAGE"));
                    SetLocalMutationSnapshotConfirmationStatus(request, status);
                    if (request.BestEffort)
                    {
                        Log.Warning("[ClashOfRim] Best-effort local mutation snapshot upload failed: operation="
                            + request.Operation
                            + ", code="
                            + (result.ErrorCode ?? string.Empty)
                            + ", message="
                            + (result.Message ?? string.Empty)
                            + ".");
                        ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                        {
                            Messages.Message(status, MessageTypeDefOf.NeutralEvent, historical: false);
                            CompleteLocalAtomicMutation();
                        });
                        return;
                    }

                    ShowUnconfirmedSnapshotFailure(
                        request.Operation,
                        status,
                        request.RetryAction,
                        allowRetry: ClientNetworkFailurePolicy.CanRetry(result.ErrorCode, result.Message));
                    return;
                }

                string successStatus = request.BuildSuccessStatus?.Invoke(result)
                    ?? ClashOfRimText.Key(
                        "ClashOfRim.LocalMutation.ConfirmSucceeded",
                        (result.AcceptedSnapshotId ?? settings.CurrentSnapshotId).Named("SNAPSHOT"));
                SetLocalMutationSnapshotConfirmationStatus(request, successStatus);
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    CloseUnconfirmedSnapshotFailureWindow();
                    Messages.Message(successStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                    CompleteLocalAtomicMutation();
                    request.OnSuccessOnMainThread?.Invoke(result);
                });
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                string status = request.BuildExceptionStatus?.Invoke(ex)
                    ?? ClashOfRimText.Key(
                        "ClashOfRim.LocalMutation.ConfirmException",
                        request.UploadingStatus.Named("STATUS"),
                        ex.GetType().Name.Named("TYPE"),
                        ex.Message.Named("MESSAGE"));
                SetLocalMutationSnapshotConfirmationStatus(request, status);
                if (request.BestEffort)
                {
                    Log.Warning($"[ClashOfRim] {request.Operation} best-effort local mutation snapshot confirmation failed: " + ex);
                    ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    {
                        Messages.Message(status, MessageTypeDefOf.NeutralEvent, historical: false);
                        CompleteLocalAtomicMutation();
                    });
                    return;
                }

                ShowUnconfirmedSnapshotFailure(
                    request.Operation,
                    status,
                    request.RetryAction,
                    allowRetry: ClientNetworkFailurePolicy.CanRetry(ex));
                Log.Warning($"[ClashOfRim] {request.Operation} local mutation snapshot confirmation failed: " + ex);
            }
            finally
            {
                uploadTransaction.Dispose();
            }
        });
    }



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
