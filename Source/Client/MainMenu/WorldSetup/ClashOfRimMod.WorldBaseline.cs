using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Admin;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.WorldObjects;
using AIRsLight.ClashOfRim.Protocol;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Profile;
using static AIRsLight.ClashOfRim.ClientWorldRuntimeValues;
using static AIRsLight.ClashOfRim.ClientWorldConfigurationRuntime;
using static AIRsLight.ClashOfRim.ClientWorldColonySitesRuntime;
using static AIRsLight.ClashOfRim.ClientWorldNameRuntime;
using AIRsLight.ClashOfRim.Modules.World;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal bool TryGenerateWorldFromServerConfiguration(Window createWorldPage)
        => worldGeneration.TryStart(new ClientWorldGenerationPage(createWorldPage));

    internal bool IsBlockedByServerColonySite(int tile, out ModPlayerColonySiteDto? blockingSite)
    {
        blockingSite = null;
        if (Find.WorldGrid is null)
        {
            return false;
        }

        List<ModPlayerColonySiteDto> sites;
        lock (colonySiteStateLock)
        {
            if (occupiedPlayerColonySites.Count == 0)
            {
                return false;
            }

            sites = occupiedPlayerColonySites.Values.ToList();
        }

        foreach (ModPlayerColonySiteDto site in sites)
        {
            if (!IsValidTile(site.Tile))
            {
                continue;
            }

            if (Find.WorldGrid.IsNeighborOrSame(tile, site.Tile))
            {
                blockingSite = site;
                return true;
            }
        }

        return false;
    }

    internal bool IsBlockedByServerColonySite(PlanetTile tile, out ModPlayerColonySiteDto? blockingSite)
    {
        blockingSite = null;
        if (!tile.Valid || tile.LayerDef.isSpace)
        {
            return false;
        }

        return IsBlockedByServerColonySite(tile.tileId, out blockingSite);
    }

    internal static string FormatBlockedColonySite(ModPlayerColonySiteDto site)
    {
        string owner = string.IsNullOrWhiteSpace(site.UserId)
            ? ClashOfRimText.Key("ClashOfRim.UnknownOtherPlayer")
            : site.UserId;
        string label = string.IsNullOrWhiteSpace(site.Label)
            ? site.ColonyId ?? string.Empty
            : site.Label ?? string.Empty;
        return string.IsNullOrWhiteSpace(label)
            ? $"{owner}@{site.Tile}"
            : $"{owner}/{label}@{site.Tile}";
    }

    private void UpdateOccupiedPlayerColonySites(ModWorldConfigurationDto? configuration)
    {
        RememberWorldConfigurationIdentity(configuration);
        List<string> remoteSiteOwners = new();
        lock (colonySiteStateLock)
        {
            occupiedPlayerColonySites.Clear();
            if (configuration?.PlayerColonySites is null)
            {
                return;
            }

            foreach (ModPlayerColonySiteDto site in configuration.PlayerColonySites)
            {
                if (site.Tile >= 0)
                {
                    occupiedPlayerColonySites[ColonySiteCacheKey(site)] = site;
                    if (string.Equals(site.UserId, settings.UserId, StringComparison.Ordinal)
                        && string.Equals(site.ColonyId, settings.ColonyId, StringComparison.Ordinal))
                    {
                        CacheServerColonyAppearance(site.Appearance);
                    }

                    if (!string.IsNullOrWhiteSpace(site.UserId)
                        && !string.Equals(site.UserId, settings.UserId, StringComparison.Ordinal)
                        && !remoteSiteOwners.Contains(site.UserId, StringComparer.Ordinal))
                    {
                        remoteSiteOwners.Add(site.UserId);
                    }
                }
            }
        }

        foreach (string owner in remoteSiteOwners)
        {
            PlayerFactionProxyUtility.EnsureProxyForUser(owner);
        }
    }

    private void RememberWorldConfigurationIdentity(ModWorldConfigurationDto? configuration) => worldConfiguration.Remember(configuration);

    private void MergeOccupiedPlayerColonySitesFromWorldMapMarkers(IEnumerable<ModWorldMapMarkerDto> markers)
    {
        List<ModPlayerColonySiteDto> colonySites = markers
            .Where(marker => string.Equals(marker.Kind, "TradeableColony", StringComparison.Ordinal))
            .Where(marker => marker.Tile >= 0)
            .Where(marker => !string.IsNullOrWhiteSpace(marker.OwnerUserId))
            .Select(marker => new ModPlayerColonySiteDto
            {
                UserId = marker.OwnerUserId,
                ColonyId = marker.OwnerColonyId ?? string.Empty,
                WorldObjectId = marker.WorldObjectId,
                MapUniqueId = marker.MapId,
                Tile = marker.Tile,
                TileLayerId = marker.TileLayerId,
                Label = marker.Label,
                Appearance = marker.Appearance
            })
            .ToList();
        if (colonySites.Count == 0)
        {
            return;
        }

        lock (colonySiteStateLock)
        {
            foreach (ModPlayerColonySiteDto site in colonySites)
            {
                occupiedPlayerColonySites[ColonySiteCacheKey(site)] = site;
            }
        }
    }

    private static string ColonySiteCacheKey(ModPlayerColonySiteDto site)
    {
        return site.Tile.ToString(CultureInfo.InvariantCulture)
            + ","
            + site.TileLayerId.ToString(CultureInfo.InvariantCulture);
    }

    internal void SubmitInitialWorldConfigurationIfPending()
    {
        var initial = worldSetup.TakeInitial();
        if (initial is null)
        {
            return;
        }

        if (!settings.IsConfigured)
        {
            loginStatus = ClashOfRimText.Key("ClashOfRim.WorldBaseline.StatusConfigMissing");
            return;
        }

        var capture = worldBaselineSubmission.Capture();
        ClientWorldBaselineCapture.Result captured;
        try { captured = worldBaselineCapture.Capture(capture, BuildCurrentColonyAppearanceDto(settings)); }
        catch (Exception ex)
        {
            loginStatus = ClashOfRimText.Key("ClashOfRim.WorldBaseline.StatusInvalid", ex.Message.Named("REASON"));
            Log.Warning("[ClashOfRim] Initial world baseline capture failed: " + ex);
            return;
        }
        if (captured.Error is { } error)
        {
            loginStatus = ClashOfRimText.Key("ClashOfRim.WorldBaseline.StatusInvalid", DescribeWorldCaptureFailure(error).Named("REASON"));
            Log.Warning("[ClashOfRim] " + loginStatus);
            Messages.Message(loginStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        var prepared = captured.Prepared;
        if (prepared is null) { worldSetup.RestoreInitial(initial); return; }
        var configuration = captured.Configuration!;

        // Keep automatic map login from racing the temporary authenticated setup session.
        if (!TryBeginManualSynchronization(out var manualOperation, out string admissionFailure))
        {
            worldBaselineSubmission.Discard(prepared);
            worldSetup.RestoreInitial(initial);
            loginStatus = admissionFailure;
            return;
        }
        loginStatus = ClashOfRimText.Key("ClashOfRim.WorldBaseline.StatusSubmitting");
        ClashLog.Message("[ClashOfRim] Submitting initial world configuration: " + DescribeWorldConfiguration(configuration));
        ClientSessionWork.Run(async () =>
        {
            try
            {
                var result = await worldBaselineSubmission.SubmitAsync(prepared);
                if (result is null) return;
                using (ClientSessionWork.EnterPublication())
                {
                    if (!worldBaselineSubmission.IsCurrent(prepared)) return;
                    if (!result.Success || result.Response?.Result?.Accepted != true)
                    {
                        loginStatus = ClashOfRimText.Key("ClashOfRim.WorldBaseline.StatusWorldConfigFailed", (result.ErrorCode ?? result.Response?.Result?.ErrorCode.ToString() ?? string.Empty).Named("CODE"), (result.Message ?? result.Response?.Result?.Message ?? string.Empty).Named("MESSAGE"));
                        Log.Warning("[ClashOfRim] Initial world configuration rejected: " + loginStatus);
                        return;
                    }

                    ClashLog.Message("[ClashOfRim] Initial world configuration accepted by server: " + DescribeWorldConfiguration(result.Response.WorldConfiguration));
                }

                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(httpClient, prepared.Context);
                ClashOfRimClientNetworkResult<ModGetAdminBaselineRequirementsResponseDto> requirementsResult =
                    await ClientSessionWork.Observe(client.GetAdminBaselineRequirementsAsync());
                using (ClientSessionWork.EnterPublication())
                {
                    if (!worldBaselineSubmission.IsCurrent(prepared)) return;
                    if (!requirementsResult.Success || requirementsResult.Response?.Result?.Accepted != true)
                    {
                        loginStatus = ClashOfRimText.Key("ClashOfRim.WorldBaseline.StatusAdminBaselineFailed", (requirementsResult.ErrorCode ?? requirementsResult.Response?.Result?.ErrorCode.ToString() ?? string.Empty).Named("CODE"), (requirementsResult.Message ?? requirementsResult.Response?.Result?.Message ?? string.Empty).Named("MESSAGE"));
                        Log.Warning("[ClashOfRim] Admin baseline requirements rejected after world configuration submit: " + loginStatus);
                        EnqueueClashOfRimMainThreadAction(() => worldBaselineSubmission.TryApply(prepared, () =>
                            Messages.Message(loginStatus, MessageTypeDefOf.RejectInput, historical: false)));
                        return;
                    }
                }

                ModSubmitAdminBaselineRequestDto adminBaseline = await ClientSessionWork.Observe(BuildAdminBaselineOnMainThreadAsync(
                    requirementsResult.Response.BaselineExtensions, prepared));
                if (!worldBaselineSubmission.IsCurrent(prepared)) return;
                ClashOfRimClientNetworkResult<ModSubmitAdminBaselineResponseDto> baselineResult =
                    await ClientSessionWork.Observe(client.SubmitAdminBaselineAsync(adminBaseline));
                using var publication = ClientSessionWork.EnterPublication();
                if (!worldBaselineSubmission.IsCurrent(prepared)) return;
                if (!baselineResult.Success || baselineResult.Response?.Result?.Accepted != true)
                {
                    loginStatus = ClashOfRimText.Key(
                        "ClashOfRim.WorldBaseline.StatusAdminBaselineFailed",
                        (baselineResult.ErrorCode ?? baselineResult.Response?.Result?.ErrorCode.ToString() ?? string.Empty).Named("CODE"),
                        (baselineResult.Message ?? baselineResult.Response?.Result?.Message ?? string.Empty).Named("MESSAGE"));
                    Log.Warning("[ClashOfRim] Admin baseline rejected after world configuration submit: " + loginStatus);
                    EnqueueClashOfRimMainThreadAction(() => worldBaselineSubmission.TryApply(prepared, () =>
                        Messages.Message(loginStatus, MessageTypeDefOf.RejectInput, historical: false)));
                    return;
                }

                ModSubmitAdminBaselineResponseDto baselineResponse = baselineResult.Response;
                loginStatus = ClashOfRimText.Key(
                    "ClashOfRim.WorldBaseline.StatusSubmitted",
                    baselineResponse.StandardMarketValueCount.Named("PRICECOUNT"),
                    baselineResponse.TrapAutoApprovedCount.Named("TRAPAUTO"),
                    baselineResponse.TrapCandidateCount.Named("TRAPCANDIDATE"));
                EnqueueClashOfRimMainThreadAction(() => worldBaselineSubmission.Complete(prepared, () =>
                {
                    Messages.Message(loginStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                    RequestWorldMapMarkerRefresh(ClashOfRimText.Key("ClashOfRim.WorldBaseline.ReasonSubmitted"));
                    if (!worldBaselineSubmission.IsCurrent(prepared)) return;
                    StartInitialColonySnapshotUpload();
                }));
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                if (!worldBaselineSubmission.IsCurrent(prepared)) return;
                loginStatus = ClashOfRimText.Key("ClashOfRim.WorldBaseline.StatusSubmitException", ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Initial world configuration submit exception: " + ex);
            }
            finally
            {
                manualOperation.Dispose();
            }
        });
    }

    internal void StartUpdateServerWorldBaseline()
    {
        if (!CanRunAdminRequest(out string failure))
        {
            adminStatus = failure;
            return;
        }

        var capture = worldBaselineSubmission.Capture();
        ClientWorldBaselineCapture.Result captured;
        try
        {
            captured = worldBaselineCapture.Capture(capture, BuildCurrentColonyAppearanceDto(settings));
        }
        catch (Exception ex)
        {
            adminStatus = ClashOfRimText.Key(
                "ClashOfRim.Admin.WorldBaselineCaptureFailed",
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE"));
            Log.Warning("[ClashOfRim] World baseline capture failed: " + ex);
            Messages.Message(adminStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (captured.Error is { } error)
        {
            adminStatus = ClashOfRimText.Key("ClashOfRim.Admin.WorldBaselineInvalid", DescribeWorldCaptureFailure(error).Named("REASON"));
            Messages.Message(adminStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        var prepared = captured.Prepared;
        if (prepared is null) return;

        adminInProgress = true;
        adminStatus = ClashOfRimText.Key("ClashOfRim.Admin.WorldBaselineUpdating");
        ClientSessionWork.Run(async () =>
        {
            try
            {
                var result = await worldBaselineSubmission.SubmitAsync(prepared);
                if (result is null) return;
                using var publication = ClientSessionWork.EnterPublication();
                if (!worldBaselineSubmission.IsCurrent(prepared)) return;
                if (!result.Success || result.Response is null)
                {
                    adminStatus = ClashOfRimText.Key(
                        "ClashOfRim.Admin.StatusFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? serverResult = result.Response.Result;
                if (serverResult is not null && !serverResult.Accepted)
                {
                    adminStatus = ClashOfRimText.Key(
                        "ClashOfRim.Admin.StatusRejected",
                        serverResult.ErrorCode.Named("CODE"),
                        (serverResult.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ModWorldConfigurationDto? acceptedConfiguration = result.Response.WorldConfiguration;
                adminStatus = ClashOfRimText.Key(
                    "ClashOfRim.Admin.WorldBaselineUpdated",
                    (acceptedConfiguration?.Factions.Count ?? 0).Named("FACTIONS"),
                    (acceptedConfiguration?.WorldObjects.Count ?? 0).Named("WORLDOBJECTS"));
                ClashOfRimGameComponent.EnqueueMainThreadAction(() => worldBaselineSubmission.Complete(prepared, () =>
                {
                    if (acceptedConfiguration is not null)
                    {
                        ApplyWorldBaseline(acceptedConfiguration);
                        if (!worldBaselineSubmission.IsCurrent(prepared)) return;
                        UpdateOccupiedPlayerColonySites(acceptedConfiguration);
                    }

                    Messages.Message(adminStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                    RequestWorldMapMarkerRefresh(ClashOfRimText.Key("ClashOfRim.Admin.WorldBaselineRefreshReason"));
                }));
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                if (!worldBaselineSubmission.IsCurrent(prepared)) return;
                adminStatus = ClashOfRimText.Key(
                    "ClashOfRim.Admin.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] World baseline update failed: " + ex);
            }
            finally
            {
                ClientSessionWork.TryApply(() =>
                {
                    adminInProgress = false;
                });
            }
        });
    }

    internal string BuildCurrentStorytellerDifficultySignature() => storytellerConfiguration.Capture().Signature;

    internal void SubmitRuntimeStorytellerSettingsIfChanged(string? initialSignature)
    {
        if (!IsInActiveMultiplayerSession || !isAdministrator)
        {
            return;
        }

        string currentSignature = BuildCurrentStorytellerDifficultySignature();
        if (!string.IsNullOrWhiteSpace(initialSignature)
            && string.Equals(initialSignature, currentSignature, StringComparison.Ordinal))
        {
            return;
        }

        StartSubmitRuntimeStorytellerSettings();
    }

    private void StartSubmitRuntimeStorytellerSettings()
    {
        if (!CanRunAdminRequest(out string failure))
        {
            Messages.Message(failure, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (adminInProgress)
        {
            return;
        }

        var capture = worldBaselineSubmission.Capture();
        ClientWorldBaselineCapture.Result captured;
        try
        {
            captured = worldBaselineCapture.Capture(capture, BuildCurrentColonyAppearanceDto(settings), storytellerOnly: true);
        }
        catch (Exception ex)
        {
            adminStatus = ClashOfRimText.Key(
                "ClashOfRim.Storyteller.AdminSyncFailed",
                ex.GetType().Name.Named("CODE"),
                ex.Message.Named("MESSAGE"));
            Log.Warning("[ClashOfRim] Storyteller/difficulty capture failed: " + ex);
            Messages.Message(adminStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (captured.Error is { } error)
        {
            adminStatus = ClashOfRimText.Key("ClashOfRim.Storyteller.AdminSyncFailed", error.Code.Named("CODE"), DescribeWorldCaptureFailure(error).Named("MESSAGE"));
            Messages.Message(adminStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        var prepared = captured.Prepared;
        if (prepared is null) return;
        var configuration = captured.Configuration!;

        adminInProgress = true;
        adminStatus = ClashOfRimText.Key("ClashOfRim.Storyteller.AdminSyncing");
        Messages.Message(adminStatus, MessageTypeDefOf.NeutralEvent, historical: false);
        ClientSessionWork.Run(async () =>
        {
            try
            {
                var result = await worldBaselineSubmission.SubmitAsync(prepared);
                if (result is null) return;
                using var publication = ClientSessionWork.EnterPublication();
                if (!worldBaselineSubmission.IsCurrent(prepared)) return;
                if (!result.Success || result.Response is null)
                {
                    adminStatus = ClashOfRimText.Key(
                        "ClashOfRim.Storyteller.AdminSyncFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    ClashOfRimGameComponent.EnqueueMainThreadAction(() => worldBaselineSubmission.TryApply(prepared, () =>
                        Messages.Message(adminStatus, MessageTypeDefOf.RejectInput, historical: false)));
                    return;
                }

                ModProtocolResponseDto? serverResult = result.Response.Result;
                if (serverResult is not null && !serverResult.Accepted)
                {
                    adminStatus = ClashOfRimText.Key(
                        "ClashOfRim.Storyteller.AdminSyncRejected",
                        serverResult.ErrorCode.Named("CODE"),
                        (serverResult.Message ?? string.Empty).Named("MESSAGE"));
                    ClashOfRimGameComponent.EnqueueMainThreadAction(() => worldBaselineSubmission.TryApply(prepared, () =>
                        Messages.Message(adminStatus, MessageTypeDefOf.RejectInput, historical: false)));
                    return;
                }

                ModWorldConfigurationDto? acceptedConfiguration = result.Response.WorldConfiguration;
                adminStatus = ClashOfRimText.Key(
                    "ClashOfRim.Storyteller.AdminSynced",
                    (acceptedConfiguration?.StorytellerDefName ?? configuration.StorytellerDefName ?? string.Empty).Named("STORYTELLER"),
                    (acceptedConfiguration?.DifficultyDefName ?? configuration.DifficultyDefName ?? string.Empty).Named("DIFFICULTY"));
                ClashOfRimGameComponent.EnqueueMainThreadAction(() => worldBaselineSubmission.Complete(prepared, () =>
                {
                    if (acceptedConfiguration is not null)
                    {
                        ApplyRuntimeWorldConfigurationUpdate(acceptedConfiguration);
                        if (!worldBaselineSubmission.IsCurrent(prepared)) return;
                        UpdateOccupiedPlayerColonySites(acceptedConfiguration);
                    }

                    Messages.Message(adminStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                }));
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                if (!worldBaselineSubmission.IsCurrent(prepared)) return;
                adminStatus = ClashOfRimText.Key(
                    "ClashOfRim.Storyteller.AdminSyncException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Storyteller/difficulty sync failed: " + ex);
                ClashOfRimGameComponent.EnqueueMainThreadAction(() => worldBaselineSubmission.TryApply(prepared, () =>
                    Messages.Message(adminStatus, MessageTypeDefOf.RejectInput, historical: false)));
            }
            finally
            {
                ClientSessionWork.TryApply(() =>
                {
                    adminInProgress = false;
                });
            }
        });
    }

    private void StartInitialColonySnapshotUpload()
    {
        if (!settings.IsConfigured)
        {
            snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.NotConfigured");
            Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (string.IsNullOrWhiteSpace(lastSessionId))
        {
            snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.Menu.UploadSnapshotNoSession");
            Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction(out var uploadTransaction))
        {
            snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.Menu.UploadSnapshotBusy");
            return;
        }

        snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.WorldBaseline.StatusUploadingInitialSnapshot");
        ClashLog.Message("[ClashOfRim] Uploading initial colony snapshot after server world baseline submit.");
        ClientSessionWork.Run(async () =>
        {
            try
            {
                var service = new ModSnapshotUploadService(settings, uploadTransaction);
                ModSnapshotUploadResult result = await ClientSessionWork.Observe(service.UploadConfiguredSnapshotAsync(
                    snapshotUploadKind: ModSnapshotUploadKinds.InitialColonySnapshot));
                using var publication = ClientSessionWork.EnterPublication();
                if (!result.Success)
                {
                    snapshotUploadStatus = ClashOfRimText.Key(
                        "ClashOfRim.WorldBaseline.StatusInitialSnapshotFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    Log.Warning("[ClashOfRim] Initial colony snapshot upload failed: " + snapshotUploadStatus);
                    ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                        Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false));
                    return;
                }

                snapshotUploadStatus = ClashOfRimText.Key(
                    "ClashOfRim.WorldBaseline.StatusInitialSnapshotUploaded",
                    (result.AcceptedSnapshotId ?? settings.CurrentSnapshotId).Named("SNAPSHOT"));
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    Messages.Message(snapshotUploadStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                    StartAutomaticEventRefresh(ClashOfRimText.Key("ClashOfRim.WorldBaseline.ReasonInitialSnapshotUploaded"));
                    _ = playerDirectory.RefreshAsync(manual: false);
                    StartRefreshChatMessages(initialLoad: true);
                    StartRegisterPlayerColonySites();
                    StartSyncWorldConfigurationExtensions();
                    if (!sessionConnection.IsRunning)
                    {
                        StartManualPresence();
                    }
                });
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                snapshotUploadStatus = ClashOfRimText.Key(
                    "ClashOfRim.WorldBaseline.StatusInitialSnapshotException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Initial colony snapshot upload exception: " + ex);
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    Messages.Message(snapshotUploadStatus, MessageTypeDefOf.RejectInput, historical: false));
            }
            finally
            {
                uploadTransaction.Dispose();
            }
        });
    }

    private async Task<ModSubmitAdminBaselineRequestDto> BuildAdminBaselineOnMainThreadAsync(
        IReadOnlyList<ModAdminBaselineExtensionRequirementDto>? extensionRequirements,
        ClientWorldBaselineSubmission.Prepared? prepared = null)
    {
        string userId = prepared?.Context.UserId ?? settings.UserId;
        var completion = new TaskCompletionSource<ModSubmitAdminBaselineRequestDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ClientSessionWork.Token.Register(() => completion.TrySetCanceled());
        EnqueueClashOfRimMainThreadAction(() =>
        {
            try
            {
                if (prepared is not null && !worldBaselineSubmission.IsCurrent(prepared)) { completion.TrySetCanceled(); return; }
                var baseline = BuildAdminBaseline(extensionRequirements, userId);
                if (prepared is null || worldBaselineSubmission.IsCurrent(prepared)) completion.TrySetResult(baseline);
                else completion.TrySetCanceled();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }, onDiscard: () => completion.TrySetCanceled());
        return await completion.Task;
    }

    private ModSubmitAdminBaselineRequestDto BuildAdminBaseline(
        IReadOnlyList<ModAdminBaselineExtensionRequirementDto>? extensionRequirements, string userId)
    {
        DateTimeOffset generatedAtUtc = DateTimeOffset.UtcNow;
        TrapClassificationUploadPackage trapPackage = TrapClassificationUploadBuilder.Build(
            TrapClassificationScanner.ScanLoadedThingDefs(),
            adminApprovedCandidateDefNames: null,
            generatedAtUtc,
            userId);

        ModSubmitAdminBaselineRequestDto baseline = new()
        {
            GeneratedAtUtc = generatedAtUtc.ToString("O")
        };
        baseline.StandardMarketValues.AddRange(ReadCurrentStandardMarketValues());
        baseline.StuffMarketValues.AddRange(ReadCurrentStuffMarketValues());
        baseline.QualityMarketValueModifiers.AddRange(ReadCurrentQualityMarketValueModifiers());
        baseline.PackableBuildings.AddRange(ReadCurrentPackableBuildings());
        baseline.Buildings.AddRange(ReadCurrentBuildingBaselines());
        ClashOfRimCompatibilityApi.AppendAdminBaselineExtensions(baseline, extensionRequirements);
        baseline.StuffHitPointModifiers.AddRange(ReadCurrentStuffHitPointModifiers());
        baseline.TrapClassifications.AddRange(trapPackage.Entries.Select(entry => new ModTrapClassificationDto
        {
            DefName = entry.DefName,
            ThingClass = entry.ThingClass,
            ModPackageId = entry.ModPackageId,
            ModName = entry.ModName,
            ScanStatus = entry.ScanStatus.ToString(),
            ScanReason = entry.ScanReason,
            InheritsBuildingTrap = entry.ScanStatus == TrapClassificationScanStatus.ApprovedByInheritance,
            AdminApproved = entry.AdminApproved
        }));

        return baseline;
    }

    private static List<ModPackableBuildingDto> ReadCurrentPackableBuildings()
    {
        return DefDatabase<ThingDef>.AllDefsListForReading
            .Where(def => def != null
                && !string.IsNullOrWhiteSpace(def.defName)
                && Trades.TradeThingReferenceUtility.IsPackableItemDef(def))
            .Select(def => new ModPackableBuildingDto
            {
                DefName = def.defName,
                MinifiedDefName = def.minifiedDef?.defName,
                Label = def.label,
                ModPackageId = def.modContentPack?.PackageId,
                ModName = def.modContentPack?.Name
            })
            .OrderBy(entry => entry.ModPackageId, StringComparer.Ordinal)
            .ThenBy(entry => entry.DefName, StringComparer.Ordinal)
            .ToList();
    }

    private static List<ModBuildingBaselineDto> ReadCurrentBuildingBaselines()
    {
        return DefDatabase<ThingDef>.AllDefsListForReading
            .Where(def => def != null
                && !string.IsNullOrWhiteSpace(def.defName)
                && def.category == ThingCategory.Building)
            .Select(def => new ModBuildingBaselineDto
            {
                DefName = def.defName,
                Label = def.label,
                ModPackageId = def.modContentPack?.PackageId,
                ModName = def.modContentPack?.Name,
                UseHitPoints = def.useHitPoints,
                EstimatedMaxHitPoints = ReadEstimatedMaxHitPoints(def),
                Minifiable = def.Minifiable,
                MinifiedDefName = def.minifiedDef?.defName
            })
            .OrderBy(entry => entry.ModPackageId, StringComparer.Ordinal)
            .ThenBy(entry => entry.DefName, StringComparer.Ordinal)
            .ToList();
    }

    private static List<ModStuffHitPointModifierDto> ReadCurrentStuffHitPointModifiers()
    {
        return DefDatabase<ThingDef>.AllDefsListForReading
            .Where(def => def != null
                && !string.IsNullOrWhiteSpace(def.defName)
                && def.IsStuff)
            .Select(def => new ModStuffHitPointModifierDto
            {
                DefName = def.defName,
                Label = def.label,
                ModPackageId = def.modContentPack?.PackageId,
                ModName = def.modContentPack?.Name,
                MaxHitPointsFactor = ReadStuffStatModifier(def, "statFactors", StatDefOf.MaxHitPoints, defaultValue: 1f),
                MaxHitPointsOffset = ReadStuffStatModifier(def, "statOffsets", StatDefOf.MaxHitPoints, defaultValue: 0f)
            })
            .Where(entry => entry.MaxHitPointsFactor != 1f || entry.MaxHitPointsOffset != 0f)
            .OrderBy(entry => entry.ModPackageId, StringComparer.Ordinal)
            .ThenBy(entry => entry.DefName, StringComparer.Ordinal)
            .ToList();
    }

    private static float ReadStuffStatModifier(ThingDef stuff, string memberName, StatDef statDef, float defaultValue)
    {
        object? modifiers = ReadMember(stuff.stuffProps, memberName);
        if (modifiers is not System.Collections.IEnumerable enumerable)
        {
            return defaultValue;
        }

        foreach (object? modifier in enumerable)
        {
            object? modifierStat = ReadMember(modifier, "stat");
            string? defName = ReadDefName(modifierStat);
            if (!string.Equals(defName, statDef.defName, StringComparison.Ordinal))
            {
                continue;
            }

            object? value = ReadMember(modifier, "value");
            if (value is not null
                && float.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float parsed))
            {
                return parsed;
            }
        }

        return defaultValue;
    }

    private static List<ModStandardMarketValueDto> ReadCurrentStandardMarketValues()
    {
        return DefDatabase<ThingDef>.AllDefsListForReading
            .Where(def => def != null
                && !string.IsNullOrWhiteSpace(def.defName))
            .Select(def => new ModStandardMarketValueDto
            {
                DefName = def.defName,
                MarketValue = ReadThingDefMarketValue(def)
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.DefName) && entry.MarketValue >= 0f)
            .OrderBy(entry => entry.DefName, StringComparer.Ordinal)
            .ToList();
    }

    private static List<ModStuffMarketValueDto> ReadCurrentStuffMarketValues()
    {
        List<ThingDef> stuffs = DefDatabase<ThingDef>.AllDefsListForReading
            .Where(def => def != null && !string.IsNullOrWhiteSpace(def.defName) && def.IsStuff)
            .ToList();

        var values = new List<ModStuffMarketValueDto>();
        foreach (ThingDef thingDef in DefDatabase<ThingDef>.AllDefsListForReading
            .Where(def => def != null
                && !string.IsNullOrWhiteSpace(def.defName)
                && def.category != ThingCategory.Pawn
                && def.MadeFromStuff))
        {
            float baseValue = ReadThingDefMarketValue(thingDef);
            foreach (ThingDef stuff in stuffs.Where(stuff => stuff.stuffProps?.CanMake(thingDef) == true))
            {
                float stuffValue = ReadThingDefMarketValue(thingDef, stuff);
                if (stuffValue < 0f || Mathf.Approximately(stuffValue, baseValue))
                {
                    continue;
                }

                values.Add(new ModStuffMarketValueDto
                {
                    ThingDefName = thingDef.defName,
                    StuffDefName = stuff.defName,
                    MarketValue = stuffValue
                });
            }
        }

        return values
            .OrderBy(entry => entry.ThingDefName, StringComparer.Ordinal)
            .ThenBy(entry => entry.StuffDefName, StringComparer.Ordinal)
            .ToList();
    }

    private static List<ModQualityMarketValueModifierDto> ReadCurrentQualityMarketValueModifiers()
    {
        object? parts = ReadMember(StatDefOf.MarketValue, "parts");
        if (parts is not System.Collections.IEnumerable enumerable)
        {
            return new List<ModQualityMarketValueModifierDto>();
        }

        foreach (object? part in enumerable)
        {
            if (part is not StatPart_Quality)
            {
                continue;
            }

            return Enum.GetValues(typeof(QualityCategory))
                .Cast<QualityCategory>()
                .Select(quality => new ModQualityMarketValueModifierDto
                {
                    Quality = quality.ToString(),
                    Factor = ReadQualityFloat(part, "factor" + quality, 1f),
                    MaxGain = ReadQualityFloat(part, "maxGain" + quality, 9999999f)
                })
                .ToList();
        }

        return new List<ModQualityMarketValueModifierDto>();
    }

    private static float ReadQualityFloat(object source, string memberName, float defaultValue)
    {
        object? value = ReadMember(source, memberName);
        if (value is not null
            && float.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static float ReadThingDefMarketValue(ThingDef def)
    {
        return ReadThingDefMarketValue(def, stuff: null);
    }

    private static float ReadThingDefMarketValue(ThingDef def, ThingDef? stuff)
    {
        try
        {
            return Mathf.Max(0f, def.GetStatValueAbstract(StatDefOf.MarketValue, stuff));
        }
        catch
        {
            object? baseMarketValue = ReadMember(def, "BaseMarketValue");
            if (baseMarketValue is not null
                && float.TryParse(
                    Convert.ToString(baseMarketValue, CultureInfo.InvariantCulture),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float parsed))
            {
                return Mathf.Max(0f, parsed);
            }
        }

        return 0f;
    }

    private static int ReadEstimatedMaxHitPoints(ThingDef def)
    {
        if (!def.useHitPoints)
        {
            return 0;
        }

        try
        {
            return Math.Max(1, Mathf.RoundToInt(def.GetStatValueAbstract(StatDefOf.MaxHitPoints)));
        }
        catch
        {
            return Math.Max(1, def.BaseMaxHitPoints);
        }
    }

    private void ApplyWorldBaseline(ModWorldConfigurationDto configuration)
        => worldConfiguration.Apply(configuration);

    private void ApplyServerWorldConfigurationExtensionCatalog(ModWorldConfigurationDto? configuration)
        => worldConfiguration.ApplyCatalog(configuration);

    private void ApplyRuntimeWorldConfigurationUpdate(ModWorldConfigurationDto configuration)
        => worldConfiguration.ApplyCatalog(configuration, applyStoryteller: true);

    internal bool TryApplyServerStorytellerBaselineAndContinue(Window storytellerPage)
        => storytellerConfiguration.ContinueSetup(new ClientStorytellerPage(storytellerPage));

    internal void LockMultiplayerCommitmentModeIfNeeded() => storytellerConfiguration.LockCommitment();

    internal static void DebugLogFlow(string stage, string? detail = null)
    {
        string suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : " | " + detail;
        ClashLog.Message("[ClashOfRim.Flow] " + stage + suffix);
    }

    private static string DescribeWorldCaptureFailure(ClientWorldBaselineCapture.Failure failure)
        => failure.MessageKey is { } key ? ClashOfRimText.Key(key) : failure.Detail ?? "Unknown";

    private static string DescribeWorldConfiguration(ModWorldConfigurationDto? configuration)
    {
        if (configuration is null)
        {
            return "worldConfig=<null>";
        }

        string extensionSummary = FormatWorldConfigurationExtensionSummaryForLog(configuration);
        return $"worldConfig={configuration.WorldConfigurationId}, seed={configuration.SeedString ?? "<null>"}, coverage={configuration.PlanetCoverage ?? "<null>"}, rainfall={configuration.OverallRainfall ?? "<null>"}, temperature={configuration.OverallTemperature ?? "<null>"}, population={configuration.OverallPopulation ?? "<null>"}, landmarkDensity={configuration.LandmarkDensity ?? "<null>"}, generationPollution={ReadWorldGenerationPollution(configuration).ToString(CultureInfo.InvariantCulture)}, tileCount={configuration.TileCount ?? "<null>"}, factionDefs={configuration.FactionDefNames.Count}, factions={configuration.Factions.Count}, features={configuration.Features.Count}, roads={configuration.Roads.Count}, worldObjects={configuration.WorldObjects.Count}, playerSites={configuration.PlayerColonySites.Count}, worldExtensions={extensionSummary}, storyteller={configuration.StorytellerDefName ?? "<null>"}, difficulty={configuration.DifficultyDefName ?? "<null>"}";
    }

    private static float ReadWorldGenerationPollution(ModWorldConfigurationDto configuration)
    {
        return ClashOfRimCompatibilityApi.ResolveWorldGenerationFloatSetting(configuration, "pollution", 0f);
    }

    private static string FormatWorldConfigurationExtensionSummaryForLog(ModWorldConfigurationDto configuration)
    {
        IReadOnlyList<WorldConfigurationExtensionSummaryItem> items =
            ClashOfRimCompatibilityApi.GetWorldConfigurationExtensionSummary(configuration);
        return items.Count == 0
            ? "<none>"
            : string.Join(",", items.Select(item => item.Key + "=" + item.Value));
    }

    private static string DescribeCurrentWorldState()
    {
        try
        {
            object? world = Find.World;
            object? worldInfo = ReadMember(world, "info") ?? ReadMember(world, "Info");
            int factionCount = Find.World?.factionManager?.AllFactionsListForReading?.Count ?? -1;
            int objectCount = Find.WorldObjects?.AllWorldObjects?.Count ?? -1;
            int featureCount = Find.WorldFeatures?.features?.Count ?? -1;
            int tileCount = Find.WorldGrid?.TilesCount ?? -1;
            return $"seed={ReadFirstString(worldInfo, "seedString", "SeedString", "seed") ?? "<null>"}, coverage={ReadFirstString(worldInfo, "planetCoverage", "PlanetCoverage") ?? "<null>"}, rainfall={ReadFirstString(worldInfo, "overallRainfall", "OverallRainfall") ?? "<null>"}, temperature={ReadFirstString(worldInfo, "overallTemperature", "OverallTemperature") ?? "<null>"}, population={ReadFirstString(worldInfo, "overallPopulation", "OverallPopulation") ?? "<null>"}, landmarkDensity={ReadFirstString(worldInfo, "landmarkDensity", "LandmarkDensity") ?? "<null>"}, tileCount={tileCount}, factions={factionCount}, features={featureCount}, worldObjects={objectCount}";
        }
        catch (Exception ex)
        {
            return "failed:" + ex.GetType().Name + " " + ex.Message;
        }
    }

    private static string DescribeWorldMapMarkerDelivery(ModWorldMapMarkerDeliveryDto? delivery)
    {
        if (delivery is null)
        {
            return "delivery=<null>";
        }

        int tradeable = delivery.Markers.Count(marker => string.Equals(marker.Kind, "TradeableColony", StringComparison.Ordinal));
        int runtime = delivery.Markers.Count(marker => marker.Kind is "RuntimeCaravan" or "RuntimeShuttle" or "RuntimeTransportPod" or "RuntimeWorldObject");
        int raid = delivery.Markers.Count(marker => string.Equals(marker.Kind, "ActiveRaidTarget", StringComparison.Ordinal));
        return $"deliveryUser={delivery.UserId}, generatedAt={delivery.GeneratedAtUtc}, markers={delivery.Markers.Count}, tradeable={tradeable}, runtime={runtime}, raid={raid}";
    }

    private static string DescribeWorldMapMarkerSample(IReadOnlyList<ModWorldMapMarkerDto> markers)
    {
        if (markers.Count == 0)
        {
            return "sample=<empty>";
        }

        return "sample=" + string.Join(" | ", markers
            .OrderBy(marker => marker.Tile)
            .ThenBy(marker => marker.MarkerId, StringComparer.Ordinal)
            .Take(5)
            .Select(marker => $"id={marker.MarkerId}, kind={marker.Kind}, owner={marker.OwnerUserId}, colony={marker.OwnerColonyId}, tile={marker.Tile}, label={marker.Label}, trade={marker.CanTrade}, raid={marker.CanRaid}, reinforce={marker.CanReinforce}, appearance={DescribeAppearance(marker.Appearance)}"));
    }

    private static string DescribePlayerColonySiteSample(IReadOnlyList<ModPlayerColonySiteDto> sites)
    {
        if (sites.Count == 0)
        {
            return "<empty>";
        }

        return string.Join(" | ", sites
            .OrderBy(site => site.Tile)
            .ThenBy(site => site.UserId, StringComparer.Ordinal)
            .Take(5)
            .Select(site => $"owner={site.UserId}, colony={site.ColonyId}, tile={site.Tile}, worldObject={site.WorldObjectId}, map={site.MapUniqueId}, label={site.Label}, appearance={DescribeAppearance(site.Appearance)}"));
    }

    private static string DescribeAppearance(ModColonyAppearanceDto? appearance)
    {
        if (appearance is null)
        {
            return "<none>";
        }

        return (appearance.Mode ?? string.Empty)
            + "/"
            + (appearance.IconDefName ?? string.Empty)
            + "/"
            + (appearance.ColorDefName ?? appearance.ColorHex ?? string.Empty);
    }

    internal static string DescribeWindow(Window? window)
    {
        if (window is null)
        {
            return "<null>";
        }

        string result = window.GetType().Name;
        if (window is Page page)
        {
            result += $"(prev={page.prev?.GetType().Name ?? "<null>"}, next={page.next?.GetType().Name ?? "<null>"}, nextAct={DescribeAction(page.nextAct)})";
        }

        result += $"[absorb={window.absorbInputAroundWindow}, preventCamera={window.preventCameraMotion}, layer={window.layer}]";
        return result;
    }

    internal static string DescribeWindowStack()
    {
        if (Find.WindowStack is null)
        {
            return "WindowStack=<null>";
        }

        List<string> windows = new();
        for (int i = 0; i < Find.WindowStack.Count; i++)
        {
            windows.Add(i + ":" + DescribeWindow(Find.WindowStack[i]));
        }

        return "WindowStack.Count=" + Find.WindowStack.Count + " [" + string.Join("; ", windows) + "]";
    }

    internal static string DescribeAction(Action? action)
    {
        return action is null
            ? "<null>"
            : action.Method.DeclaringType?.Name + "." + action.Method.Name;
    }

    private static List<ModRuntimeWorldObjectMarkerDto> ReadCurrentRuntimeWorldObjects()
    {
        var markers = new List<ModRuntimeWorldObjectMarkerDto>();
        if (Find.WorldObjects?.AllWorldObjects is null)
        {
            return markers;
        }

        foreach (WorldObject worldObject in Find.WorldObjects.AllWorldObjects)
        {
            if (!IsRuntimeWorldObjectForSync(worldObject) || !IsValidTile(worldObject.Tile))
            {
                continue;
            }

            string? worldObjectId = ReadWorldObjectId(worldObject, worldObject.Tile);
            if (string.IsNullOrWhiteSpace(worldObjectId))
            {
                continue;
            }

            markers.Add(new ModRuntimeWorldObjectMarkerDto
            {
                WorldObjectId = worldObjectId!,
                DefName = worldObject.def?.defName,
                Kind = ResolveRuntimeWorldObjectKind(worldObject),
                Tile = worldObject.Tile,
                TileLayerId = ReadPlanetTileLayerId(worldObject.Tile),
                Label = worldObject.Label,
                PathTiles = ReadRuntimeWorldObjectPathTiles(worldObject)
            });
        }

        return markers;
    }

    private static List<int>? ReadRuntimeWorldObjectPathTiles(WorldObject worldObject)
    {
        if (worldObject is not Caravan caravan
            || caravan.pather is null
            || !caravan.pather.Moving
            || caravan.pather.curPath is null
            || !caravan.pather.curPath.Found
            || caravan.pather.curPath.NodesLeftCount < 2)
        {
            return null;
        }

        var pathTiles = new List<int>(caravan.pather.curPath.NodesLeftCount);
        for (int i = 0; i < caravan.pather.curPath.NodesLeftCount; i++)
        {
            PlanetTile tile = caravan.pather.curPath.Peek(i);
            if (IsValidTile(tile))
            {
                pathTiles.Add(tile);
            }
        }

        return pathTiles.Count >= 2 ? pathTiles : null;
    }

    private static bool IsRuntimeWorldObjectForSync(WorldObject worldObject)
    {
        if (worldObject is null
            || worldObject is RemoteRuntimeWorldObject
            || worldObject is RemoteColonyMapParent
            || worldObject is RemoteSessionMapParent
            || worldObject is Settlement
            || worldObject.def is null
            || worldObject.Faction != Faction.OfPlayer)
        {
            return false;
        }

        string className = worldObject.GetType().Name;
        string defName = worldObject.def.defName ?? string.Empty;
        return worldObject is Caravan
            || ContainsRuntimeWorldObjectToken(className)
            || ContainsRuntimeWorldObjectToken(defName);
    }

    private static string ResolveRuntimeWorldObjectKind(WorldObject worldObject)
    {
        string className = worldObject.GetType().Name;
        string defName = worldObject.def?.defName ?? string.Empty;
        if (worldObject is Caravan || ContainsToken(className, "Caravan") || ContainsToken(defName, "Caravan"))
        {
            return "Caravan";
        }

        if (ContainsToken(className, "Shuttle") || ContainsToken(defName, "Shuttle"))
        {
            return "Shuttle";
        }

        if (ContainsToken(className, "TransportPod") || ContainsToken(defName, "TransportPod"))
        {
            return "TransportPod";
        }

        return "WorldObject";
    }

    private static bool ContainsRuntimeWorldObjectToken(string value)
    {
        return ContainsToken(value, "Caravan")
            || ContainsToken(value, "Shuttle")
            || ContainsToken(value, "TransportPod")
            || ContainsToken(value, "Traveling");
    }

    private static bool ContainsToken(string value, string token)
    {
        return value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string BuildPlayerColonySiteSignature(
        IEnumerable<ModPlayerColonySiteDto> sites,
        IEnumerable<ModWorldConfigurationExtensionDto>? extensions = null)
    {
        string siteSignature = string.Join("|", sites
            .OrderBy(site => site.Tile)
            .ThenBy(site => site.WorldObjectId, StringComparer.Ordinal)
            .ThenBy(site => site.MapUniqueId, StringComparer.Ordinal)
            .Select(site =>
                (site.UserId ?? string.Empty) + "\u001f"
                + (site.ColonyId ?? string.Empty) + "\u001f"
                + (site.WorldObjectId ?? string.Empty) + "\u001f"
                + (site.MapUniqueId ?? string.Empty) + "\u001f"
                + site.Tile.ToString(CultureInfo.InvariantCulture) + "\u001f"
                + site.TileLayerId.ToString(CultureInfo.InvariantCulture) + "\u001f"
                + (site.Label ?? string.Empty) + "\u001f"
                + (site.FactionName ?? string.Empty) + "\u001f"
                + BuildColonyAppearanceSignature(site.Appearance)));
        return siteSignature + "\u001e" + BuildWorldConfigurationExtensionSignature(
            extensions ?? Enumerable.Empty<ModWorldConfigurationExtensionDto>());
    }

    private static ModColonyAppearanceDto? BuildCurrentColonyAppearanceDto(ClashOfRimSettings settings)
    {
        ColonyAppearanceSelection appearance = ReadCurrentColonyAppearanceSelection(settings);
        if (!appearance.HasAny)
        {
            return null;
        }

        return new ModColonyAppearanceDto
        {
            Mode = NullIfWhiteSpace(appearance.Mode),
            IconDefName = NullIfWhiteSpace(appearance.IconDefName),
            ColorDefName = NullIfWhiteSpace(appearance.ColorDefName),
            ColorHex = NullIfWhiteSpace(appearance.ColorHex)
        };
    }

    private static string BuildColonyAppearanceSignature(ModColonyAppearanceDto? appearance)
    {
        if (appearance is null)
        {
            return string.Empty;
        }

        return (appearance.Mode ?? string.Empty) + "\u001d"
            + (appearance.IconDefName ?? string.Empty) + "\u001d"
            + (appearance.ColorDefName ?? string.Empty) + "\u001d"
            + (appearance.ColorHex ?? string.Empty);
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string BuildWorldConfigurationExtensionSignature(IEnumerable<ModWorldConfigurationExtensionDto> extensions)
    {
        return string.Join("|", extensions
            .OrderBy(extension => extension.ProviderId, StringComparer.Ordinal)
            .ThenBy(extension => extension.Kind, StringComparer.Ordinal)
            .Select(extension =>
                (extension.ProviderId ?? string.Empty) + "\u001f"
                + (extension.Kind ?? string.Empty) + "\u001f"
                + (extension.SchemaVersion ?? string.Empty) + "\u001f"
                + (extension.PayloadJson ?? string.Empty)));
    }

    private static bool HasLoadedPlayerColonyContext()
    {
        if (Find.CurrentMap is not null)
        {
            return true;
        }

        return Find.Maps?.Any(IsPlayerColonyMap) == true;
    }

    private static string DescribePlayerColonyScanContext()
    {
        int maps = Find.Maps?.Count ?? -1;
        int worldObjects = Find.WorldObjects?.AllWorldObjects?.Count ?? -1;
        string currentMap = Find.CurrentMap is null
            ? "<null>"
            : "Map_" + Find.CurrentMap.uniqueID.ToString(CultureInfo.InvariantCulture);
        string playerWorldObjectSample = string.Join(
            "; ",
            (Find.WorldObjects?.AllWorldObjects ?? Enumerable.Empty<WorldObject>())
            .Where(IsPlayerColonyWorldObject)
            .Take(5)
            .Select(worldObject =>
                ReadFirstString(ReadMember(worldObject, "def"), "defName", "Name")
                + "@"
                + ReadFirstInt(worldObject, "Tile", "tile")?.ToString(CultureInfo.InvariantCulture)));

        return "context=programState:"
            + Current.ProgramState
            + ", maps:"
            + maps.ToString(CultureInfo.InvariantCulture)
            + ", currentMap:"
            + currentMap
            + ", worldObjects:"
            + worldObjects.ToString(CultureInfo.InvariantCulture)
            + ", playerWorldObjects:"
            + (string.IsNullOrWhiteSpace(playerWorldObjectSample) ? "<empty>" : playerWorldObjectSample);
    }

}
