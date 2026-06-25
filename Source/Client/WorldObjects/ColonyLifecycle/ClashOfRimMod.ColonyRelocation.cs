using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    private const string ColonyRelocationConfirmationOperation = "ColonyRelocation";
    private const string ColonyRelocationOperationKey = "ClashOfRim.SnapshotConfirmationFailure.OperationColonyRelocation";
    private bool implicitColonyRelocationFlightPending;
    private string implicitColonyRelocationPreviousSnapshotId = string.Empty;

    private void ShowColonyRelocationFailure(string message, Action retry)
    {
        EnqueueClashOfRimMainThreadAction(() =>
            ShowUnconfirmedSnapshotFailure(
                ClashOfRimText.Key(ColonyRelocationOperationKey),
                message,
                retry));
    }

    internal Command? TryBuildColonyRelocationCommand(Caravan caravan, Command original)
    {
        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(lastSessionId))
        {
            return null;
        }

        if (caravan.Faction != Faction.OfPlayer || !caravan.IsPlayerControlled)
        {
            return null;
        }

        List<MapParent> colonies = FindPlayerHomeSettlements();
        if (colonies.Count == 0)
        {
            return null;
        }

        var command = new Command_Action
        {
            defaultLabel = ClashOfRimText.Key("ClashOfRim.ColonyRelocation.CommandLabel"),
            defaultDesc = ClashOfRimText.Key("ClashOfRim.ColonyRelocation.CommandDesc"),
            icon = original.icon,
            Order = original.Order,
            action = () => OpenColonyRelocationConfirmation(caravan, colonies[0])
        };

        if (colonies.Count != 1)
        {
            command.Disable(ClashOfRimText.Key("ClashOfRim.ColonyRelocation.DisabledMultipleLocalColonies"));
            return command;
        }

        if (original.Disabled && !IsOriginalSettleDisabledOnlyBecauseBaseExists(original.disabledReason))
        {
            command.Disable(original.disabledReason);
            return command;
        }

        if (localAtomicMutationPending)
        {
            command.Disable(ClashOfRimText.Key("ClashOfRim.ColonyRelocation.DisabledAtomic"));
            return command;
        }

        if (snapshotUploadInProgress || manualSyncInProgress)
        {
            command.Disable(ClashOfRimText.Key("ClashOfRim.ColonyRelocation.DisabledSync"));
            return command;
        }

        if (ClashOfRimGameComponent.HasActiveRemoteMapSession
            || ClashOfRimGameComponent.HasActiveRaidBattleSession)
        {
            command.Disable(ClashOfRimText.Key("ClashOfRim.ColonyRelocation.DisabledRemoteMap"));
            return command;
        }

        return command;
    }

    private static bool IsOriginalSettleDisabledOnlyBecauseBaseExists(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        string singleBase = "CommandSettleFailAlreadyHaveBase".Translate().ToString();
        string maxBase = "CommandSettleFailReachedMaximumNumberOfBases".Translate().ToString();
        return string.Equals(reason, singleBase, StringComparison.Ordinal)
            || string.Equals(reason, maxBase, StringComparison.Ordinal);
    }

    private void OpenColonyRelocationConfirmation(Caravan caravan, MapParent oldColony)
    {
        if (caravan.Destroyed || oldColony.Destroyed)
        {
            Messages.Message(ClashOfRimText.Key("ClashOfRim.ColonyRelocation.InvalidTarget"), MessageTypeDefOf.RejectInput, false);
            return;
        }

        Find.WindowStack.Add(new ColonyRelocationConfirmationWindow(
            oldColony.LabelCap,
            caravan.Tile,
            () => StartColonyRelocationPreflight(caravan, oldColony)));
    }

    private void StartColonyRelocationPreflight(Caravan caravan, MapParent oldColony)
    {
        if (!settings.IsConfigured)
        {
            Messages.Message(ClashOfRimText.Key("ClashOfRim.ColonyRelocation.NotConfigured"), MessageTypeDefOf.RejectInput, false);
            return;
        }

        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            Messages.Message(atomicMessage, MessageTypeDefOf.RejectInput, false);
            return;
        }

        string previousSnapshotId = settings.CurrentSnapshotId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(previousSnapshotId))
        {
            Messages.Message(ClashOfRimText.Key("ClashOfRim.ColonyRelocation.MissingSnapshot"), MessageTypeDefOf.RejectInput, false);
            return;
        }

        int targetTile = caravan.Tile;
        int targetTileLayerId = ReadFirstTileLayerId(caravan, "Tile", "tile");
        string idempotencyKey = $"colony-relocation:{settings.UserId}:{settings.ColonyId}:{previousSnapshotId}:{targetTile},{targetTileLayerId}:{DateTime.UtcNow.Ticks}";
        BeginLocalAtomicMutation(
            ClashOfRimText.Key(ColonyRelocationOperationKey),
            ClashOfRimText.Key("ClashOfRim.ColonyRelocation.StatusPreflight"));

        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(httpClient, ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModColonyRelocationResponseDto> result =
                    await client.PreflightColonyRelocationAsync(targetTile, idempotencyKey, targetTileLayerId);
                if (!result.Success || result.Response?.Result?.Accepted != true)
                {
                    string message = result.Response?.Result?.Message
                        ?? result.Message
                        ?? ClashOfRimText.Key("ClashOfRim.ColonyRelocation.PreflightFailed");
                    EnqueueClashOfRimMainThreadAction(() =>
                    {
                        ClearLocalAtomicMutation();
                        Messages.Message(message, MessageTypeDefOf.RejectInput, false);
                    });
                    return;
                }

                EnqueueClashOfRimMainThreadAction(() => BeginLocalColonyRelocation(caravan, oldColony, previousSnapshotId, targetTileLayerId, idempotencyKey));
            }
            catch (Exception ex)
            {
                EnqueueClashOfRimMainThreadAction(() =>
                {
                    ClearLocalAtomicMutation();
                    Messages.Message(
                        ClashOfRimText.Key(
                            "ClashOfRim.ColonyRelocation.PreflightException",
                            ex.GetType().Name.Named("TYPE"),
                            ex.Message.Named("MESSAGE")),
                        MessageTypeDefOf.RejectInput,
                        false);
                });
            }
        });
    }

    private void BeginLocalColonyRelocation(
        Caravan caravan,
        MapParent oldColony,
        string previousSnapshotId,
        int targetTileLayerId,
        string idempotencyKey)
    {
        if (caravan.Destroyed || oldColony.Destroyed)
        {
            ClearLocalAtomicMutation();
            Messages.Message(ClashOfRimText.Key("ClashOfRim.ColonyRelocation.InvalidTarget"), MessageTypeDefOf.RejectInput, false);
            return;
        }

        localAtomicMutationStatus = ClashOfRimText.Key("ClashOfRim.ColonyRelocation.StatusSettling");
        playerColonySiteRegistrationSuppressed = true;
        int targetTile = caravan.Tile;
        try
        {
            SoundDefOf.Tick_High.PlayOneShotOnCamera(null);
            SettleInEmptyTileUtility.Settle(caravan);
            LongEventHandler.QueueLongEvent(
                () => FinishLocalColonyRelocation(oldColony, previousSnapshotId, targetTile, targetTileLayerId, idempotencyKey),
                "ClashOfRim.ColonyRelocation.Generating".Translate(),
                true,
                ex =>
                {
                    playerColonySiteRegistrationSuppressed = false;
                    ShowColonyRelocationFailure(
                        $"{ex.GetType().Name} {ex.Message}",
                        () => FinishLocalColonyRelocation(oldColony, previousSnapshotId, targetTile, targetTileLayerId, idempotencyKey));
                });
        }
        catch (Exception ex)
        {
            playerColonySiteRegistrationSuppressed = false;
            ShowColonyRelocationFailure(
                $"{ex.GetType().Name} {ex.Message}",
                () => BeginLocalColonyRelocation(caravan, oldColony, previousSnapshotId, targetTileLayerId, idempotencyKey));
        }
    }

    private void FinishLocalColonyRelocation(
        MapParent oldColony,
        string previousSnapshotId,
        int targetTile,
        int targetTileLayerId,
        string idempotencyKey)
    {
        try
        {
            localAtomicMutationStatus = ClashOfRimText.Key("ClashOfRim.ColonyRelocation.StatusAbandoningOldColony");
            if (!oldColony.Destroyed)
            {
                oldColony.Abandon(false);
            }

            List<MapParent> colonies = FindPlayerHomeSettlements();
            if (colonies.Count != 1
                || colonies[0].Tile != targetTile
                || ReadFirstTileLayerId(colonies[0], "Tile", "tile") != targetTileLayerId)
            {
                string message = ClashOfRimText.Key(
                    "ClashOfRim.ColonyRelocation.LocalAnchorInvalid",
                    colonies.Count.ToString().Named("COUNT"));
                ShowColonyRelocationFailure(
                    message,
                    () => UploadRelocatedSnapshot(previousSnapshotId, targetTile, targetTileLayerId, idempotencyKey));
                return;
            }

            UploadRelocatedSnapshot(previousSnapshotId, targetTile, targetTileLayerId, idempotencyKey);
        }
        catch (Exception ex)
        {
            ShowColonyRelocationFailure(
                $"{ex.GetType().Name} {ex.Message}",
                () => FinishLocalColonyRelocation(oldColony, previousSnapshotId, targetTile, targetTileLayerId, idempotencyKey));
        }
        finally
        {
            playerColonySiteRegistrationSuppressed = false;
        }
    }

    private void UploadRelocatedSnapshot(string previousSnapshotId, int targetTile, int targetTileLayerId, string idempotencyKey)
    {
        UploadRelocatedSnapshot(
            previousSnapshotId,
            targetTile,
            targetTileLayerId,
            idempotencyKey,
            releaseSiteRegistrationSuppressionOnComplete: false);
    }

    private void UploadRelocatedSnapshot(
        string previousSnapshotId,
        int targetTile,
        int targetTileLayerId,
        string idempotencyKey,
        bool releaseSiteRegistrationSuppressionOnComplete)
    {
        if (!TryBeginSnapshotUploadTransaction(allowExistingManualSync: true))
        {
            ShowColonyRelocationFailure(
                ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy"),
                () => UploadRelocatedSnapshot(previousSnapshotId, targetTile, targetTileLayerId, idempotencyKey, releaseSiteRegistrationSuppressionOnComplete));
            return;
        }

        localAtomicMutationStatus = ClashOfRimText.Key("ClashOfRim.ColonyRelocation.StatusUploadingSnapshot");
        Task.Run(async () =>
        {
            bool confirmationStarted = false;
            try
            {
                var uploadService = new ModSnapshotUploadService(settings);
                ModSnapshotUploadResult upload = await uploadService.UploadConfiguredSnapshotAsync(
                    removeRaidBattleSessions: false,
                    confirmationOperation: ColonyRelocationConfirmationOperation,
                    snapshotUploadKind: ModSnapshotUploadKinds.ColonyRelocation);
                if (!upload.Success || string.IsNullOrWhiteSpace(upload.AcceptedSnapshotId))
                {
                    Log.Warning(
                        "[ClashOfRim] Colony relocation snapshot upload failed before confirm: code="
                        + (upload.ErrorCode ?? string.Empty)
                        + ", message="
                        + (upload.Message ?? string.Empty));
                    ShowColonyRelocationFailure(
                        $"{upload.ErrorCode} {upload.Message}",
                        () => UploadRelocatedSnapshot(previousSnapshotId, targetTile, targetTileLayerId, idempotencyKey, releaseSiteRegistrationSuppressionOnComplete));
                    return;
                }

                confirmationStarted = true;
                string acceptedSnapshotId = upload.AcceptedSnapshotId!;
                ClashLog.Message(
                    "[ClashOfRim] Colony relocation snapshot uploaded; starting confirm: previous="
                    + previousSnapshotId
                    + ", relocated="
                    + acceptedSnapshotId
                    + ", targetTile="
                    + targetTile
                    + ","
                    + targetTileLayerId);
                EnqueueClashOfRimMainThreadAction(() =>
                    ConfirmRelocatedSnapshot(
                        previousSnapshotId,
                        acceptedSnapshotId,
                        targetTile,
                        targetTileLayerId,
                        idempotencyKey,
                        releaseSiteRegistrationSuppressionOnComplete,
                        endSnapshotUploadOnComplete: true));
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim] Colony relocation snapshot upload exception before confirm: " + ex);
                ShowColonyRelocationFailure(
                    $"{ex.GetType().Name} {ex.Message}",
                    () => UploadRelocatedSnapshot(previousSnapshotId, targetTile, targetTileLayerId, idempotencyKey, releaseSiteRegistrationSuppressionOnComplete));
            }
            finally
            {
                if (!confirmationStarted)
                {
                    EndSnapshotUploadTransaction();
                }
            }
        });
    }

    private void ConfirmRelocatedSnapshot(
        string previousSnapshotId,
        string relocatedSnapshotId,
        int targetTile,
        int targetTileLayerId,
        string idempotencyKey)
    {
        ConfirmRelocatedSnapshot(
            previousSnapshotId,
            relocatedSnapshotId,
            targetTile,
            targetTileLayerId,
            idempotencyKey,
            releaseSiteRegistrationSuppressionOnComplete: false);
    }

    private void ConfirmRelocatedSnapshot(
        string previousSnapshotId,
        string relocatedSnapshotId,
        int targetTile,
        int targetTileLayerId,
        string idempotencyKey,
        bool releaseSiteRegistrationSuppressionOnComplete,
        bool endSnapshotUploadOnComplete = false)
    {
        localAtomicMutationStatus = ClashOfRimText.Key("ClashOfRim.ColonyRelocation.StatusConfirming");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(httpClient, ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModColonyRelocationResponseDto> result =
                    await client.ConfirmColonyRelocationAsync(
                        previousSnapshotId,
                        relocatedSnapshotId,
                        targetTile,
                        idempotencyKey,
                        targetTileLayerId);
                if (!result.Success || result.Response?.Result?.Accepted != true)
                {
                    string message = result.Response?.Result?.Message
                        ?? result.Message
                        ?? ClashOfRimText.Key("ClashOfRim.ColonyRelocation.ConfirmFailed");
                    Log.Warning(
                        "[ClashOfRim] Colony relocation confirm rejected: code="
                        + (result.ErrorCode ?? result.Response?.Result?.ErrorCode.ToString() ?? string.Empty)
                        + ", message="
                        + message);
                    ShowColonyRelocationFailure(
                        message,
                        () => ConfirmRelocatedSnapshot(previousSnapshotId, relocatedSnapshotId, targetTile, targetTileLayerId, idempotencyKey, releaseSiteRegistrationSuppressionOnComplete));
                    return;
                }

                EnqueueClashOfRimMainThreadAction(() =>
                {
                    if (releaseSiteRegistrationSuppressionOnComplete)
                    {
                        playerColonySiteRegistrationSuppressed = false;
                    }

                    UpdateOccupiedPlayerColonySites(result.Response.WorldConfiguration);
                    CaptureWorldMapMarkers(result.Response.WorldMapMarkers, source: "colony-relocation");
                    lastRegisteredPlayerColonySiteSignature = null;
                    ClearImplicitColonyRelocationFlightState();
                    CompleteLocalAtomicMutation();
                    CloseUnconfirmedSnapshotFailureWindow();
                    Messages.Message(
                        ClashOfRimText.Key("ClashOfRim.ColonyRelocation.Succeeded"),
                        MessageTypeDefOf.PositiveEvent,
                        false);
                });
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim] Colony relocation confirm exception: " + ex);
                ShowColonyRelocationFailure(
                    $"{ex.GetType().Name} {ex.Message}",
                    () => ConfirmRelocatedSnapshot(previousSnapshotId, relocatedSnapshotId, targetTile, targetTileLayerId, idempotencyKey, releaseSiteRegistrationSuppressionOnComplete));
            }
            finally
            {
                if (endSnapshotUploadOnComplete)
                {
                    EndSnapshotUploadTransaction();
                }
            }
        });
    }

    internal bool TryBeginImplicitColonyRelocationTakeoff(Map map, int targetTile)
    {
        if (map is null || !settings.IsConfigured || string.IsNullOrWhiteSpace(lastSessionId))
        {
            return true;
        }

        if (snapshotUploadInProgress || manualSyncInProgress)
        {
            Messages.Message(ClashOfRimText.Key("ClashOfRim.ColonyRelocation.DisabledSync"), MessageTypeDefOf.RejectInput, false);
            return false;
        }

        string operation = ClashOfRimText.Key(ColonyRelocationOperationKey);
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string atomicMessage))
        {
            Messages.Message(atomicMessage, MessageTypeDefOf.RejectInput, false);
            return false;
        }

        string previousSnapshotId = settings.CurrentSnapshotId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(previousSnapshotId))
        {
            Messages.Message(ClashOfRimText.Key("ClashOfRim.ColonyRelocation.MissingSnapshot"), MessageTypeDefOf.RejectInput, false);
            return false;
        }

        if (!localAtomicMutationPending)
        {
            BeginLocalAtomicMutation(
                operation,
                ClashOfRimText.Key("ClashOfRim.ColonyRelocation.StatusFlight"));
        }
        else
        {
            localAtomicMutationStatus = ClashOfRimText.Key("ClashOfRim.ColonyRelocation.StatusFlight");
        }

        implicitColonyRelocationFlightPending = true;
        implicitColonyRelocationPreviousSnapshotId = previousSnapshotId;
        playerColonySiteRegistrationSuppressed = true;
        lastRegisteredPlayerColonySiteSignature = null;
        ClashLog.Message("[ClashOfRim] Gravship relocation atomic section started: targetTile=" + targetTile);
        return true;
    }

    internal void StartImplicitColonyRelocationConfirmation(Map map, string reason)
    {
        if (map is null)
        {
            return;
        }

        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(lastSessionId))
        {
            return;
        }

        string operation = ClashOfRimText.Key(ColonyRelocationOperationKey);
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string atomicMessage))
        {
            Messages.Message(atomicMessage, MessageTypeDefOf.RejectInput, false);
            return;
        }

        string previousSnapshotId = implicitColonyRelocationFlightPending
            && !string.IsNullOrWhiteSpace(implicitColonyRelocationPreviousSnapshotId)
            ? implicitColonyRelocationPreviousSnapshotId
            : settings.CurrentSnapshotId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(previousSnapshotId))
        {
            Messages.Message(ClashOfRimText.Key("ClashOfRim.ColonyRelocation.MissingSnapshot"), MessageTypeDefOf.RejectInput, false);
            return;
        }

        int targetTile = map.Tile;
        int targetTileLayerId = ReadFirstTileLayerId(map, "Tile", "tile");
        string idempotencyKey = $"implicit-colony-relocation:{settings.UserId}:{settings.ColonyId}:{previousSnapshotId}:{targetTile},{targetTileLayerId}:{DateTime.UtcNow.Ticks}";
        if (!localAtomicMutationPending)
        {
            BeginLocalAtomicMutation(
                operation,
                ClashOfRimText.Key("ClashOfRim.ColonyRelocation.StatusPreflight"));
        }
        else
        {
            localAtomicMutationStatus = ClashOfRimText.Key("ClashOfRim.ColonyRelocation.StatusPreflight");
        }
        playerColonySiteRegistrationSuppressed = true;
        lastRegisteredPlayerColonySiteSignature = null;
        ClashLog.Message("[ClashOfRim] Starting implicit colony relocation confirmation: " + reason + ", targetTile=" + targetTile + "," + targetTileLayerId);

        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(httpClient, ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModColonyRelocationResponseDto> result =
                    await client.PreflightColonyRelocationAsync(targetTile, idempotencyKey, targetTileLayerId);
                if (!result.Success || result.Response?.Result?.Accepted != true)
                {
                    string message = result.Response?.Result?.Message
                        ?? result.Message
                        ?? ClashOfRimText.Key("ClashOfRim.ColonyRelocation.PreflightFailed");
                    ShowColonyRelocationFailure(
                        message,
                        () => StartImplicitColonyRelocationConfirmation(map, reason));
                    return;
                }

                EnqueueClashOfRimMainThreadAction(() =>
                    UploadRelocatedSnapshot(
                        previousSnapshotId,
                        targetTile,
                        targetTileLayerId,
                        idempotencyKey,
                        releaseSiteRegistrationSuppressionOnComplete: true));
            }
            catch (Exception ex)
            {
                ShowColonyRelocationFailure(
                    $"{ex.GetType().Name} {ex.Message}",
                    () => StartImplicitColonyRelocationConfirmation(map, reason));
            }
        });
    }

    private void ClearImplicitColonyRelocationFlightState()
    {
        implicitColonyRelocationFlightPending = false;
        implicitColonyRelocationPreviousSnapshotId = string.Empty;
    }

    private static List<MapParent> FindPlayerHomeSettlements()
    {
        return Find.WorldObjects?.AllWorldObjects
            .OfType<MapParent>()
            .Where(parent => parent.Faction == Faction.OfPlayer)
            .Where(parent => parent.HasMap && parent.Map?.IsPlayerHome == true)
            .GroupBy(parent => parent.GetUniqueLoadID(), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList()
            ?? new List<MapParent>();
    }

}
