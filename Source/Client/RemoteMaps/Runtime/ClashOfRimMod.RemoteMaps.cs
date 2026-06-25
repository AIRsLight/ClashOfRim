using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.Support;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    private static readonly RemoteSnapshotDownloadFailureKeys ObservationDownloadFailureKeys = new(
        "ClashOfRim.Observation.StatusDownloadFailed",
        "ClashOfRim.Observation.StatusMissingSnapshot",
        "ClashOfRim.Observation.StatusPayloadFailed");

    private static readonly RemoteSnapshotDownloadFailureKeys RaidBattleDownloadFailureKeys = new(
        "ClashOfRim.Raid.StatusBattleDownloadFailed",
        "ClashOfRim.Raid.StatusBattleMissingSnapshot",
        "ClashOfRim.Raid.StatusBattlePayloadFailed");

    private const int RaidAttackerSnapshotRetryDelayMs = 250;
    private const int RaidAttackerSnapshotWaitLogInterval = 20;

    private static void QueueRemoteMapLongEvent(string textKey, Action action)
    {
        CloseServerEntryProgressWindowNow();
        LongEventHandler.QueueLongEvent(
            action,
            textKey,
            doAsynchronously: false,
            exceptionHandler: ex => Log.Warning("[ClashOfRim] Remote map long event failed: " + ex),
            showExtraUIInfo: false);
    }

    private static void SetRemoteMapLongEventText(string textKey)
    {
        LongEventHandler.SetCurrentEventText(ClashOfRimText.Key(textKey));
    }

    private static void ShowRemoteMapLoadingProgress(string status)
    {
        ShowServerEntryProgressWindow(
            ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.LoadingTitle"),
            status,
            -1f,
            canClose: false);
    }

    private void ReportRemoteMapDownloadFailureFromWorker(string message)
    {
        ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
        {
            worldMapStatus = message;
            CloseServerEntryProgressWindowNow();
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
        });
    }

    internal void OpenCaravanRaidMenu(Caravan caravan, IReadOnlyList<ModWorldMapMarkerDto> targets)
    {
        if (!pvpEnabled)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.PvpDisabled");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (targets.Count == 0)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusNoTargets");
            return;
        }

        List<Pawn> pawns = caravan.PawnsListForReading
            .Where(pawn => pawn != null && !pawn.Dead && caravan.IsOwner(pawn))
            .ToList();
        if (pawns.Count == 0)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusNoPawns");
            return;
        }

        var options = new List<FloatMenuOption>();
        foreach (ModWorldMapMarkerDto target in targets)
        {
            ModWorldMapMarkerDto captured = target;
            options.Add(new FloatMenuOption(
                ClashOfRimText.Key(
                    "ClashOfRim.Raid.CaravanTargetOption",
                    FormatSupportTargetLabel(captured).Named("TARGET"),
                    pawns.Count.Named("COUNT")),
                () =>
                {
                    string confirmation = ClashOfRimText.Key(
                        "ClashOfRim.Raid.ConfirmStart",
                        FormatSupportTargetLabel(captured).Named("TARGET"),
                        pawns.Count.Named("COUNT"));
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        confirmation,
                        () => StartCreateRaidFromCaravan(caravan, captured)));
                }));
        }

        Find.WindowStack.Add(new FloatMenu(options));
    }

    public void StartRaidFromVehicleLanding(Caravan caravan, ModWorldMapMarkerDto target)
    {
        if (!pvpEnabled)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.PvpDisabled");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (caravan is null || caravan.Destroyed)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.VehicleFramework.RemoteLanding.RaidStartFailedNoCaravan");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (target is null || !target.CanRaid)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.VehicleFramework.RemoteLanding.RaidUnavailable");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        StartCreateRaidFromCaravan(caravan, target);
    }

    private void StartCreateRaidFromCaravan(Caravan caravan, ModWorldMapMarkerDto target)
    {
        StartCreateRaid(new CaravanRaidAttackForceSource(caravan), target);
    }

    internal void StartCreateRaidFromTransporters(
        string transporterKey,
        string targetUserId,
        string targetColonyId,
        string targetSnapshotId,
        string targetMapId,
        string targetWorldObjectId,
        PlanetTile targetTile,
        int targetTileLayerId,
        string targetLabel,
        List<ActiveTransporterInfo> transporters)
    {
        var target = new ModWorldMapMarkerDto
        {
            MarkerId = string.Empty,
            Kind = "TradeableColony",
            OwnerUserId = targetUserId ?? string.Empty,
            OwnerColonyId = targetColonyId ?? string.Empty,
            SnapshotId = targetSnapshotId ?? string.Empty,
            MapId = targetMapId ?? string.Empty,
            WorldObjectId = targetWorldObjectId ?? string.Empty,
            Tile = targetTile,
            TileLayerId = Math.Max(0, targetTileLayerId),
            Label = targetLabel ?? string.Empty,
            CanRaid = true
        };
        StartCreateRaid(new TransporterRaidAttackForceSource(
            transporters,
            target.Tile,
            transporterKey,
            settings.UserId,
            settings.ColonyId,
            settings.CurrentSnapshotId),
            target);
    }

    private void StartCreateRaid(IRaidAttackForceSource attackSource, ModWorldMapMarkerDto target)
    {
        if (!pvpEnabled)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.PvpDisabled");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            worldMapStatus = atomicMessage;
            Messages.Message(atomicMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            worldMapStatus = failureReason;
            return;
        }

        if (target.Tile != attackSource.Tile)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusTargetTileMismatch");
            return;
        }

        if (string.IsNullOrWhiteSpace(target.OwnerUserId)
            || string.IsNullOrWhiteSpace(target.OwnerColonyId)
            || string.IsNullOrWhiteSpace(target.MapId)
            || string.IsNullOrWhiteSpace(target.WorldObjectId))
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusTargetIncomplete");
            return;
        }

        IReadOnlyList<Pawn> pawns = attackSource.AttackPawns;
        if (pawns.Count == 0)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusNoPawns");
            return;
        }

        IReadOnlyList<RaidAttackPawnLoad> attackPawns = pawns
            .Select(pawn =>
            {
                string globalKey = PawnGlobalIdUtility.Build(settings.UserId, pawn);
                return new RaidAttackPawnLoad(globalKey, pawn);
            })
            .ToList();
        IReadOnlyList<string> pawnGlobalKeys = attackPawns.Select(pawn => pawn.GlobalKey).ToList();
        IReadOnlyList<ModThingReferenceDto> carriedThings = attackSource.BuildCarriedThings(
            settings.UserId,
            settings.ColonyId,
            settings.CurrentSnapshotId);
        string idempotencyKey = $"raid-start:{settings.UserId}:{settings.CurrentSnapshotId}:{attackSource.UniqueLoadId}:{target.OwnerUserId}:{target.OwnerColonyId}:{DateTime.UtcNow.Ticks}";
        manualSyncInProgress = true;
        worldMapStatus = ClashOfRimText.Key(
            "ClashOfRim.Raid.StatusBattleDownloading",
            FormatSupportTargetLabel(target).Named("TARGET"));
        ShowRemoteMapLoadingProgress(worldMapStatus);
        bool dispatchedToMain = false;
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(60)
                };
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));

                ClashOfRimClientNetworkResult<ModPrepareRaidResponseDto> prepare =
                    await client.PrepareRaidAsync(
                        idempotencyKey,
                        target.OwnerUserId ?? string.Empty,
                        target.OwnerColonyId ?? string.Empty,
                        target.WorldObjectId ?? string.Empty,
                        target.MapId ?? string.Empty,
                        target.Tile,
                        isHostile: true);

                if (!prepare.Success || prepare.Response is null)
                {
                    ReportRemoteMapDownloadFailureFromWorker(ClashOfRimText.Key(
                        "ClashOfRim.Raid.StatusCreateFailed",
                        (prepare.ErrorCode ?? string.Empty).Named("CODE"),
                        (prepare.Message ?? string.Empty).Named("MESSAGE")));
                    return;
                }

                ModProtocolResponseDto? prepareResult = prepare.Response.Result;
                if (prepareResult is not null && !prepareResult.Accepted)
                {
                    ReportRemoteMapDownloadFailureFromWorker(ClashOfRimText.Key(
                        "ClashOfRim.Raid.StatusCreateRejected",
                        prepareResult.ErrorCode.Named("CODE"),
                        (prepareResult.Message ?? string.Empty).Named("MESSAGE")));
                    return;
                }

                string raidEventId = string.IsNullOrWhiteSpace(prepare.Response.RaidEventId)
                    ? BuildLocalRaidEventId(idempotencyKey)
                    : prepare.Response.RaidEventId!;
                if (string.IsNullOrWhiteSpace(prepare.Response.RaidPreparationId))
                {
                    ReportRemoteMapDownloadFailureFromWorker(ClashOfRimText.Key(
                        "ClashOfRim.Raid.StatusCreateRejected",
                        "MissingPreparation".Named("CODE"),
                        ClashOfRimText.Key("ClashOfRim.Raid.StatusMissingPreparation").Named("MESSAGE")));
                    return;
                }

                string raidPreparationId = prepare.Response.RaidPreparationId!;
                TimeSpan battleDuration = RaidDurationFromServerMinutes(
                    prepare.Response.RaidMaxDurationMinutes,
                    RaidBattleDuration);
                TimeSpan settlementGraceDuration = RaidDurationFromServerMinutes(
                    prepare.Response.RaidTimeoutGraceMinutes,
                    RaidSettlementGraceDuration);
                RemoteSnapshotDownloadResult snapshot =
                    await new RemoteSnapshotDownloadService(client).DownloadAsync(new RemoteSnapshotDownloadRequest
                    {
                        UserId = target.OwnerUserId ?? string.Empty,
                        ColonyId = target.OwnerColonyId ?? string.Empty,
                        AuthorizationEventId = raidPreparationId,
                        AuthorizationScope = RemoteSessionMapParent.RaidPreparationDownloadMode
                    });

                dispatchedToMain = true;
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    FinishEnterRaidBattle(
                        idempotencyKey,
                        raidEventId,
                        raidPreparationId,
                        target,
                        settings.UserId,
                        settings.ColonyId,
                        settings.CurrentSnapshotId,
                        snapshot.Metadata,
                        snapshot.Payload,
                        attackSource,
                        attackPawns,
                        pawnGlobalKeys,
                        carriedThings,
                        battleDuration,
                        settlementGraceDuration,
                        prepare.Response.GuardDeployment));
            }
            catch (Exception ex)
            {
                ReportRemoteMapDownloadFailureFromWorker(ClashOfRimText.Key(
                    "ClashOfRim.Raid.StatusCreateException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE")));
                Log.Warning("[ClashOfRim] Raid creation failed: " + ex);
            }
            finally
            {
                if (!dispatchedToMain)
                {
                    manualSyncInProgress = false;
                }
            }
        });
    }

    internal void StartObserveRemoteMarker(ModWorldMapMarkerDto target, string mode)
    {
        StartDownloadObservationSnapshot(target, mode);
    }

    private void StartDownloadObservationSnapshot(ModWorldMapMarkerDto target, string mode)
    {
        if (!settings.IsConfigured)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Observation.StatusNotConfigured");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (manualSyncInProgress)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Observation.StatusBusy");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (string.IsNullOrWhiteSpace(target.OwnerUserId)
            || string.IsNullOrWhiteSpace(target.OwnerColonyId))
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Observation.StatusTargetIncomplete");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        string scope = RemoteMapSessionPolicy.NormalizeMode(mode);
        string? authorizationEventId = string.Equals(scope, RemoteSessionMapParent.RaidObservationMode, StringComparison.OrdinalIgnoreCase)
            ? target.RelatedEventId
            : null;
        if (string.Equals(scope, RemoteSessionMapParent.RaidObservationMode, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(authorizationEventId))
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Observation.StatusMissingRaidEvent");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        manualSyncInProgress = true;
        worldMapStatus = ClashOfRimText.Key(
            RemoteMapSessionPolicy.ObservationDownloadingStatusKey(scope),
            FormatSupportTargetLabel(target).Named("TARGET"));
        ShowRemoteMapLoadingProgress(worldMapStatus);

        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(60)
                };
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                if (string.IsNullOrWhiteSpace(settings.AuthToken))
                {
                    ClashOfRimClientNetworkResult<ModLoginResponseDto> login = await client.LoginAsync("observation");
                    if (!login.Success || login.Response is null || login.Response.Result?.Accepted != true)
                    {
                        string message = login.Response?.Result?.Message ?? login.Message ?? ClashOfRimText.Key("ClashOfRim.ServerEntry.StatusLoginFallback");
                        ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                        {
                            CloseServerEntryProgressWindowNow();
                            ShowCompatibilityMismatchWindow(login.Response);
                            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Observation.StatusLoginFailed", message.Named("MESSAGE"));
                            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
                        });
                        return;
                    }

                    settings.AuthToken = login.Response.AuthToken ?? string.Empty;
                    settings.Write();
                    lastSessionId = login.Response.SessionId;
                    sessionExpiredHandling = false;
                    client = new ClashOfRimModNetworkClient(
                        httpClient,
                        ClashOfRimClientNetworkContext.FromSettings(settings));
                }

                RemoteSnapshotDownloadResult snapshot =
                    await new RemoteSnapshotDownloadService(client).DownloadAsync(new RemoteSnapshotDownloadRequest
                    {
                        UserId = target.OwnerUserId,
                        ColonyId = target.OwnerColonyId ?? string.Empty,
                        AuthorizationEventId = authorizationEventId,
                        AuthorizationScope = scope
                    });

                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    FinishDownloadObservationSnapshot(target, scope, snapshot.Metadata, snapshot.Payload));
            }
            catch (Exception ex)
            {
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    CloseServerEntryProgressWindowNow();
                    worldMapStatus = ClashOfRimText.Key(
                        "ClashOfRim.Observation.StatusException",
                        ex.GetType().Name.Named("TYPE"),
                        ex.Message.Named("MESSAGE"));
                    Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
                });
                Log.Warning("[ClashOfRim] Observation snapshot download failed: " + ex);
            }
            finally
            {
                manualSyncInProgress = false;
            }
        });
    }

    private void FinishDownloadObservationSnapshot(
        ModWorldMapMarkerDto target,
        string scope,
        ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto> metadata,
        ClashOfRimClientNetworkResult<byte[]>? payload)
    {
        if (!RemoteSnapshotDownloadValidator.TryGetOpenPayload(
                metadata,
                payload,
                ObservationDownloadFailureKeys,
                out RemoteSnapshotOpenPayload? openPayload,
                out string downloadFailureReason)
            || openPayload is null)
        {
            worldMapStatus = downloadFailureReason;
            CloseServerEntryProgressWindowNow();
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        string sessionId = $"observation:{settings.UserId}:{target.OwnerUserId}:{openPayload.SnapshotId}:{DateTime.UtcNow.Ticks}";
        ClashLog.Message(
            "[ClashOfRim][Observation] Opening "
            + scope
            + " snapshot target="
            + target.OwnerUserId
            + "/"
            + target.OwnerColonyId
            + ", snapshot="
            + openPayload.SnapshotId);
        var openRequest = new RemoteSessionMapOpenRequest
        {
            Target = target,
            Mode = scope,
            SessionId = sessionId,
            SnapshotId = openPayload.SnapshotId,
            RelatedEventId = target.RelatedEventId ?? string.Empty,
            Package = openPayload.Package,
            Payload = openPayload.Payload,
            CloseExistingObservationSessions = true,
            FailureCleanupReason = "remote session map generation failure"
        };
        worldMapStatus = ClashOfRimText.Key(
            "ClashOfRim.RemoteSessionMap.StatusGenerating",
            FormatSupportTargetLabel(target).Named("TARGET"));
        ShowServerEntryProgressWindowNow(
            ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.LoadingTitle"),
            worldMapStatus,
            -1f,
            canClose: false);
        QueueRemoteMapLongEvent("ClashOfRim.RemoteSessionMap.StatusGeneratingLong", () =>
            OpenDownloadedObservationSnapshot(openRequest, target, scope));
    }

    private void OpenDownloadedObservationSnapshot(
        RemoteSessionMapOpenRequest openRequest,
        ModWorldMapMarkerDto target,
        string scope)
    {
        try
        {
            if (!RemoteSessionMapUtility.TryOpen(openRequest, out RemoteSessionMapOpenResult openResult))
            {
                CloseServerEntryProgressWindowNow();
                worldMapStatus = openResult.FailureReason;
                Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            RemoteMapSessionController.RegisterOpenedObservation(openRequest, openResult);
            CloseServerEntryProgressWindowNow();
            Messages.Message(
                ClashOfRimText.Key(
                    RemoteMapSessionPolicy.ObservationLoadedMessageKey(scope),
                    FormatSupportTargetLabel(target).Named("TARGET")),
                MessageTypeDefOf.NeutralEvent,
                historical: false);
        }
        catch (Exception ex)
        {
            CloseServerEntryProgressWindowNow();
            worldMapStatus = ClashOfRimText.Key(
                "ClashOfRim.RemoteSessionMap.StatusGenerateException",
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE"));
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            Log.Warning("[ClashOfRim] Remote observation map open failed: " + ex);
        }
    }

    private void FinishEnterRaidBattle(
        string raidIdempotencyKey,
        string raidEventId,
        string raidPreparationId,
        ModWorldMapMarkerDto target,
        string attackerUserId,
        string attackerColonyId,
        string attackerSnapshotId,
        ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto> metadata,
        ClashOfRimClientNetworkResult<byte[]>? payload,
        IRaidAttackForceSource attackSource,
        IReadOnlyList<RaidAttackPawnLoad> attackPawns,
        IReadOnlyList<string> pawnGlobalKeys,
        IReadOnlyList<ModThingReferenceDto> carriedThings,
        TimeSpan battleDuration,
        TimeSpan settlementGraceDuration,
        ModRaidGuardDeploymentDto? guardDeployment)
    {
        bool submitted = false;
        try
        {
            if (!RemoteSnapshotDownloadValidator.TryGetOpenPayload(
                    metadata,
                    payload,
                    RaidBattleDownloadFailureKeys,
                    out RemoteSnapshotOpenPayload? openPayload,
                    out string downloadFailureReason)
                || openPayload is null)
            {
                worldMapStatus = downloadFailureReason;
                CloseServerEntryProgressWindowNow();
                Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            string sessionId = $"raidbattle:{attackerUserId}:{raidEventId}:{DateTime.UtcNow.Ticks}";
            var openRequest = new RemoteSessionMapOpenRequest
            {
                Target = target,
                Mode = RemoteSessionMapParent.RaidBattleMode,
                SessionId = sessionId,
                SnapshotId = openPayload.SnapshotId,
                RelatedEventId = raidEventId,
                Package = openPayload.Package,
                Payload = openPayload.Payload,
                CloseExistingObservationSessions = true,
                FailureCleanupReason = "raid battle map generation failure"
            };
            submitted = true;
            worldMapStatus = ClashOfRimText.Key(
                "ClashOfRim.RemoteSessionMap.StatusGenerating",
                FormatSupportTargetLabel(target).Named("TARGET"));
            ShowServerEntryProgressWindowNow(
                ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.LoadingTitle"),
                worldMapStatus,
                -1f,
                canClose: false);
            QueueRemoteMapLongEvent("ClashOfRim.RemoteSessionMap.StatusGeneratingLong", () =>
                OpenPreparedRaidBattle(
                    raidIdempotencyKey,
                    raidEventId,
                    raidPreparationId,
                    target,
                    attackerUserId,
                    attackerColonyId,
                    attackerSnapshotId,
                    openRequest,
                    openPayload.Package.NextLineageToken ?? string.Empty,
                    attackSource,
                    attackPawns,
                    pawnGlobalKeys,
                    carriedThings,
                    battleDuration,
                    settlementGraceDuration,
                    guardDeployment));
        }
        finally
        {
            if (!submitted)
            {
                manualSyncInProgress = false;
            }
        }
    }

    private void OpenPreparedRaidBattle(
        string raidIdempotencyKey,
        string raidEventId,
        string raidPreparationId,
        ModWorldMapMarkerDto target,
        string attackerUserId,
        string attackerColonyId,
        string attackerSnapshotId,
        RemoteSessionMapOpenRequest openRequest,
        string defenderLineageToken,
        IRaidAttackForceSource attackSource,
        IReadOnlyList<RaidAttackPawnLoad> attackPawns,
        IReadOnlyList<string> pawnGlobalKeys,
        IReadOnlyList<ModThingReferenceDto> carriedThings,
        TimeSpan battleDuration,
        TimeSpan settlementGraceDuration,
        ModRaidGuardDeploymentDto? guardDeployment)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationRaid");
        bool atomicStarted = false;
        bool submitted = false;
        try
        {
            if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
            {
                CloseServerEntryProgressWindowNow();
                worldMapStatus = blockedMessage;
                Log.Warning("[ClashOfRim][Raid] Refusing to open prepared raid battle while another local atomic mutation is pending: event="
                    + raidEventId
                    + ", target="
                    + (target.WorldObjectId ?? string.Empty)
                    + ", blocked="
                    + blockedMessage
                    + ".");
                Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Raid.StatusPrepareSnapshot"));
            atomicStarted = true;
            ClashLog.Message("[ClashOfRim][Raid] Opening prepared raid battle under local atomic mutation: event="
                + raidEventId
                + ", target="
                + (target.WorldObjectId ?? string.Empty)
                + ", attackerSnapshot="
                + attackerSnapshotId
                + ".");

            if (!RemoteSessionMapUtility.TryOpen(openRequest, out RemoteSessionMapOpenResult openResult))
            {
                CloseServerEntryProgressWindowNow();
                worldMapStatus = openResult.FailureReason;
                Log.Warning("[ClashOfRim][Raid] Prepared raid battle map open failed before event creation: event="
                    + raidEventId
                    + ", reason="
                    + worldMapStatus);
                Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            ActiveRaidBattleSession session = RemoteMapSessionController.CreateRaidBattleSession(
                raidEventId,
                attackerUserId,
                attackerColonyId,
                attackerSnapshotId,
                openRequest,
                openResult,
                defenderLineageToken,
                battleDuration,
                settlementGraceDuration,
                guardDeployment);

            if (!PrepareRaidBattlefield(session, attackSource, attackPawns))
            {
                CloseServerEntryProgressWindowNow();
                RemoteMapSessionController.CloseRaidBattleForExternalResolution(
                    session.EventId,
                    "raid attack pawn transfer failed");
                return;
            }

            CloseServerEntryProgressWindowNow();
            ClashLog.Message("[ClashOfRim][Raid] Prepared raid battle map opened and attackers moved; submitting authoritative start snapshot: event="
                + session.EventId
                + ", clientMap="
                + session.ClientMapId
                + ", attackerPawns="
                + session.AttackPawnThingIds.Count
                + ".");
            submitted = true;
            StartSubmitPreparedRaid(
                raidIdempotencyKey,
                raidPreparationId,
                session,
                target,
                pawnGlobalKeys,
                carriedThings);
        }
        catch (Exception ex)
        {
            CloseServerEntryProgressWindowNow();
            worldMapStatus = ClashOfRimText.Key(
                "ClashOfRim.RemoteSessionMap.StatusGenerateException",
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE"));
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            Log.Warning("[ClashOfRim] Prepared raid battle map open failed: " + ex);
        }
        finally
        {
            if (!submitted)
            {
                manualSyncInProgress = false;
                if (atomicStarted)
                {
                    ClearLocalAtomicMutation();
                }
            }
        }
    }

    private bool PrepareRaidBattlefield(
        ActiveRaidBattleSession session,
        IRaidAttackForceSource attackSource,
        IReadOnlyList<RaidAttackPawnLoad> attackPawns)
    {
        string runtimeMapId = string.IsNullOrWhiteSpace(session.ClientMapId)
            ? session.TargetMapId
            : session.ClientMapId;
        Map? map = Find.Maps?.FirstOrDefault(candidate =>
            MapIdsMatch($"Map_{candidate.uniqueID}", runtimeMapId))
            ?? Find.CurrentMap;
        if (map is null)
        {
            Log.Warning("[ClashOfRim][Raid] Cannot prepare battlefield because no map is loaded.");
            return false;
        }

        Faction? defenderFaction = PlayerFactionProxyUtility.EnsureProxyForUser(session.DefenderUserId);
        if (defenderFaction is not null)
        {
            defenderFaction.SetRelationDirect(Faction.OfPlayer, FactionRelationKind.Hostile, canSendHostilityLetter: false);
            Faction.OfPlayer?.SetRelationDirect(defenderFaction, FactionRelationKind.Hostile, canSendHostilityLetter: false);
            RemoteSessionMapAreaSanitizer.Apply(map);
            RemoteSessionMapConstructionSanitizer.Apply(map);
            RemoteSessionPawnStateSanitizerResult pawnStateResult = RemoteSessionPawnStateSanitizer.DropLoadedCarriedThings(map);
            RemoteSessionMapThingAccessSanitizer.ForbidLoadedHaulables(map);
            RaidDefenderMapUtility.ConvertPlayerOwnedMapObjectsToDefenderProxy(map, defenderFaction);
            RaidDefenderLootProtectionUtility.Apply(map, defenderFaction);
            DefensePointRaidAiApplicator.Apply(map, defenderFaction);
            ClashOfRimCompatibilityApi.NotifyRemoteDefenderMapPrepared(map, defenderFaction);
            RaidGuardDeploymentUtility.DeployIfNeeded(map, defenderFaction, session);
            ClashLog.Message("[ClashOfRim][Raid] Prepared defender map pawn state: carriedThingsDropped="
                + pawnStateResult.DroppedThings
                + ", carriedThingJobsInterrupted="
                + pawnStateResult.InterruptedJobs
                + ", carriedThingsDestroyed="
                + pawnStateResult.DestroyedThings
                + ".");
        }

        foreach (RaidAttackPawnLoad load in attackPawns)
        {
            Pawn pawn = load.Pawn;
            if (pawn is null || pawn.Dead || pawn.Destroyed)
            {
                Log.Warning("[ClashOfRim][Raid] Cannot move attack force because pawn is unavailable: " + load.GlobalKey);
                return false;
            }
        }

        RaidCaravanMapEntryResult compatibilityEntry = attackSource.TryEnterMapViaCompatibility(
            map,
            attackPawns.Select(load => load.Pawn).ToList());
        if (compatibilityEntry.Kind == RaidCaravanMapEntryResultKind.Failed)
        {
            Log.Warning("[ClashOfRim][Raid] Compatibility handler failed to move attack caravan: "
                + compatibilityEntry.FailureReason);
            return false;
        }

        if (compatibilityEntry.Kind == RaidCaravanMapEntryResultKind.Success)
        {
            session.AttackPawnThingIds.AddRange(compatibilityEntry.AttackPawnThingIds);
            attackSource.CleanupAfterSuccessfulEntry();
            ClashOfRimGameComponent.SetActiveRaidBattleSession(session);
            return true;
        }

        if (!attackSource.TryPlaceAttackersOnMap(session, map, attackPawns, out string placementFailureReason))
        {
            Log.Warning("[ClashOfRim][Raid] Failed to move attack force: " + placementFailureReason);
            return false;
        }

        attackSource.CleanupAfterSuccessfulEntry();
        ClashOfRimGameComponent.SetActiveRaidBattleSession(session);
        return true;
    }

    private static void RemoveEmptyRaidSourceCaravan(Caravan sourceCaravan)
    {
        if (sourceCaravan is null || sourceCaravan.Destroyed)
        {
            return;
        }

        if (sourceCaravan.PawnsListForReading?.Count > 0)
        {
            return;
        }

        if (Find.WorldObjects?.Contains(sourceCaravan) == true)
        {
            Find.WorldObjects.Remove(sourceCaravan);
            ClashLog.Message("[ClashOfRim][Raid] Removed empty source caravan after moving all attackers into raid battle.");
        }
    }

    private static bool TryMoveRaidAttackerPawnToMap(
        Caravan sourceCaravan,
        RaidAttackPawnLoad load,
        Map map,
        IntVec3 entryCell,
        int entryRadius,
        out string pawnThingId,
        out string failureReason)
    {
        pawnThingId = string.Empty;
        failureReason = string.Empty;
        Pawn pawn = load.Pawn;
        if (pawn is null || pawn.Dead || pawn.Destroyed)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerPawnUnavailable");
            return false;
        }

        if (!sourceCaravan.ContainsPawn(pawn))
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerPawnNotInCaravan");
            return false;
        }

        sourceCaravan.RemovePawn(pawn);
        if (pawn.Spawned)
        {
            pawn.DeSpawn(DestroyMode.Vanish);
        }

        IntVec3 cell = PawnExchangePlacementService.SpawnNearMapEdge(pawn, map, entryCell, entryRadius);
        if (pawn.Faction != Faction.OfPlayer)
        {
            pawn.SetFaction(Faction.OfPlayer);
        }

        pawnThingId = pawn.ThingID;
        return true;
    }

    private interface IRaidAttackForceSource
    {
        int Tile { get; }

        string UniqueLoadId { get; }

        IReadOnlyList<Pawn> AttackPawns { get; }

        IReadOnlyList<ModThingReferenceDto> BuildCarriedThings(string userId, string colonyId, string snapshotId);

        bool ContainsPawn(Pawn pawn);

        RaidCaravanMapEntryResult TryEnterMapViaCompatibility(Map map, IReadOnlyList<Pawn> pawns);

        bool TryPlaceAttackersOnMap(
            ActiveRaidBattleSession session,
            Map map,
            IReadOnlyList<RaidAttackPawnLoad> attackPawns,
            out string failureReason);

        void CleanupAfterSuccessfulEntry();
    }

    private sealed class CaravanRaidAttackForceSource : IRaidAttackForceSource
    {
        private readonly Caravan caravan;

        public CaravanRaidAttackForceSource(Caravan caravan)
        {
            this.caravan = caravan;
        }

        public int Tile => caravan.Tile;

        public string UniqueLoadId => caravan.GetUniqueLoadID();

        public IReadOnlyList<Pawn> AttackPawns => caravan.PawnsListForReading
            .Where(pawn => pawn != null && !pawn.Dead && caravan.IsOwner(pawn))
            .ToList();

        public IReadOnlyList<ModThingReferenceDto> BuildCarriedThings(string userId, string colonyId, string snapshotId)
        {
            return TradeCaravanFulfillmentUtility.BuildDeliveredThingReferences(caravan, userId, colonyId, snapshotId);
        }

        public bool ContainsPawn(Pawn pawn)
        {
            return caravan.ContainsPawn(pawn);
        }

        public RaidCaravanMapEntryResult TryEnterMapViaCompatibility(Map map, IReadOnlyList<Pawn> pawns)
        {
            return ClashOfRimCompatibilityApi.TryEnterRaidCaravanMap(caravan, map, pawns);
        }

        public bool TryPlaceAttackersOnMap(
            ActiveRaidBattleSession session,
            Map map,
            IReadOnlyList<RaidAttackPawnLoad> attackPawns,
            out string failureReason)
        {
            failureReason = string.Empty;
            IntVec3 attackerEntryCell = PawnExchangePlacementService.FindMapEdgeLandingCell(map);
            int attackerEntryRadius = Math.Min(12, 4 + attackPawns.Count / 3);
            foreach (RaidAttackPawnLoad load in attackPawns)
            {
                if (TryMoveRaidAttackerPawnToMap(
                        caravan,
                        load,
                        map,
                        attackerEntryCell,
                        attackerEntryRadius,
                        out string pawnThingId,
                        out string pawnFailureReason)
                    && !string.IsNullOrWhiteSpace(pawnThingId))
                {
                    session.AttackPawnThingIds.Add(pawnThingId);
                    continue;
                }

                failureReason = ClashOfRimText.Key(
                    "ClashOfRim.Raid.StatusAttackForcePlacementFailed",
                    load.GlobalKey.Named("PAWN"),
                    pawnFailureReason.Named("REASON"));
                return false;
            }

            return true;
        }

        public void CleanupAfterSuccessfulEntry()
        {
            RemoveEmptyRaidSourceCaravan(caravan);
        }
    }

    private sealed class TransporterRaidAttackForceSource : IRaidAttackForceSource
    {
        private readonly List<ActiveTransporterInfo> transporters;
        private readonly int tile;
        private readonly string transporterKey;
        private readonly string userId;
        private readonly string colonyId;
        private readonly string snapshotId;

        public TransporterRaidAttackForceSource(
            List<ActiveTransporterInfo> transporters,
            int tile,
            string transporterKey,
            string userId,
            string colonyId,
            string snapshotId)
        {
            this.transporters = transporters is null
                ? new List<ActiveTransporterInfo>()
                : transporters.Where(transporter => transporter is not null).ToList();
            this.tile = tile;
            this.transporterKey = string.IsNullOrWhiteSpace(transporterKey)
                ? Guid.NewGuid().ToString("N")
                : transporterKey;
            this.userId = userId ?? string.Empty;
            this.colonyId = colonyId ?? string.Empty;
            this.snapshotId = snapshotId ?? string.Empty;
        }

        public int Tile => tile;

        public string UniqueLoadId => "transporters:" + transporterKey;

        public IReadOnlyList<Pawn> AttackPawns => EnumeratePayloadThings()
            .OfType<Pawn>()
            .Where(IsAttackPawn)
            .Distinct()
            .ToList();

        public IReadOnlyList<ModThingReferenceDto> BuildCarriedThings(string userId, string colonyId, string snapshotId)
        {
            var references = new List<ModThingReferenceDto>();
            foreach (Thing thing in EnumeratePayloadThings())
            {
                if (thing is Pawn || thing.Destroyed || !TradeThingReferenceUtility.IsTradeableItem(thing))
                {
                    continue;
                }

                CompBiocodable? biocodable = thing.TryGetComp<CompBiocodable>();
                references.Add(TradeThingReferenceUtility.BuildThingReference(
                    thing,
                    $"owner:{this.userId}/colony:{this.colonyId}/snapshot:{this.snapshotId}/transportPods:{transporterKey}/thing:{thing.ThingID}",
                    thing.stackCount,
                    BuildBiocodedPawnGlobalId(biocodable?.CodedPawn)));
            }

            return references;
        }

        public bool ContainsPawn(Pawn pawn)
        {
            return pawn is not null && AttackPawns.Contains(pawn);
        }

        public RaidCaravanMapEntryResult TryEnterMapViaCompatibility(Map map, IReadOnlyList<Pawn> pawns)
        {
            return RaidCaravanMapEntryResult.NotHandled;
        }

        public bool TryPlaceAttackersOnMap(
            ActiveRaidBattleSession session,
            Map map,
            IReadOnlyList<RaidAttackPawnLoad> attackPawns,
            out string failureReason)
        {
            failureReason = string.Empty;
            if (transporters.Count == 0)
            {
                failureReason = ClashOfRimText.Key("ClashOfRim.Raid.StatusTransporterUnavailable");
                return false;
            }

            IntVec3 entryCell = DropCellFinder.GetBestShuttleLandingSpot(map, Faction.OfPlayer);
            if (!entryCell.IsValid)
            {
                entryCell = PawnExchangePlacementService.FindMapEdgeLandingCell(map);
            }

            var dropPodTransporters = new List<ActiveTransporterInfo>();
            foreach (ActiveTransporterInfo transporter in transporters)
            {
                if (transporter?.innerContainer is null)
                {
                    continue;
                }

                if (transporter.GetShuttle() is not null)
                {
                    TransportersArrivalActionUtility.DropShuttle(transporter, map, entryCell, null, Faction.OfPlayer);
                    continue;
                }

                dropPodTransporters.Add(transporter);
            }

            if (dropPodTransporters.Count > 0)
            {
                TransportersArrivalActionUtility.DropTravellingDropPods(dropPodTransporters, entryCell, map);
            }

            foreach (RaidAttackPawnLoad load in attackPawns)
            {
                Pawn pawn = load.Pawn;
                if (pawn is null || pawn.Dead || pawn.Destroyed || string.IsNullOrWhiteSpace(pawn.ThingID))
                {
                    failureReason = ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerPawnUnavailable");
                    return false;
                }

                if (!session.AttackPawnThingIds.Contains(pawn.ThingID))
                {
                    session.AttackPawnThingIds.Add(pawn.ThingID);
                }
            }

            if (session.AttackPawnThingIds.Count == 0)
            {
                failureReason = ClashOfRimText.Key("ClashOfRim.Raid.StatusNoPawns");
                return false;
            }

            return true;
        }

        public void CleanupAfterSuccessfulEntry()
        {
        }

        private IEnumerable<Thing> EnumeratePayloadThings()
        {
            foreach (ActiveTransporterInfo transporter in transporters)
            {
                if (transporter?.innerContainer is null)
                {
                    continue;
                }

                Thing? shuttle = transporter.GetShuttle();
                for (int index = 0; index < transporter.innerContainer.Count; index++)
                {
                    Thing thing = transporter.innerContainer[index];
                    if (thing is null || thing.Destroyed || ReferenceEquals(thing, shuttle))
                    {
                        continue;
                    }

                    yield return thing;
                }
            }
        }

        private static bool IsAttackPawn(Pawn pawn)
        {
            return pawn is not null
                && !pawn.Dead
                && !pawn.Destroyed
                && pawn.Faction == Faction.OfPlayer
                && !pawn.IsPrisonerOfColony;
        }

        private string? BuildBiocodedPawnGlobalId(Pawn? pawn)
        {
            if (pawn is null || string.IsNullOrWhiteSpace(pawn.ThingID))
            {
                return null;
            }

            return PawnGlobalIdUtility.Build(userId, pawn);
        }
    }

    private void StartSubmitPreparedRaid(
        string idempotencyKey,
        string raidPreparationId,
        ActiveRaidBattleSession session,
        ModWorldMapMarkerDto target,
        IReadOnlyList<string> pawnGlobalKeys,
        IReadOnlyList<ModThingReferenceDto> carriedThings)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationRaid");
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            worldMapStatus = blockedMessage;
            Log.Warning("[ClashOfRim][Raid] Prepared raid start snapshot submission blocked after local battle map mutation: event="
                + session.EventId
                + ", target="
                + (target.WorldObjectId ?? string.Empty)
                + ", blocked="
                + blockedMessage
                + ". The local battle map cannot continue without server confirmation.");
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            ShowUnconfirmedSnapshotFailure(
                operation,
                worldMapStatus,
                () => StartSubmitPreparedRaid(
                    idempotencyKey,
                    raidPreparationId,
                    session,
                    target,
                    pawnGlobalKeys,
                    carriedThings));
            return;
        }

        RemoteSessionMapParent? activeBattleParent = RemoteSessionMapUtility.FindActiveSessionMap(ActiveRemoteMapSession.FromRaidBattle(session));
        Map? activeBattleMap = activeBattleParent?.Map;
        if (activeBattleMap is null)
        {
            worldMapStatus = ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusMissingPreparedBattleMap",
                session.EventId.Named("EVENTID"));
            Log.Warning("[ClashOfRim][Raid] Refusing to submit prepared raid "
                + session.EventId
                + " because the local save has no active battle map. This path must not recreate a remote map.");
            ShowUnconfirmedSnapshotFailure(
                operation,
                worldMapStatus,
                () => StartSubmitPreparedRaid(
                    idempotencyKey,
                    raidPreparationId,
                    session,
                    target,
                    pawnGlobalKeys,
                    carriedThings));
            return;
        }

        if (!TryBeginSnapshotUploadTransaction(allowExistingManualSync: true))
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            Log.Warning("[ClashOfRim][Raid] Prepared raid start snapshot submission could not acquire upload transaction after local battle map mutation: event="
                + session.EventId
                + ", target="
                + (target.WorldObjectId ?? string.Empty)
                + ".");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            ShowUnconfirmedSnapshotFailure(
                operation,
                worldMapStatus,
                () => StartSubmitPreparedRaid(
                    idempotencyKey,
                    raidPreparationId,
                    session,
                    target,
                    pawnGlobalKeys,
                    carriedThings));
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Raid.StatusPrepareSnapshot"));
        SetRemoteMapLongEventText("ClashOfRim.Raid.StatusPrepareSnapshotLong");
        ClashLog.Message("[ClashOfRim][Raid] Packaging prepared raid snapshot: event="
            + session.EventId
            + ", runtimeMap=Map_"
            + activeBattleMap.uniqueID
            + ", sourceMap="
            + session.TargetMapId
            + ", clientMap="
            + session.ClientMapId
            + ", attackerPawns="
            + session.AttackPawnThingIds.Count
            + ".");
        if (!TryBuildCurrentGameSnapshotPackage(
                settings.UserId,
                settings.ColonyId,
                out ModSnapshotPackageBuildResult build,
                out string buildFailureReason))
        {
            worldMapStatus = ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusAttackerSnapshotException",
                "SaveToMemoryFailed".Named("TYPE"),
                buildFailureReason.Named("MESSAGE"));
            EndSnapshotUploadTransaction();
            ShowUnconfirmedSnapshotFailure(
                operation,
                worldMapStatus,
                () => StartSubmitPreparedRaid(
                    idempotencyKey,
                    raidPreparationId,
                    session,
                    target,
                    pawnGlobalKeys,
                    carriedThings));
            return;
        }

        StartSubmitPreparedRaidWithSnapshot(
            idempotencyKey,
            raidPreparationId,
            session,
            target,
            pawnGlobalKeys,
            carriedThings,
            build.Package!,
            build.Payload!,
            uploadTransactionAlreadyStarted: true);
    }

    private void StartSubmitPreparedRaidWithSnapshot(
        string idempotencyKey,
        string raidPreparationId,
        ActiveRaidBattleSession session,
        ModWorldMapMarkerDto target,
        IReadOnlyList<string> pawnGlobalKeys,
        IReadOnlyList<ModThingReferenceDto> carriedThings,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        bool uploadTransactionAlreadyStarted = false)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationRaid");
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            worldMapStatus = blockedMessage;
            Log.Warning("[ClashOfRim][Raid] Prepared raid start snapshot multipart submission blocked by another local atomic mutation: event="
                + session.EventId
                + ", blocked="
                + blockedMessage
                + ".");
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            ShowUnconfirmedSnapshotFailure(
                operation,
                worldMapStatus,
                () => StartSubmitPreparedRaidWithSnapshot(
                    idempotencyKey,
                    raidPreparationId,
                    session,
                    target,
                    pawnGlobalKeys,
                    carriedThings,
                    confirmedSnapshot,
                    confirmedPayload,
                    uploadTransactionAlreadyStarted: false));
            return;
        }

        bool uploadTransactionOwned = uploadTransactionAlreadyStarted;
        if (!uploadTransactionOwned && !TryBeginSnapshotUploadTransaction())
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            Log.Warning("[ClashOfRim][Raid] Prepared raid start snapshot multipart submission could not acquire upload transaction: event="
                + session.EventId
                + ".");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            ShowUnconfirmedSnapshotFailure(
                operation,
                worldMapStatus,
                () => StartSubmitPreparedRaidWithSnapshot(
                    idempotencyKey,
                    raidPreparationId,
                    session,
                    target,
                    pawnGlobalKeys,
                    carriedThings,
                    confirmedSnapshot,
                    confirmedPayload,
                    uploadTransactionAlreadyStarted: false));
            return;
        }
        uploadTransactionOwned = true;

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Raid.StatusSubmitting", FormatSupportTargetLabel(target).Named("TARGET")));
        worldMapStatus = ClashOfRimText.Key(
            "ClashOfRim.Raid.StatusSubmitting",
            FormatSupportTargetLabel(target).Named("TARGET"));
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModEventCreationResponseDto> result =
                    await client.CreateRaidWithSnapshotAsync(
                        idempotencyKey,
                        raidPreparationId,
                        target.OwnerUserId ?? string.Empty,
                        target.OwnerColonyId ?? string.Empty,
                        session.DefenderSnapshotId,
                        target.WorldObjectId ?? string.Empty,
                        target.MapId ?? string.Empty,
                        target.Tile,
                        isHostile: true,
                        defenderOnline: false,
                        defenderWealth: 1_000_000,
                        defenderRaidCooldownUntilUtc: null,
                        pawnGlobalKeys: pawnGlobalKeys,
                        carriedThings: carriedThings,
                        confirmedSnapshot,
                        confirmedPayload,
                        session.GuardDeploymentId);

                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (!result.Success || result.Response is null)
                    {
                        worldMapStatus = ClashOfRimText.Key(
                            "ClashOfRim.Raid.StatusCreateFailed",
                            result.ErrorCode.Named("CODE"),
                            result.Message.Named("MESSAGE"));
                        Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
                        ShowUnconfirmedSnapshotFailure(
                            operation,
                            worldMapStatus,
                            () => StartSubmitPreparedRaidWithSnapshot(
                                idempotencyKey,
                                raidPreparationId,
                                session,
                                target,
                                pawnGlobalKeys,
                                carriedThings,
                                confirmedSnapshot,
                                confirmedPayload));
                        return;
                    }

                    ModProtocolResponseDto? response = result.Response.Result;
                    if (response is not null && !response.Accepted)
                    {
                        worldMapStatus = ClashOfRimText.Key(
                            "ClashOfRim.Raid.StatusCreateRejected",
                            response.ErrorCode.Named("CODE"),
                            response.Message.Named("MESSAGE"));
                        Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
                        ShowUnconfirmedSnapshotFailure(
                            operation,
                            worldMapStatus,
                            () => StartSubmitPreparedRaidWithSnapshot(
                                idempotencyKey,
                                raidPreparationId,
                                session,
                                target,
                                pawnGlobalKeys,
                                carriedThings,
                                confirmedSnapshot,
                                confirmedPayload));
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(result.Response.AppliedSnapshotId))
                    {
                        session.AttackerSnapshotId = result.Response.AppliedSnapshotId!;
                        PersistAcceptedSnapshotLineage(
                            result.Response.AppliedSnapshotId!,
                            result.Response.NextLineageToken);
                    }
                    ApplyRaidBattleServerDeadlines(session, result.Response);

                    ClashLog.Message("[ClashOfRim][Raid] Prepared raid start snapshot accepted: event="
                        + result.Response.EventId
                        + ", appliedSnapshot="
                        + (result.Response.AppliedSnapshotId ?? string.Empty)
                        + ", nextLineage="
                        + (result.Response.NextLineageToken ?? string.Empty)
                        + ".");

                    manualSyncInProgress = false;
                    worldMapStatus = ClashOfRimText.Key(
                        "ClashOfRim.Raid.StatusCreated",
                        result.Response.EventId.Named("EVENTID"));
                    Messages.Message(
                        ClashOfRimText.Key("ClashOfRim.Raid.CreatedMessage", FormatSupportTargetLabel(target).Named("TARGET")),
                        MessageTypeDefOf.ThreatBig,
                        historical: false);
                    CompleteLocalAtomicMutation();
                    CloseUnconfirmedSnapshotFailureWindow();
                });
            }
            catch (Exception ex)
            {
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    worldMapStatus = ClashOfRimText.Key(
                        "ClashOfRim.Raid.StatusCreateException",
                        ex.GetType().Name.Named("TYPE"),
                        ex.Message.Named("MESSAGE"));
                    Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
                    ShowUnconfirmedSnapshotFailure(
                        operation,
                        worldMapStatus,
                        () => StartSubmitPreparedRaidWithSnapshot(
                            idempotencyKey,
                            raidPreparationId,
                            session,
                            target,
                            pawnGlobalKeys,
                            carriedThings,
                            confirmedSnapshot,
                            confirmedPayload));
                });
                Log.Warning("[ClashOfRim] Prepared raid submission failed: " + ex);
            }
            finally
            {
                if (uploadTransactionOwned)
                {
                    EndSnapshotUploadTransaction();
                }
            }
        });
    }

    private static string BuildLocalRaidEventId(string idempotencyKey)
    {
        string normalizedKey = string.Concat((idempotencyKey ?? string.Empty)
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-'));
        return "raid:" + normalizedKey;
    }

    private static TimeSpan RaidDurationFromServerMinutes(double? minutes, TimeSpan fallback)
    {
        if (minutes is null || double.IsNaN(minutes.Value) || double.IsInfinity(minutes.Value) || minutes.Value <= 0d)
        {
            return fallback;
        }

        return TimeSpan.FromMinutes(minutes.Value);
    }

    private static void ApplyRaidBattleServerDeadlines(
        ActiveRaidBattleSession session,
        ModEventCreationResponseDto response)
    {
        if (!TryParseServerUtc(response.RaidStartedAtUtc, out DateTimeOffset startedAtUtc)
            || !TryParseServerUtc(response.RaidDeadlineUtc, out DateTimeOffset deadlineUtc)
            || !TryParseServerUtc(response.RaidFinalDeadlineUtc, out DateTimeOffset finalDeadlineUtc))
        {
            ClashLog.Message("[ClashOfRim][Raid] Raid creation response did not include server deadlines; keeping local fallback deadlines: event="
                + session.EventId
                + ".");
            return;
        }

        session.StartedAtUtcTicks = startedAtUtc.UtcDateTime.Ticks;
        session.DeadlineUtcTicks = deadlineUtc.UtcDateTime.Ticks;
        session.FinalDeadlineUtcTicks = finalDeadlineUtc.UtcDateTime.Ticks;
        ClashLog.Message("[ClashOfRim][Raid] Applied server raid deadlines: event="
            + session.EventId
            + ", deadline="
            + deadlineUtc.ToString("O")
            + ", finalDeadline="
            + finalDeadlineUtc.ToString("O")
            + ".");
    }

    private void StartConfirmRaidStartSnapshot(ActiveRaidBattleSession session)
    {
        StartConfirmLocalMutationSnapshot(new LocalMutationSnapshotConfirmationRequest
        {
            Operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationRaid"),
            UploadingStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerSnapshotUploading"),
            RetryAction = () => StartConfirmRaidStartSnapshot(session),
            SetStatus = value => worldMapStatus = value,
            BuildFailureStatus = upload => ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusAttackerSnapshotFailed",
                upload.ErrorCode.Named("CODE"),
                upload.Message.Named("MESSAGE")),
            BuildSuccessStatus = upload => ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusAttackerSnapshotSucceeded",
                upload.AcceptedSnapshotId.Named("SNAPSHOT")),
            BuildExceptionStatus = ex => ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusAttackerSnapshotException",
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE"))
        });
    }

    internal void StartFinishActiveRaidBattle(ActiveRaidBattleSession session, string finishReason)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.EventId))
        {
            return;
        }

        if (!RemoteMapSessionController.TryBeginRaidBattleFinish(session))
        {
            return;
        }

        worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusSettlementUploading");
        var context = new RaidBattleSettlementContext(session, finishReason);

        if (TryReportRaidSettlementSupportLossesBeforeSnapshot(context))
        {
            return;
        }

        QueueRemoteMapLongEvent(
            "ClashOfRim.Raid.StatusSettlementSavingLong",
            () => ContinueFinishActiveRaidBattleAfterSupportResolution(context));
    }

    private void ContinueFinishActiveRaidBattleAfterSupportResolution(RaidBattleSettlementContext context)
    {
        ActiveRaidBattleSession session = context.Session;
        if (!TryBeginSnapshotUploadTransaction(allowExistingManualSync: true))
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            RemoteMapSessionController.ResetRaidBattleFinish(session);
            return;
        }

        SetRemoteMapLongEventText("ClashOfRim.Raid.StatusSettlementSavingLong");
        if (!TryBuildCurrentGameSnapshotPackage(
                settings.UserId,
                settings.ColonyId,
                out ModSnapshotPackageBuildResult build,
                out string buildFailureReason))
        {
            Log.Warning("[ClashOfRim][Raid] Failed to build raid settlement snapshot: event="
                + session.EventId
                + ", reason="
                + buildFailureReason
                + ", buildCode="
                + (build.ErrorCode ?? string.Empty)
                + ", buildMessage="
                + (build.Message ?? string.Empty)
                + ".");
            worldMapStatus = ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusSettlementSaveFailed",
                buildFailureReason.Named("REASON"));
            RemoteMapSessionController.ResetRaidBattleFinish(session);
            EndSnapshotUploadTransaction();
            ShowUnconfirmedSnapshotFailure(
                ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationRaid"),
                worldMapStatus,
                () => StartFinishActiveRaidBattle(session, context.FinishReason));
            return;
        }

        if (!TryPrepareRaidSettlementSnapshotPackage(
                build,
                session,
                out ModSnapshotPackageMetadataDto? confirmedSnapshot,
                out byte[]? confirmedPayload,
                out string prepareFailureReason))
        {
            Log.Warning("[ClashOfRim][Raid] Failed to prepare returned raid settlement snapshot: event="
                + session.EventId
                + ", reason="
                + prepareFailureReason
                + ".");
            worldMapStatus = ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusSettlementSaveFailed",
                prepareFailureReason.Named("REASON"));
            RemoteMapSessionController.ResetRaidBattleFinish(session);
            EndSnapshotUploadTransaction();
            ShowUnconfirmedSnapshotFailure(
                ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationRaid"),
                worldMapStatus,
                () => StartFinishActiveRaidBattle(session, context.FinishReason));
            return;
        }

        StartUploadPreparedRaidSettlement(
            context.WithPreparedSnapshot(confirmedSnapshot!, confirmedPayload!),
            uploadTransactionAlreadyStarted: true);
    }

    private static bool TryPrepareRaidSettlementSnapshotPackage(
        ModSnapshotPackageBuildResult build,
        ActiveRaidBattleSession session,
        out ModSnapshotPackageMetadataDto? confirmedSnapshot,
        out byte[]? confirmedPayload,
        out string failureReason)
    {
        confirmedSnapshot = null;
        confirmedPayload = null;
        failureReason = string.Empty;

        byte[] originalPayload = build.OriginalPayload ?? Array.Empty<byte>();
        if (!RaidBattleSnapshotPreparer.TryPrepareReturnedSnapshot(
                originalPayload,
                session,
                out byte[] preparedSaveBytes,
                out failureReason))
        {
            return false;
        }

        ModSnapshotPackageBuildResult prepared = ModSnapshotPackageBuilder.FromSaveBytes(
            preparedSaveBytes,
            "raid-settlement",
            build.Package?.OwnerId ?? string.Empty,
            build.Package?.ColonyId ?? string.Empty,
            DateTime.UtcNow);
        if (!prepared.Success || prepared.Package is null || prepared.Payload is null)
        {
            failureReason = $"{prepared.ErrorCode} {prepared.Message}";
            return false;
        }

        confirmedSnapshot = prepared.Package;
        confirmedPayload = prepared.Payload;
        return true;
    }

    private void StartUploadPreparedRaidSettlement(
        RaidBattleSettlementContext context,
        bool uploadTransactionAlreadyStarted = false)
    {
        ActiveRaidBattleSession session = context.Session;
        if (session is null || string.IsNullOrWhiteSpace(session.EventId))
        {
            return;
        }

        ModSnapshotPackageMetadataDto? confirmedSnapshot = context.ConfirmedSnapshot;
        byte[]? confirmedPayload = context.ConfirmedPayload;
        if (confirmedSnapshot is null || confirmedPayload is null)
        {
            return;
        }

        RemoteMapSessionController.KeepRaidBattleFinishInProgress(session);

        StartConfirmPreparedIdentityEventApplicationSnapshot(new PreparedIdentityEventApplicationSnapshotConfirmationRequest
        {
            Operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationRaid"),
            EventId = session.EventId,
            UserId = settings.UserId,
            ColonyId = settings.ColonyId,
            BaseSnapshotId = settings.CurrentSnapshotId,
            ConfirmedSnapshot = confirmedSnapshot,
            ConfirmedPayload = confirmedPayload,
            ClientApplicationResult = context.DefenderClientApplicationResult,
            IdempotencyKey = context.DefenderIdempotencyKey,
            HttpTimeout = RaidSettlementHttpTimeout(session),
            UploadingStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusSettlementUploading"),
            SetStatus = value => worldMapStatus = value,
            RetryAction = () => StartUploadPreparedRaidSettlement(context),
            UploadTransactionAlreadyStarted = uploadTransactionAlreadyStarted,
            CompleteMutationBeforeAcceptedCallback = true,
            ShowFailure = (retry, allowRetry) => ShowRaidSettlementFailure(context, retry, allowRetry),
            BuildRequestFailedStatus = result => ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusSettlementFailed",
                result.ErrorCode.Named("CODE"),
                result.Message.Named("MESSAGE")),
            BuildRejectedStatus = (serverResult, response) => ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusSettlementRejected",
                serverResult.ErrorCode.Named("CODE"),
                serverResult.Message.Named("MESSAGE"),
                response.ServerValidationResult.Named("VALIDATION")),
            BuildExceptionStatus = ex => ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusSettlementException",
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE")),
            OnAcceptedOnMainThread = response =>
            {
                if (!string.Equals(
                        ClashOfRimGameComponent.ActiveRaidBattleSession?.EventId,
                        session.EventId,
                        StringComparison.Ordinal))
                {
                    return;
                }

                worldMapStatus = ClashOfRimText.Key(
                    "ClashOfRim.Raid.StatusSettlementSucceeded",
                    response.AppliedSnapshotId.Named("SNAPSHOT"));
                Messages.Message(
                    ClashOfRimText.Key("ClashOfRim.Raid.SettlementSucceededMessage"),
                    MessageTypeDefOf.PositiveEvent,
                    historical: false);
                RemoteMapSessionController.CloseRaidBattleAfterSettlementAccepted(session, context.FinishReason);
                ClashOfRimGameComponent.ClearActiveRaidBattleSession(session.EventId);
                StartConfirmRaidAttackerSnapshotWhenReady(context);
            }
        });
    }

    private void StartConfirmRaidAttackerSnapshotWhenReady(RaidBattleSettlementContext context, int attempts = 0)
    {
        if (SnapshotUploadInProgress)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerSnapshotWaiting");
            if (attempts == 0 || attempts % RaidAttackerSnapshotWaitLogInterval == 0)
            {
                ClashLog.Message("[ClashOfRim][Raid] Waiting for settlement upload transaction before attacker snapshot: event="
                    + context.Session.EventId
                    + ", attempts="
                    + attempts
                    + ".");
            }

            Task.Run(async () =>
            {
                await Task.Delay(RaidAttackerSnapshotRetryDelayMs);
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    StartConfirmRaidAttackerSnapshotWhenReady(context, attempts + 1));
            });
            return;
        }

        StartConfirmRaidAttackerSnapshot(context);
    }

    private void StartConfirmRaidAttackerSnapshot(RaidBattleSettlementContext context)
    {
        ActiveRaidBattleSession session = context.Session;
        if (session is null || string.IsNullOrWhiteSpace(session.EventId))
        {
            return;
        }

        StartConfirmLocalMutationSnapshot(new LocalMutationSnapshotConfirmationRequest
        {
            Operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationRaidAttacker"),
            UploadingStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerSnapshotUploading"),
            RemoveRaidBattleSessions = true,
            BestEffort = true,
            RetryAction = () => StartConfirmRaidAttackerSnapshot(context),
            SetStatus = value => worldMapStatus = value,
            BuildFailureStatus = upload => ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusAttackerSnapshotFailed",
                upload.ErrorCode.Named("CODE"),
                upload.Message.Named("MESSAGE")),
            BuildSuccessStatus = upload => ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusAttackerSnapshotSucceeded",
                upload.AcceptedSnapshotId.Named("SNAPSHOT")),
            BuildExceptionStatus = ex => ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusAttackerSnapshotException",
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE")),
            OnSuccessOnMainThread = upload =>
            {
                Messages.Message(
                    ClashOfRimText.Key("ClashOfRim.Raid.AttackerSnapshotSucceededMessage"),
                    MessageTypeDefOf.PositiveEvent,
                    historical: false);
                ClashLog.Message("[ClashOfRim][Raid] Attacker cleanup snapshot accepted without server snapshot reload: event="
                    + session.EventId
                    + ", snapshot="
                    + (upload.AcceptedSnapshotId ?? string.Empty)
                    + ".");
                StartRefreshPlayers(ClashOfRimText.Key("ClashOfRim.Raid.AttackerSnapshotSucceededMessage"), requireManualGate: false);
            }
        });
    }

    private bool TryReportRaidSettlementSupportLossesBeforeSnapshot(RaidBattleSettlementContext context)
    {
        ActiveRaidBattleSession session = context.Session;
        ClashOfRimGameComponent? component = Verse.Current.Game?.GetComponent<ClashOfRimGameComponent>();
        if (component is null)
        {
            return false;
        }

        List<PendingSupportPawnLoss> pending = new();
        foreach (ActiveSupportPawnAssignment assignment in component.CopyActiveSupportAssignments())
        {
            if (!assignment.AutoReturnOnSettlement || assignment.FinishInProgress)
            {
                continue;
            }

            Pawn? pawn = FindActiveSupportPawn(assignment);
            if (pawn is null || pawn.Dead)
            {
                continue;
            }

            if (pawn.Map is null || !MapIdsMatch("Map_" + pawn.Map.uniqueID, session.ClientMapId))
            {
                continue;
            }

            pending.Add(new PendingSupportPawnLoss(
                assignment,
                pawn,
                string.IsNullOrWhiteSpace(pawn.LabelShort) ? assignment.PawnLabel : pawn.LabelShort));
        }

        if (pending.Count == 0)
        {
            return false;
        }

        foreach (PendingSupportPawnLoss loss in pending)
        {
            component.MarkSupportAssignmentInProgress(loss.Assignment.EventId, inProgress: true);
        }

        worldMapStatus = ClashOfRimText.Key(
            "ClashOfRim.Raid.StatusSettlementReportingSupportLoss",
            pending.Count.Named("COUNT"));

        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                foreach (PendingSupportPawnLoss loss in pending)
                {
                    string idempotencyKey =
                        $"support-finish:{settings.UserId}:{settings.ColonyId}:{loss.Assignment.EventId}:RaidSettlementLost";
                    ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto> result =
                        await client.FinishSupportPawnAsync(
                            idempotencyKey,
                            loss.Assignment.EventId,
                            "RaidSettlementLost",
                            loss.Assignment.PawnGlobalKey,
                            loss.PawnLabel,
                            pawnDead: true,
                            pawnPackage: null);

                    if (!result.Success || result.Response is null)
                    {
                        worldMapStatus = ClashOfRimText.Key(
                            "ClashOfRim.Raid.StatusSettlementSupportLossFailed",
                            loss.PawnLabel.Named("PAWN"),
                            result.ErrorCode.Named("CODE"),
                            result.Message.Named("MESSAGE"));
                        ShowRaidSettlementSupportLossFailure(context, component, pending);
                        return;
                    }

                    ModProtocolResponseDto? response = result.Response.Result;
                    if (response is not null && !response.Accepted)
                    {
                        worldMapStatus = ClashOfRimText.Key(
                            "ClashOfRim.Raid.StatusSettlementSupportLossRejected",
                            loss.PawnLabel.Named("PAWN"),
                            response.ErrorCode.Named("CODE"),
                            response.Message.Named("MESSAGE"));
                        ShowRaidSettlementSupportLossFailure(context, component, pending);
                        return;
                    }
                }

                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    foreach (PendingSupportPawnLoss loss in pending)
                    {
                        Pawn? currentPawn = FindActiveSupportPawn(loss.Assignment) ?? loss.Pawn;
                        if (!RemoveSupportPawnFromLocalMap(currentPawn))
                        {
                            worldMapStatus = ClashOfRimText.Key(
                                "ClashOfRim.Raid.StatusSettlementSupportLossLocalRemoveFailed",
                                loss.PawnLabel.Named("PAWN"));
                            ShowRaidSettlementSupportLossFailure(context, component, pending);
                            return;
                        }

                        component.MarkSupportAssignmentFinished(loss.Assignment.EventId);
                    }

                    QueueRemoteMapLongEvent(
                        "ClashOfRim.Raid.StatusSettlementSavingLong",
                        () => ContinueFinishActiveRaidBattleAfterSupportResolution(context));
                });
            }
            catch (Exception ex)
            {
                worldMapStatus = ClashOfRimText.Key(
                    "ClashOfRim.Raid.StatusSettlementSupportLossException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                ShowRaidSettlementSupportLossFailure(context, component, pending);
                Log.Warning("[ClashOfRim] Raid settlement support loss reporting failed: " + ex);
            }
        });

        return true;
    }

    private void ShowRaidSettlementSupportLossFailure(
        RaidBattleSettlementContext context,
        ClashOfRimGameComponent component,
        IReadOnlyList<PendingSupportPawnLoss> pending)
    {
        ActiveRaidBattleSession session = context.Session;
        ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
        {
            if (!string.Equals(
                    ClashOfRimGameComponent.ActiveRaidBattleSession?.EventId,
                    session.EventId,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (session.IsFinalDeadlineExpired)
            {
                HandleRaidBattleFinalDeadlineExpired(session);
                return;
            }

            foreach (PendingSupportPawnLoss loss in pending)
            {
                component.MarkSupportAssignmentInProgress(loss.Assignment.EventId, inProgress: false);
            }

            manualSyncInProgress = false;
            RemoteMapSessionController.ResetRaidBattleFinish(session);
            ShowUnconfirmedSnapshotFailure(
                ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationRaid"),
                worldMapStatus,
                () => StartFinishActiveRaidBattle(session, context.FinishReason));
        });
    }

    private void ShowRaidSettlementFailure(
        RaidBattleSettlementContext context,
        Action? retryAction = null,
        bool allowRetry = false)
    {
        ActiveRaidBattleSession session = context.Session;
        ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
        {
            if (session.IsFinalDeadlineExpired)
            {
                HandleRaidBattleFinalDeadlineExpired(session);
                return;
            }

            manualSyncInProgress = false;
            RemoteMapSessionController.ResetRaidBattleFinish(session);
            ShowUnconfirmedSnapshotFailure(
                ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationRaid"),
                worldMapStatus,
                retryAction ?? (() => StartFinishActiveRaidBattle(session, context.FinishReason)),
                allowRetry);
        });
    }

    internal void HandleRaidBattleFinalDeadlineExpired(ActiveRaidBattleSession session, bool closeLocalBattleMap = true)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.EventId))
        {
            return;
        }

        if (!finalDeadlineRaidCleanupEventIds.Add(session.EventId))
        {
            ClashLog.Message("[ClashOfRim][Raid] Ignoring duplicate final-deadline handling for active raid "
                + session.EventId
                + ".");
            return;
        }

        if (closeLocalBattleMap)
        {
            RemoteMapSessionController.CloseRaidBattleForFinalDeadline(session);
        }
        else
        {
            ClashOfRimGameComponent.ClearActiveRaidBattleSession(session.EventId);
        }

        CloseUnconfirmedSnapshotFailureWindow();
        manualSyncInProgress = false;
        worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusSettlementFinalDeadlineExpired");
        Messages.Message(worldMapStatus, MessageTypeDefOf.NegativeEvent, historical: false);
        StartConfirmSettledRaidCleanupSnapshot(session.EventId);
    }

    private static TimeSpan RaidSettlementHttpTimeout(ActiveRaidBattleSession session)
    {
        TimeSpan remaining = session.FinalRemaining;
        if (remaining <= TimeSpan.FromSeconds(5))
        {
            return TimeSpan.FromSeconds(5);
        }

        return remaining < TimeSpan.FromSeconds(60)
            ? remaining
            : TimeSpan.FromSeconds(60);
    }
    private sealed class PendingSupportPawnLoss
    {
        public PendingSupportPawnLoss(
            ActiveSupportPawnAssignment assignment,
            Pawn pawn,
            string pawnLabel)
        {
            Assignment = assignment;
            Pawn = pawn;
            PawnLabel = pawnLabel;
        }

        public ActiveSupportPawnAssignment Assignment { get; }
        public Pawn Pawn { get; }
        public string PawnLabel { get; }
    }
}
