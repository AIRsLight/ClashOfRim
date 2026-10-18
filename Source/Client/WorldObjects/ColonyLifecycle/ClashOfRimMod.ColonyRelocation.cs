using System;
using static AIRsLight.ClashOfRim.ClientWorldRuntimeValues;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
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
    private bool implicitColonyRelocationFlightPending;
    private string implicitColonyRelocationPreviousSnapshotId = string.Empty;

    private void ShowColonyRelocationFailure(string message, Action retry)
    {
        EnqueueClashOfRimMainThreadAction(() =>
            ShowUnconfirmedSnapshotFailure(
                LocalMutationKind.ColonyRelocation,
                message,
                retry));
    }

    internal Command? TryBuildColonyRelocationCommand(Caravan caravan, Command original)
    {
        if (caravan.Faction != Faction.OfPlayer || !caravan.IsPlayerControlled)
        {
            return null;
        }

        return TryBuildColonyRelocationCommand(caravan, original, () => SettleInEmptyTileUtility.Settle(caravan));
    }

    internal Command? TryBuildColonyRelocationCommand(Map map, Command original, bool requiresNoEnemies)
    {
        if (map.IsPlayerHome || !Find.Maps.Contains(map))
        {
            return null;
        }

        // Vanilla stops validation at the base-count limit. Preserve the checks that
        // follow it when replacing that limit with the one-colony relocation flow.
        if (map.mapPawns.FreeColonistsCount == 0)
        {
            original.Disable("CommandSettleFailNoColonists".Translate());
        }
        else if (requiresNoEnemies && map.attackTargetsCache.TargetsHostileToColony.Any(t => GenHostility.IsActiveThreatToPlayer(t)))
        {
            original.Disable("CommandSettleFailEnemies".Translate());
        }

        return TryBuildColonyRelocationCommand(map.Parent, original, () => SettleInExistingMapUtility.Settle(map));
    }

    private Command? TryBuildColonyRelocationCommand(WorldObject target, Command original, Action settle)
    {
        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(lastSessionId))
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
            action = () => OpenColonyRelocationConfirmation(target, colonies[0], settle)
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

        if (localMutations.IsPending)
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

    private void OpenColonyRelocationConfirmation(WorldObject target, MapParent oldColony, Action settle)
    {
        if (target.Destroyed || oldColony.Destroyed)
        {
            Messages.Message(ClashOfRimText.Key("ClashOfRim.ColonyRelocation.InvalidTarget"), MessageTypeDefOf.RejectInput, false);
            return;
        }

        Find.WindowStack.Add(new ColonyRelocationConfirmationWindow(
            oldColony.LabelCap,
            target.Tile,
            () => StartColonyRelocationPreflight(target, oldColony, settle)));
    }

    private void StartColonyRelocationPreflight(WorldObject target, MapParent oldColony, Action settle)
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

        int targetTile = target.Tile;
        int targetTileLayerId = ReadFirstTileLayerId(target, "Tile", "tile");
        string idempotencyKey = $"colony-relocation:{settings.UserId}:{settings.ColonyId}:{previousSnapshotId}:{targetTile},{targetTileLayerId}:{DateTime.UtcNow.Ticks}";
        BeginLocalAtomicMutation(
            LocalMutationKind.ColonyRelocation,
            ClashOfRimText.Key("ClashOfRim.ColonyRelocation.StatusPreflight"));

        ClientSessionWork.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(httpClient, ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModColonyRelocationResponseDto> result =
                    await ClientSessionWork.Observe(client.PreflightColonyRelocationAsync(targetTile, idempotencyKey, targetTileLayerId));
                using var publication = ClientSessionWork.EnterPublication();
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

                EnqueueClashOfRimMainThreadAction(() => BeginLocalColonyRelocation(target, oldColony, previousSnapshotId, targetTileLayerId, idempotencyKey, settle));
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
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
        WorldObject target,
        MapParent oldColony,
        string previousSnapshotId,
        int targetTileLayerId,
        string idempotencyKey,
        Action settle)
    {
        if (target.Destroyed || oldColony.Destroyed)
        {
            ClearLocalAtomicMutation();
            Messages.Message(ClashOfRimText.Key("ClashOfRim.ColonyRelocation.InvalidTarget"), MessageTypeDefOf.RejectInput, false);
            return;
        }

        localMutations.UpdateStatus(ClashOfRimText.Key("ClashOfRim.ColonyRelocation.StatusSettling"));
        playerColonySiteRegistrationSuppressed = true;
        int targetTile = target.Tile;
        try
        {
            SoundDefOf.Tick_High.PlayOneShotOnCamera(null);
            settle();
            LongEventHandler.QueueLongEvent(
                () => FinishLocalColonyRelocation(oldColony, previousSnapshotId, targetTile, targetTileLayerId, idempotencyKey),
                "ClashOfRim.ColonyRelocation.Generating".Translate(),
                false,
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
                () => BeginLocalColonyRelocation(target, oldColony, previousSnapshotId, targetTileLayerId, idempotencyKey, settle));
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
            localMutations.UpdateStatus(ClashOfRimText.Key("ClashOfRim.ColonyRelocation.StatusAbandoningOldColony"));
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
        if (!TryBeginSnapshotUploadTransaction(out var uploadTransaction))
        {
            ShowColonyRelocationFailure(
                ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy"),
                () => UploadRelocatedSnapshot(previousSnapshotId, targetTile, targetTileLayerId, idempotencyKey, releaseSiteRegistrationSuppressionOnComplete));
            return;
        }

        localMutations.UpdateStatus(ClashOfRimText.Key("ClashOfRim.ColonyRelocation.StatusUploadingSnapshot"));
        ClientSessionWork.Run(async () =>
        {
            bool confirmationStarted = false;
            try
            {
                var uploadService = new ModSnapshotUploadService(settings, uploadTransaction);
                ModSnapshotUploadResult upload = await ClientSessionWork.Observe(uploadService.UploadConfiguredSnapshotAsync(
                    removeRaidBattleSessions: false,
                    confirmationOperation: ColonyRelocationConfirmationOperation,
                    snapshotUploadKind: ModSnapshotUploadKinds.ColonyRelocation));
                using var publication = ClientSessionWork.EnterPublication();
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
                        uploadTransaction), onDiscard: uploadTransaction.Dispose);
                confirmationStarted = true;
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                Log.Warning("[ClashOfRim] Colony relocation snapshot upload exception before confirm: " + ex);
                ShowColonyRelocationFailure(
                    $"{ex.GetType().Name} {ex.Message}",
                    () => UploadRelocatedSnapshot(previousSnapshotId, targetTile, targetTileLayerId, idempotencyKey, releaseSiteRegistrationSuppressionOnComplete));
            }
            finally
            {
                ClientSessionWork.TryApply(() =>
                {
                    if (!confirmationStarted)
                    {
                        uploadTransaction.Dispose();
                    }
                });
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
        ClientSynchronizationCoordinator.Lease? uploadTransaction = null)
    {
        localMutations.UpdateStatus(ClashOfRimText.Key("ClashOfRim.ColonyRelocation.StatusConfirming"));
        ClientSessionWork.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(httpClient, ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModColonyRelocationResponseDto> result =
                    await ClientSessionWork.Observe(client.ConfirmColonyRelocationAsync(
                        previousSnapshotId,
                        relocatedSnapshotId,
                        targetTile,
                        idempotencyKey,
                        targetTileLayerId));
                using var publication = ClientSessionWork.EnterPublication();
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

                    occupiedColonySites.Replace(result.Response.WorldConfiguration);
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
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                Log.Warning("[ClashOfRim] Colony relocation confirm exception: " + ex);
                ShowColonyRelocationFailure(
                    $"{ex.GetType().Name} {ex.Message}",
                    () => ConfirmRelocatedSnapshot(previousSnapshotId, relocatedSnapshotId, targetTile, targetTileLayerId, idempotencyKey, releaseSiteRegistrationSuppressionOnComplete));
            }
            finally
            {
                uploadTransaction?.Dispose();
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

        LocalMutationKind operation = LocalMutationKind.ColonyRelocation;
        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage, operation))
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

        if (!localMutations.IsPending)
        {
            BeginLocalAtomicMutation(
                operation,
                ClashOfRimText.Key("ClashOfRim.ColonyRelocation.StatusFlight"));
        }
        else
        {
            localMutations.UpdateStatus(ClashOfRimText.Key("ClashOfRim.ColonyRelocation.StatusFlight"));
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

        LocalMutationKind operation = LocalMutationKind.ColonyRelocation;
        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage, operation))
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
        if (!localMutations.IsPending)
        {
            BeginLocalAtomicMutation(
                operation,
                ClashOfRimText.Key("ClashOfRim.ColonyRelocation.StatusPreflight"));
        }
        else
        {
            localMutations.UpdateStatus(ClashOfRimText.Key("ClashOfRim.ColonyRelocation.StatusPreflight"));
        }
        playerColonySiteRegistrationSuppressed = true;
        lastRegisteredPlayerColonySiteSignature = null;
        ClashLog.Message("[ClashOfRim] Starting implicit colony relocation confirmation: " + reason + ", targetTile=" + targetTile + "," + targetTileLayerId);

        ClientSessionWork.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(httpClient, ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModColonyRelocationResponseDto> result =
                    await ClientSessionWork.Observe(client.PreflightColonyRelocationAsync(targetTile, idempotencyKey, targetTileLayerId));
                using var publication = ClientSessionWork.EnterPublication();
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
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
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
