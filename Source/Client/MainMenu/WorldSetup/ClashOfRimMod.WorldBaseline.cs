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
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Profile;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal bool TryGenerateWorldFromServerConfiguration(Window createWorldPage)
    {
        DebugLogFlow(
            "TryGenerateWorldFromServerConfiguration.Enter",
            $"pending={pendingServerWorldConfiguration is not null}, page={DescribeWindow(createWorldPage)}, stack={DescribeWindowStack()}");
        if (pendingServerWorldConfiguration is null)
        {
            DebugLogFlow("TryGenerateWorldFromServerConfiguration.Skip", "no pending server world configuration");
            return false;
        }

        ModWorldConfigurationDto configuration = pendingServerWorldConfiguration;
        pendingServerWorldConfiguration = null;
        Page? nextPage = createWorldPage is Page page ? page.next : null;
        Action? nextAct = createWorldPage is Page pageWithNextAct ? pageWithNextAct.nextAct : null;
        if (nextPage is not null)
        {
            nextPage.prev = null;
        }

        DebugLogFlow(
            "TryGenerateWorldFromServerConfiguration.Consume",
            $"configuration={configuration.WorldConfigurationId}, seed={configuration.SeedString}, nextPage={DescribeWindow(nextPage)}, nextAct={DescribeAction(nextAct)}");
        UpdateOccupiedPlayerColonySites(configuration);
        createWorldPage.Close();
        DebugLogFlow("TryGenerateWorldFromServerConfiguration.PageClosed", DescribeWindowStack());
        GenerateWorldFromServerConfiguration(configuration, nextPage, nextAct);
        return true;
    }

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

    private void RememberWorldConfigurationIdentity(ModWorldConfigurationDto? configuration)
    {
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.WorldConfigurationId))
        {
            return;
        }

        string worldConfigurationId = configuration.WorldConfigurationId.Trim();
        if (string.Equals(settings.CurrentWorldConfigurationId, worldConfigurationId, StringComparison.Ordinal))
        {
            return;
        }

        settings.CurrentWorldConfigurationId = worldConfigurationId;
        settings.Write();
    }

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
        if (!pendingInitialWorldConfigurationSubmit)
        {
            return;
        }

        pendingInitialWorldConfigurationSubmit = false;
        if (!settings.IsConfigured)
        {
            loginStatus = ClashOfRimText.Key("ClashOfRim.WorldBaseline.StatusConfigMissing");
            return;
        }

        ModWorldConfigurationDto configuration = BuildCurrentWorldConfiguration();
        if (!IsUsableWorldGenerationBaseline(configuration, out string baselineFailureReason))
        {
            loginStatus = ClashOfRimText.Key("ClashOfRim.WorldBaseline.StatusInvalid", baselineFailureReason.Named("REASON"));
            Log.Warning("[ClashOfRim] " + loginStatus);
            EnqueueClashOfRimMainThreadAction(() =>
                Messages.Message(loginStatus, MessageTypeDefOf.RejectInput, historical: false));
            return;
        }

        loginStatus = ClashOfRimText.Key("ClashOfRim.WorldBaseline.StatusSubmitting");
        ClashLog.Message("[ClashOfRim] Submitting initial world configuration: " + DescribeWorldConfiguration(configuration));
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModSubmitWorldConfigurationResponseDto> result =
                    await SubmitWorldConfigurationWithDetachedGeometryAsync(client, configuration, "initial world configuration");
                if (!result.Success || result.Response?.Result?.Accepted != true)
                {
                    loginStatus = ClashOfRimText.Key(
                        "ClashOfRim.WorldBaseline.StatusWorldConfigFailed",
                        (result.ErrorCode ?? result.Response?.Result?.ErrorCode.ToString() ?? string.Empty).Named("CODE"),
                        (result.Message ?? result.Response?.Result?.Message ?? string.Empty).Named("MESSAGE"));
                    Log.Warning("[ClashOfRim] Initial world configuration rejected: " + loginStatus);
                    return;
                }

                ClashLog.Message("[ClashOfRim] Initial world configuration accepted by server: "
                    + DescribeWorldConfiguration(result.Response.WorldConfiguration));

                ClashOfRimClientNetworkResult<ModGetAdminBaselineRequirementsResponseDto> requirementsResult =
                    await client.GetAdminBaselineRequirementsAsync();
                if (!requirementsResult.Success || requirementsResult.Response?.Result?.Accepted != true)
                {
                    loginStatus = ClashOfRimText.Key(
                        "ClashOfRim.WorldBaseline.StatusAdminBaselineFailed",
                        (requirementsResult.ErrorCode ?? requirementsResult.Response?.Result?.ErrorCode.ToString() ?? string.Empty).Named("CODE"),
                        (requirementsResult.Message ?? requirementsResult.Response?.Result?.Message ?? string.Empty).Named("MESSAGE"));
                    Log.Warning("[ClashOfRim] Admin baseline requirements rejected after world configuration submit: " + loginStatus);
                    EnqueueClashOfRimMainThreadAction(() =>
                        Messages.Message(loginStatus, MessageTypeDefOf.RejectInput, historical: false));
                    return;
                }

                ModSubmitAdminBaselineRequestDto adminBaseline = await BuildAdminBaselineOnMainThreadAsync(
                    requirementsResult.Response.BaselineExtensions);
                ClashOfRimClientNetworkResult<ModSubmitAdminBaselineResponseDto> baselineResult =
                    await client.SubmitAdminBaselineAsync(adminBaseline);
                if (!baselineResult.Success || baselineResult.Response?.Result?.Accepted != true)
                {
                    loginStatus = ClashOfRimText.Key(
                        "ClashOfRim.WorldBaseline.StatusAdminBaselineFailed",
                        (baselineResult.ErrorCode ?? baselineResult.Response?.Result?.ErrorCode.ToString() ?? string.Empty).Named("CODE"),
                        (baselineResult.Message ?? baselineResult.Response?.Result?.Message ?? string.Empty).Named("MESSAGE"));
                    Log.Warning("[ClashOfRim] Admin baseline rejected after world configuration submit: " + loginStatus);
                    EnqueueClashOfRimMainThreadAction(() =>
                        Messages.Message(loginStatus, MessageTypeDefOf.RejectInput, historical: false));
                    return;
                }

                ModSubmitAdminBaselineResponseDto baselineResponse = baselineResult.Response;
                loginStatus = ClashOfRimText.Key(
                    "ClashOfRim.WorldBaseline.StatusSubmitted",
                    baselineResponse.StandardMarketValueCount.Named("PRICECOUNT"),
                    baselineResponse.TrapAutoApprovedCount.Named("TRAPAUTO"),
                    baselineResponse.TrapCandidateCount.Named("TRAPCANDIDATE"));
                EnqueueClashOfRimMainThreadAction(() =>
                {
                    Messages.Message(loginStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                    RequestWorldMapMarkerRefresh(ClashOfRimText.Key("ClashOfRim.WorldBaseline.ReasonSubmitted"));
                    StartInitialColonySnapshotUpload();
                });
            }
            catch (Exception ex)
            {
                loginStatus = ClashOfRimText.Key("ClashOfRim.WorldBaseline.StatusSubmitException", ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Initial world configuration submit exception: " + ex);
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

        ModWorldConfigurationDto configuration;
        try
        {
            configuration = BuildCurrentWorldConfiguration();
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

        if (!IsUsableWorldGenerationBaseline(configuration, out string baselineFailureReason))
        {
            adminStatus = ClashOfRimText.Key(
                "ClashOfRim.Admin.WorldBaselineInvalid",
                baselineFailureReason.Named("REASON"));
            Messages.Message(adminStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        adminInProgress = true;
        adminStatus = ClashOfRimText.Key("ClashOfRim.Admin.WorldBaselineUpdating");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModSubmitWorldConfigurationResponseDto> result =
                    await SubmitWorldConfigurationWithDetachedGeometryAsync(client, configuration, "admin world baseline update");
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
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (acceptedConfiguration is not null)
                    {
                        ApplyWorldBaseline(acceptedConfiguration);
                        UpdateOccupiedPlayerColonySites(acceptedConfiguration);
                    }

                    Messages.Message(adminStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                    RequestWorldMapMarkerRefresh(ClashOfRimText.Key("ClashOfRim.Admin.WorldBaselineRefreshReason"));
                });
            }
            catch (Exception ex)
            {
                adminStatus = ClashOfRimText.Key(
                    "ClashOfRim.Admin.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] World baseline update failed: " + ex);
            }
            finally
            {
                adminInProgress = false;
            }
        });
    }

    internal string BuildCurrentStorytellerDifficultySignature()
    {
        return string.Join(
            "\u001f",
            ReadCurrentStorytellerDefName() ?? string.Empty,
            ReadCurrentDifficultyDefName() ?? string.Empty,
            ReadCurrentDifficultyValuesXml() ?? string.Empty);
    }

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

        ModWorldConfigurationDto configuration;
        try
        {
            configuration = BuildCurrentWorldConfiguration();
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

        if (string.IsNullOrWhiteSpace(configuration.StorytellerDefName)
            || string.IsNullOrWhiteSpace(configuration.DifficultyDefName))
        {
            adminStatus = ClashOfRimText.Key(
                "ClashOfRim.Storyteller.AdminSyncFailed",
                "MissingStoryteller".Named("CODE"),
                "storyteller or difficulty is empty".Named("MESSAGE"));
            Messages.Message(adminStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        adminInProgress = true;
        adminStatus = ClashOfRimText.Key("ClashOfRim.Storyteller.AdminSyncing");
        Messages.Message(adminStatus, MessageTypeDefOf.NeutralEvent, historical: false);
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModSubmitWorldConfigurationResponseDto> result =
                    await SubmitWorldConfigurationWithDetachedGeometryAsync(client, configuration, "storyteller baseline sync");
                if (!result.Success || result.Response is null)
                {
                    adminStatus = ClashOfRimText.Key(
                        "ClashOfRim.Storyteller.AdminSyncFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                        Messages.Message(adminStatus, MessageTypeDefOf.RejectInput, historical: false));
                    return;
                }

                ModProtocolResponseDto? serverResult = result.Response.Result;
                if (serverResult is not null && !serverResult.Accepted)
                {
                    adminStatus = ClashOfRimText.Key(
                        "ClashOfRim.Storyteller.AdminSyncRejected",
                        serverResult.ErrorCode.Named("CODE"),
                        (serverResult.Message ?? string.Empty).Named("MESSAGE"));
                    ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                        Messages.Message(adminStatus, MessageTypeDefOf.RejectInput, historical: false));
                    return;
                }

                ModWorldConfigurationDto? acceptedConfiguration = result.Response.WorldConfiguration;
                adminStatus = ClashOfRimText.Key(
                    "ClashOfRim.Storyteller.AdminSynced",
                    (acceptedConfiguration?.StorytellerDefName ?? configuration.StorytellerDefName ?? string.Empty).Named("STORYTELLER"),
                    (acceptedConfiguration?.DifficultyDefName ?? configuration.DifficultyDefName ?? string.Empty).Named("DIFFICULTY"));
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (acceptedConfiguration is not null)
                    {
                        ApplyRuntimeWorldConfigurationUpdate(acceptedConfiguration);
                        UpdateOccupiedPlayerColonySites(acceptedConfiguration);
                    }

                    Messages.Message(adminStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                });
            }
            catch (Exception ex)
            {
                adminStatus = ClashOfRimText.Key(
                    "ClashOfRim.Storyteller.AdminSyncException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Storyteller/difficulty sync failed: " + ex);
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    Messages.Message(adminStatus, MessageTypeDefOf.RejectInput, historical: false));
            }
            finally
            {
                adminInProgress = false;
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

        if (!TryBeginSnapshotUploadTransaction())
        {
            snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.Menu.UploadSnapshotBusy");
            return;
        }

        snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.WorldBaseline.StatusUploadingInitialSnapshot");
        ClashLog.Message("[ClashOfRim] Uploading initial colony snapshot after server world baseline submit.");
        Task.Run(async () =>
        {
            try
            {
                var service = new ModSnapshotUploadService(settings);
                ModSnapshotUploadResult result = await service.UploadConfiguredSnapshotAsync(
                    snapshotUploadKind: ModSnapshotUploadKinds.InitialColonySnapshot);
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
                    StartRefreshPlayers(ClashOfRimText.Key("ClashOfRim.WorldBaseline.ReasonInitialSnapshotUploaded"), requireManualGate: false);
                    StartRefreshChatMessages(initialLoad: true);
                    StartRegisterPlayerColonySites();
                    StartSyncWorldConfigurationExtensions();
                    if (!presenceInProgress)
                    {
                        StartManualPresence();
                    }
                });
            }
            catch (Exception ex)
            {
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
                EndSnapshotUploadTransaction();
            }
        });
    }

    private Task<ModSubmitAdminBaselineRequestDto> BuildAdminBaselineOnMainThreadAsync(
        IReadOnlyList<ModAdminBaselineExtensionRequirementDto>? extensionRequirements)
    {
        var completion = new TaskCompletionSource<ModSubmitAdminBaselineRequestDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        EnqueueClashOfRimMainThreadAction(() =>
        {
            try
            {
                completion.SetResult(BuildAdminBaseline(extensionRequirements));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    private ModSubmitAdminBaselineRequestDto BuildAdminBaseline(
        IReadOnlyList<ModAdminBaselineExtensionRequirementDto>? extensionRequirements)
    {
        DateTimeOffset generatedAtUtc = DateTimeOffset.UtcNow;
        TrapClassificationUploadPackage trapPackage = TrapClassificationUploadBuilder.Build(
            TrapClassificationScanner.ScanLoadedThingDefs(),
            adminApprovedCandidateDefNames: null,
            generatedAtUtc,
            settings.UserId);

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
                && def.category == ThingCategory.Building
                && def.Minifiable
                && def.minifiedDef is not null)
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

    private ModWorldConfigurationDto BuildCurrentWorldConfiguration()
    {
        object? world = Find.World;
        object? worldInfo = ReadMember(world, "info") ?? ReadMember(world, "Info");
        ModWorldConfigurationDto configuration = new()
        {
            WorldConfigurationId = "world:" + Guid.NewGuid().ToString("N"),
            ConfiguredByUserId = settings.UserId,
            ConfiguredByColonyId = settings.ColonyId,
            ConfiguredAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            SeedString = ReadFirstString(worldInfo, "seedString", "SeedString", "seed"),
            PlanetCoverage = ReadFirstString(worldInfo, "planetCoverage", "PlanetCoverage"),
            OverallRainfall = ReadFirstString(worldInfo, "overallRainfall", "OverallRainfall"),
            OverallTemperature = ReadFirstString(worldInfo, "overallTemperature", "OverallTemperature"),
            OverallPopulation = ReadFirstString(worldInfo, "overallPopulation", "OverallPopulation"),
            LandmarkDensity = ReadFirstString(worldInfo, "landmarkDensity", "LandmarkDensity"),
            TileCount = ReadFirstString(Find.WorldGrid, "TilesCount", "tilesCount"),
            StorytellerDefName = ReadCurrentStorytellerDefName(),
            DifficultyDefName = ReadCurrentDifficultyDefName(),
            DifficultyValuesXml = ReadCurrentDifficultyValuesXml(),
            GameLanguage = ReadCurrentGameLanguage()
        };
        configuration.FactionDefNames.AddRange(ReadCurrentWorldFactionDefNames());
        configuration.Features.AddRange(ReadCurrentWorldFeatures());
        configuration.Factions.AddRange(ReadCurrentWorldFactions());
        configuration.Roads.AddRange(ReadCurrentWorldRoads());
        configuration.WorldObjects.AddRange(ReadCurrentWorldObjects());
        configuration.PlayerColonySites.AddRange(ReadCurrentPlayerColonySites(
            settings.UserId,
            settings.ColonyId,
            BuildCurrentColonyAppearanceDto(settings)));
        configuration.TileGeometry = ReadCurrentWorldTileGeometry();
        configuration.Extensions.AddRange(ClashOfRimCompatibilityApi.CollectCurrentWorldConfigurationExtensions(
            settings.UserId,
            settings.ColonyId,
            configuration.WorldConfigurationId));
        ClashLog.Message(
            $"[ClashOfRim] Captured world baseline id={configuration.WorldConfigurationId} seed={configuration.SeedString ?? "<null>"} coverage={configuration.PlanetCoverage ?? "<null>"} rainfall={configuration.OverallRainfall ?? "<null>"} temperature={configuration.OverallTemperature ?? "<null>"} population={configuration.OverallPopulation ?? "<null>"} landmarkDensity={configuration.LandmarkDensity ?? "<null>"} generationPollution={ReadWorldGenerationPollution(configuration).ToString(CultureInfo.InvariantCulture)} factionEntries={configuration.FactionDefNames.Count} features={configuration.Features.Count} roads={configuration.Roads.Count} worldExtensions={configuration.Extensions.Count} worldObjects={configuration.WorldObjects.Count} tileGeometryLayers={configuration.TileGeometry?.Layers.Count ?? 0}.");
        return configuration;
    }

    private async Task<ClashOfRimClientNetworkResult<ModSubmitWorldConfigurationResponseDto>> SubmitWorldConfigurationWithDetachedGeometryAsync(
        ClashOfRimModNetworkClient client,
        ModWorldConfigurationDto configuration,
        string context)
    {
        ModWorldTileGeometryDto? tileGeometry = configuration.TileGeometry;
        int tileCenterCount = CountWorldTileCenters(tileGeometry);
        ModWorldConfigurationDto lightweightConfiguration = CopyWorldConfiguration(configuration, includeTileGeometry: false);
        ClashLog.Message(
            $"[ClashOfRim] Submitting world configuration without tile geometry: context={context}, "
            + $"features={lightweightConfiguration.Features.Count}, roads={lightweightConfiguration.Roads.Count}, "
            + $"worldObjects={lightweightConfiguration.WorldObjects.Count}, tileCentersDetached={tileCenterCount}.");

        ClashOfRimClientNetworkResult<ModSubmitWorldConfigurationResponseDto> result =
            await client.SubmitWorldConfigurationAsync(lightweightConfiguration).ConfigureAwait(false);
        if (!result.Success || result.Response?.Result?.Accepted != true || tileCenterCount <= 0)
        {
            return result;
        }

        string worldConfigurationId = result.Response.WorldConfiguration?.WorldConfigurationId
            ?? configuration.WorldConfigurationId
            ?? string.Empty;
        if (string.IsNullOrWhiteSpace(worldConfigurationId))
        {
            Log.Warning("[ClashOfRim] Skipped detached world tile geometry submit because the accepted world configuration id is empty.");
            return result;
        }

        ClashOfRimClientNetworkResult<ModSubmitWorldTileGeometryResponseDto> geometryResult =
            await client.SubmitWorldTileGeometryAsync(worldConfigurationId, tileGeometry!).ConfigureAwait(false);
        if (!geometryResult.Success || geometryResult.Response?.Result?.Accepted != true)
        {
            string message = geometryResult.Message
                ?? geometryResult.Response?.Result?.Message
                ?? geometryResult.ErrorCode
                ?? geometryResult.Response?.Result?.ErrorCode.ToString()
                ?? "Unknown";
            Log.Warning("[ClashOfRim] Detached world tile geometry submit failed after " + context + ": " + message);
            return result;
        }

        ClashLog.Message(
            "[ClashOfRim] Detached world tile geometry accepted by server: "
            + $"layers={geometryResult.Response.LayerCount}, tileCenters={geometryResult.Response.TileCenterCount}.");
        return result;
    }

    private static int CountWorldTileCenters(ModWorldTileGeometryDto? geometry)
    {
        return geometry?.Layers.Sum(layer => layer.TileCenters.Count) ?? 0;
    }

    private static ModWorldConfigurationDto CopyWorldConfiguration(
        ModWorldConfigurationDto source,
        bool includeTileGeometry)
    {
        var copy = new ModWorldConfigurationDto
        {
            WorldConfigurationId = source.WorldConfigurationId,
            ConfiguredByUserId = source.ConfiguredByUserId,
            ConfiguredByColonyId = source.ConfiguredByColonyId,
            ConfiguredAtUtc = source.ConfiguredAtUtc,
            SeedString = source.SeedString,
            PlanetCoverage = source.PlanetCoverage,
            OverallRainfall = source.OverallRainfall,
            OverallTemperature = source.OverallTemperature,
            OverallPopulation = source.OverallPopulation,
            LandmarkDensity = source.LandmarkDensity,
            TileCount = source.TileCount,
            StorytellerDefName = source.StorytellerDefName,
            DifficultyDefName = source.DifficultyDefName,
            DifficultyValuesXml = source.DifficultyValuesXml,
            GameLanguage = source.GameLanguage,
            TileGeometry = includeTileGeometry ? source.TileGeometry : null
        };
        copy.FactionDefNames.AddRange(source.FactionDefNames);
        copy.Features.AddRange(source.Features);
        copy.FeatureNameCatalogs.AddRange(source.FeatureNameCatalogs);
        copy.Factions.AddRange(source.Factions);
        copy.Roads.AddRange(source.Roads);
        copy.WorldObjects.AddRange(source.WorldObjects);
        copy.PlayerColonySites.AddRange(source.PlayerColonySites);
        copy.Extensions.AddRange(source.Extensions);
        return copy;
    }

    private static string? ReadCurrentGameLanguage()
    {
        try
        {
            return LanguageDatabase.activeLanguage?.folderName
                ?? Prefs.LangFolderName;
        }
        catch
        {
            return null;
        }
    }

    private static ModWorldTileGeometryDto? ReadCurrentWorldTileGeometry()
    {
        if (Find.WorldGrid?.PlanetLayers is null)
        {
            return null;
        }

        var geometry = new ModWorldTileGeometryDto();
        foreach (KeyValuePair<int, PlanetLayer> entry in Find.WorldGrid.PlanetLayers.OrderBy(entry => entry.Key))
        {
            PlanetLayer layer = entry.Value;
            if (layer is null || layer.TilesCount <= 0 || layer.AverageTileSize <= 0f)
            {
                continue;
            }

            var layerDto = new ModWorldTileLayerGeometryDto
            {
                LayerId = layer.LayerID,
                LayerDefName = layer.Def?.defName,
                AverageTileSize = layer.AverageTileSize
            };

            for (int tile = 0; tile < layer.TilesCount; tile++)
            {
                Vector3 center = layer.GetTileCenter(tile);
                layerDto.TileCenters.Add(new ModWorldTileCenterDto
                {
                    Tile = tile,
                    X = center.x,
                    Y = center.y,
                    Z = center.z
                });
            }

            geometry.Layers.Add(layerDto);
        }

        return geometry.Layers.Count == 0 ? null : geometry;
    }

    private void GenerateWorldFromServerConfiguration(ModWorldConfigurationDto configuration, Page? nextPage, Action? nextAct)
    {
        DebugLogFlow(
            "GenerateWorldFromServerConfiguration.Queue",
            $"world={configuration.WorldConfigurationId}, nextPage={DescribeWindow(nextPage)}, nextAct={DescribeAction(nextAct)}");
        string seedString = string.IsNullOrWhiteSpace(configuration.SeedString)
            ? "ClashOfRim"
            : configuration.SeedString!;
        float planetCoverage = ParseFloat(configuration.PlanetCoverage, 0.3f);
        OverallRainfall rainfall = ParseEnum(configuration.OverallRainfall, OverallRainfall.Normal);
        OverallTemperature temperature = ParseEnum(configuration.OverallTemperature, OverallTemperature.Normal);
        OverallPopulation population = ParseEnum(configuration.OverallPopulation, OverallPopulation.Normal);
        LandmarkDensity landmarkDensity = ParseEnum(configuration.LandmarkDensity, LandmarkDensity.Normal);
        float pollution = ReadWorldGenerationPollution(configuration);
        List<FactionDef> factions = ResolveFactionDefs(configuration.FactionDefNames);
        ClashLog.Message(
            $"[ClashOfRim] Generating world from server baseline id={configuration.WorldConfigurationId} seed={seedString} coverage={planetCoverage.ToString(CultureInfo.InvariantCulture)} rainfall={rainfall} temperature={temperature} population={population} landmarkDensity={landmarkDensity} pollution={pollution.ToString(CultureInfo.InvariantCulture)} factionEntries={factions.Count}.");

        LongEventHandler.QueueLongEvent(delegate
        {
            DebugLogFlow(
                "GenerateWorldFromServerConfiguration.LongEvent.Start",
                $"thread={System.Threading.Thread.CurrentThread.ManagedThreadId}, seed={seedString}");
            Find.GameInitData.ResetWorldRelatedMapInitData();
            Rand.EnsureStateStackEmpty();
            Rand.PushState(GenText.StableStringHash(seedString));
            try
            {
                Current.Game.World = WorldGenerator.GenerateWorld(
                    planetCoverage,
                    seedString,
                    rainfall,
                    temperature,
                    population,
                    landmarkDensity,
                    factions,
                    pollution);
                DebugLogFlow(
                    "GenerateWorldFromServerConfiguration.LongEvent.WorldGenerated",
                    $"worldNull={Current.Game.World is null}, expected={DescribeWorldConfiguration(configuration)}, actual={DescribeCurrentWorldState()}");
                ApplyWorldBaseline(configuration, applyPollution: true);
                ClashLog.Message("[ClashOfRim] Applied server world baseline: "
                    + DescribeWorldConfiguration(configuration)
                    + ", actual="
                    + DescribeCurrentWorldState());
            }
            finally
            {
                Rand.PopState();
            }

            LongEventHandler.ExecuteWhenFinished(delegate
            {
                DebugLogFlow(
                    "GenerateWorldFromServerConfiguration.Finished.Enter",
                    $"nextPage={DescribeWindow(nextPage)}, nextAct={DescribeAction(nextAct)}, stack={DescribeWindowStack()}");
                RefreshWorldFeatureTexts();
                CloseBlockingServerWorldSetupPages();
                if (nextPage is not null)
                {
                    DebugLogFlow("GenerateWorldFromServerConfiguration.Finished.AddNextPage.Before", DescribeWindow(nextPage));
                    Find.WindowStack.Add(nextPage);
                    DebugLogFlow("GenerateWorldFromServerConfiguration.Finished.AddNextPage.After", DescribeWindowStack());
                }
                else if (nextAct is not null)
                {
                    DebugLogFlow("GenerateWorldFromServerConfiguration.Finished.InvokeNextAct", DescribeAction(nextAct));
                    nextAct();
                    DebugLogFlow("GenerateWorldFromServerConfiguration.Finished.InvokeNextAct.After", DescribeWindowStack());
                }
                else
                {
                    DebugLogFlow("GenerateWorldFromServerConfiguration.Finished.NoNext", "nextPage and nextAct are both null");
                }

                MemoryUtility.UnloadUnusedUnityAssets();
                Find.World.renderer.RegenerateAllLayersNow();
                Current.CreatingWorld = null;
                loginStatus = ClashOfRimText.Key("ClashOfRim.WorldBaseline.StatusGenerated", seedString.Named("SEED"));
                DebugLogFlow("GenerateWorldFromServerConfiguration.Finished.Exit", DescribeWindowStack());
            });
        }, "GeneratingWorld", doAsynchronously: true, null);

        Rand.EnsureStateStackEmpty();
    }

    private static void CloseBlockingServerWorldSetupPages()
    {
        if (Find.WindowStack is null)
        {
            return;
        }

        CloseBlockingServerWorldSetupPage(typeof(Page_SelectStoryteller));
        CloseBlockingServerWorldSetupPage(typeof(Page_CreateWorldParams));
        CloseBlockingServerWorldSetupPage(typeof(Page_SelectScenario));
    }

    private static void CloseBlockingServerWorldSetupPage(Type pageType)
    {
        while (Find.WindowStack.TryRemove(pageType, doCloseSound: false))
        {
            DebugLogFlow(
                "GenerateWorldFromServerConfiguration.Finished.CloseBlockingPage",
                pageType.Name + " removed, " + DescribeWindowStack());
        }
    }

    private void ApplyWorldBaseline(ModWorldConfigurationDto configuration, bool applyPollution = false)
    {
        RememberWorldConfigurationIdentity(configuration);
        List<ModWorldFeatureDto>? generatedFeatureNameCatalog =
            ApplyWorldFeatures(configuration, out string? generatedFeatureNameCatalogLanguage);
        if (generatedFeatureNameCatalog is { Count: > 0 }
            && !string.IsNullOrWhiteSpace(generatedFeatureNameCatalogLanguage))
        {
            StartSubmitWorldFeatureNameCatalog(
                configuration.WorldConfigurationId,
                generatedFeatureNameCatalogLanguage!,
                generatedFeatureNameCatalog);
        }

        ApplyWorldFactions(configuration.Factions);
        ApplyWorldRoads(configuration.Roads);
        ApplyServerWorldConfigurationExtensions(configuration, applyPollution);
        ApplyWorldObjectLabels(configuration.WorldObjects);
    }

    private void StartSubmitWorldFeatureNameCatalog(
        string worldConfigurationId,
        string language,
        IReadOnlyList<ModWorldFeatureDto> features)
    {
        if (string.IsNullOrWhiteSpace(worldConfigurationId)
            || string.IsNullOrWhiteSpace(language)
            || features.Count == 0)
        {
            return;
        }

        ClashOfRimClientNetworkContext context = ClashOfRimClientNetworkContext.FromSettings(settings);
        if (!context.IsConfigured)
        {
            return;
        }

        string key = worldConfigurationId.Trim() + "\u001f" + language.Trim();
        lock (submittedWorldFeatureNameCatalogKeys)
        {
            if (!submittedWorldFeatureNameCatalogKeys.Add(key))
            {
                return;
            }
        }

        List<ModWorldFeatureDto> catalogFeatures = features
            .Select(feature => CopyWorldFeatureLabel(feature, feature.Label ?? string.Empty))
            .ToList();
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(httpClient, context);
                ClashOfRimClientNetworkResult<ModSubmitWorldFeatureNamesResponseDto> result =
                    await client.SubmitWorldFeatureNamesAsync(language.Trim(), worldConfigurationId.Trim(), catalogFeatures).ConfigureAwait(false);
                if (!result.Success)
                {
                    Log.Warning("[ClashOfRim] Failed to submit world feature names: " + (result.Message ?? result.ErrorCode ?? "unknown"));
                    return;
                }

                ModSubmitWorldFeatureNamesResponseDto? response = result.Response;
                if (response?.Result?.Accepted != true || !response.Accepted)
                {
                    Log.Warning("[ClashOfRim] World feature names were rejected: " + (response?.Result?.Message ?? "unknown"));
                    return;
                }

                if (response.Created)
                {
                    ClashLog.Message("[ClashOfRim] Submitted world feature names for language " + language.Trim() + ".");
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim] Exception while submitting world feature names: " + ex.GetType().Name + " " + ex.Message);
            }
        });
    }

    private void ApplyServerWorldConfigurationExtensionCatalog(ModWorldConfigurationDto? configuration)
    {
        RememberWorldConfigurationIdentity(configuration);
        ApplyServerWorldConfigurationExtensions(configuration, applyWorldState: false);
    }

    private void ApplyRuntimeWorldConfigurationUpdate(ModWorldConfigurationDto configuration)
    {
        ApplyServerWorldConfigurationExtensionCatalog(configuration);
        ApplyStorytellerBaselineIfPlaying(configuration);
    }

    private void ApplyStorytellerBaselineIfPlaying(ModWorldConfigurationDto configuration)
    {
        if (Current.ProgramState != ProgramState.Playing || Current.Game is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(configuration.StorytellerDefName)
            || string.IsNullOrWhiteSpace(configuration.DifficultyDefName))
        {
            return;
        }

        ApplyStorytellerBaselineToCurrentGame(configuration);
    }

    private void ApplyServerWorldConfigurationExtensions(ModWorldConfigurationDto? configuration, bool applyWorldState)
    {
        if (configuration is null)
        {
            return;
        }

        ClashOfRimCompatibilityApi.ApplyWorldConfigurationExtensions(configuration, settings.UserId, applyWorldState);
        RebindPlayerProxySiteOwners(configuration);
    }

    private void RebindPlayerProxySiteOwners(ModWorldConfigurationDto configuration)
    {
        HashSet<string> owners = new(StringComparer.Ordinal);
        foreach (ModPlayerColonySiteDto site in configuration.PlayerColonySites)
        {
            if (!string.IsNullOrWhiteSpace(site.UserId)
                && !string.Equals(site.UserId, settings.UserId, StringComparison.Ordinal))
            {
                owners.Add(site.UserId!);
            }
        }

        foreach (string owner in owners)
        {
            PlayerFactionProxyUtility.EnsureProxyForUser(owner);
        }

        PlayerFactionProxyUtility.NormalizeExistingProxies(owners);
    }

    internal bool TryApplyServerStorytellerBaselineAndContinue(Window storytellerPage)
    {
        DebugLogFlow(
            "TryApplyServerStorytellerBaselineAndContinue.Enter",
            $"pending={pendingServerWorldConfiguration is not null}, page={DescribeWindow(storytellerPage)}, stack={DescribeWindowStack()}");
        if (pendingServerWorldConfiguration is null)
        {
            DebugLogFlow("TryApplyServerStorytellerBaselineAndContinue.Skip", "no pending server world configuration");
            return false;
        }

        ModWorldConfigurationDto configuration = pendingServerWorldConfiguration;
        if (string.IsNullOrWhiteSpace(configuration.StorytellerDefName)
            || string.IsNullOrWhiteSpace(configuration.DifficultyDefName))
        {
            DebugLogFlow(
                "TryApplyServerStorytellerBaselineAndContinue.Skip",
                $"missing baseline storyteller={configuration.StorytellerDefName}, difficulty={configuration.DifficultyDefName}");
            return false;
        }

        if (!ApplyStorytellerBaselineToCurrentGame(configuration))
        {
            DebugLogFlow("TryApplyServerStorytellerBaselineAndContinue.Skip", "failed to apply storyteller baseline");
            return false;
        }

        Page? nextPage = storytellerPage is Page page ? page.next : null;
        DebugLogFlow(
            "TryApplyServerStorytellerBaselineAndContinue.Continue",
            $"nextPage={DescribeWindow(nextPage)}, beforeCloseStack={DescribeWindowStack()}");
        storytellerPage.Close();
        DebugLogFlow("TryApplyServerStorytellerBaselineAndContinue.Closed", DescribeWindowStack());
        Find.WindowStack.Add(nextPage ?? new Page_CreateWorldParams());
        DebugLogFlow("TryApplyServerStorytellerBaselineAndContinue.AddedNext", DescribeWindowStack());
        return true;
    }

    internal void LockMultiplayerCommitmentModeIfNeeded()
    {
        if (!IsServerWorldSetupFlow || Find.GameInitData is null)
        {
            return;
        }

        Find.GameInitData.permadeathChosen = true;
        Find.GameInitData.permadeath = false;
    }

    internal static void DebugLogFlow(string stage, string? detail = null)
    {
        string suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : " | " + detail;
        ClashLog.Message("[ClashOfRim.Flow] " + stage + suffix);
    }

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

    private static string? ReadFirstString(object? target, params string[] names)
    {
        foreach (string name in names)
        {
            object? value = ReadMember(target, name);
            if (value is not null)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static object? ReadMember(object? target, string name)
    {
        if (target is null)
        {
            return null;
        }

        Type type = target.GetType();
        try
        {
            return AccessTools.Property(type, name)?.GetValue(target, null)
                ?? AccessTools.Field(type, name)?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadDefName(object? def)
    {
        return ReadFirstString(def, "defName", "DefName");
    }

    private static string? ReadCurrentStorytellerDefName()
    {
        object? storyteller = ReadMember(Current.Game, "storyteller")
            ?? ReadMember(Find.GameInitData, "storyteller");
        return ReadDefName(ReadMember(storyteller, "def")) ?? ReadDefName(storyteller);
    }

    private static string? ReadCurrentDifficultyDefName()
    {
        object? storyteller = ReadMember(Current.Game, "storyteller");
        object? difficulty = ReadMember(storyteller, "difficultyDef")
            ?? ReadMember(storyteller, "difficulty")
            ?? ReadMember(Find.GameInitData, "difficulty")
            ?? ReadMember(Find.GameInitData, "difficultyDef");
        return ReadDefName(difficulty) ?? ReadFirstString(difficulty, "defName", "DefName");
    }

    private static bool ApplyStorytellerBaselineToCurrentGame(ModWorldConfigurationDto configuration)
    {
        StorytellerDef? storytellerDef = DefDatabase<StorytellerDef>.GetNamedSilentFail(configuration.StorytellerDefName);
        DifficultyDef? difficultyDef = DefDatabase<DifficultyDef>.GetNamedSilentFail(configuration.DifficultyDefName);
        if (storytellerDef is null || difficultyDef is null)
        {
            Log.Warning($"[ClashOfRim] Server storyteller/difficulty baseline could not be resolved: storyteller={configuration.StorytellerDefName ?? "<null>"} difficulty={configuration.DifficultyDefName ?? "<null>"}");
            return false;
        }

        if (Current.Game is null)
        {
            Log.Warning("[ClashOfRim] Server storyteller/difficulty baseline could not be applied: Current.Game is null.");
            return false;
        }

        Difficulty difficulty = TryReadDifficultyValuesXml(configuration.DifficultyValuesXml, difficultyDef)
            ?? new Difficulty(difficultyDef);
        Current.Game.storyteller = new Storyteller(storytellerDef, difficultyDef, difficulty);
        Find.GameInitData.permadeathChosen = true;
        Find.GameInitData.permadeath = false;
        return true;
    }

    private static string? ReadCurrentDifficultyValuesXml()
    {
        Difficulty? difficulty = Current.Game?.storyteller?.difficulty;
        if (difficulty is null)
        {
            return null;
        }

        if (Scribe.mode != LoadSaveMode.Inactive)
        {
            Log.Warning("[ClashOfRim] Cannot capture difficulty baseline while Scribe is active.");
            return null;
        }

        try
        {
            return Scribe.saver.DebugOutputFor(difficulty);
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim] Failed to capture difficulty baseline: " + ex.GetType().Name + " " + ex.Message);
            return null;
        }
    }

    private static Difficulty? TryReadDifficultyValuesXml(string? xml, DifficultyDef difficultyDef)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        if (Scribe.mode != LoadSaveMode.Inactive)
        {
            Log.Warning("[ClashOfRim] Cannot apply difficulty baseline while Scribe is active.");
            return null;
        }

        string path = Path.Combine(Path.GetTempPath(), "ClashOfRimDifficulty-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(path, "<root>\n" + xml + "\n</root>", Encoding.UTF8);
            Scribe.loader.InitLoading(path);
            Difficulty? difficulty = null;
            Scribe_Deep.Look(ref difficulty, "saveable", difficultyDef);
            Scribe.loader.FinalizeLoading();
            return difficulty;
        }
        catch (Exception ex)
        {
            Scribe.ForceStop();
            Log.Warning("[ClashOfRim] Failed to apply difficulty baseline: " + ex.GetType().Name + " " + ex.Message);
            return null;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best effort cleanup for temporary Scribe input.
            }
        }
    }

    private static int? ReadFirstInt(object? target, params string[] names)
    {
        foreach (string name in names)
        {
            object? value = ReadMember(target, name);
            if (value is int intValue)
            {
                return intValue;
            }

            if (value is PlanetTile planetTile)
            {
                return planetTile.Valid ? planetTile.tileId : null;
            }

            object? tileId = ReadMember(value, "tileId")
                ?? ReadMember(value, "TileId")
                ?? ReadMember(value, "id")
                ?? ReadMember(value, "Id");
            if (tileId is int reflectedTileId)
            {
                return reflectedTileId;
            }

            if (value is not null
                && int.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static int ReadFirstTileLayerId(object? target, params string[] names)
    {
        foreach (string name in names)
        {
            object? value = ReadMember(target, name);
            if (value is PlanetTile planetTile)
            {
                return ReadPlanetTileLayerId(planetTile);
            }

            if (value is not null && PlanetTile.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out PlanetTile parsed))
            {
                return ReadPlanetTileLayerId(parsed);
            }
        }

        return 0;
    }

    private static int ReadPlanetTileLayerId(PlanetTile tile)
    {
        object? layerId = ReadMember(tile, "layerId");
        if (layerId is int reflectedLayerId)
        {
            return Math.Max(0, reflectedLayerId);
        }

        try
        {
            return tile.Valid ? Math.Max(0, tile.Layer.LayerID) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool? ReadFirstBool(object? target, params string[] names)
    {
        foreach (string name in names)
        {
            object? value = ReadMember(target, name);
            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (value is not null
                && bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out bool parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static List<string> ReadCurrentWorldFactionDefNames()
    {
        object? worldInfo = ReadMember(Find.World, "info") ?? ReadMember(Find.World, "Info");
        if (ReadMember(worldInfo, "factions") is IEnumerable<FactionDef> worldInfoFactions)
        {
            List<string> configuredFactionDefs = worldInfoFactions
                .Where(def => def != null)
                .Select(def => def.defName)
                .Where(defName => !string.IsNullOrWhiteSpace(defName))
                .ToList();
            if (configuredFactionDefs.Count > 0)
            {
                return configuredFactionDefs;
            }
        }

        return Find.World?.factionManager?.AllFactions
            .Where(faction => faction != null && faction != Faction.OfPlayer && faction.def != null)
            .Select(faction => faction.def.defName)
            .Where(defName => !string.IsNullOrWhiteSpace(defName))
            .ToList() ?? new List<string>();
    }

    private static bool IsUsableWorldGenerationBaseline(ModWorldConfigurationDto configuration, out string failureReason)
    {
        if (string.IsNullOrWhiteSpace(configuration.SeedString))
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.WorldBaseline.FailureMissingSeed");
            return false;
        }

        if (!float.TryParse(
                configuration.PlanetCoverage,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float coverage)
            || coverage <= 0f)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.WorldBaseline.FailureMissingCoverage");
            return false;
        }

        if (configuration.FactionDefNames.Count == 0)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.WorldBaseline.FailureMissingFactions");
            return false;
        }

        if (configuration.Features.Count == 0)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.WorldBaseline.FailureMissingFeatures");
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static List<ModWorldFeatureDto> ReadCurrentWorldFeatures()
    {
        List<ModWorldFeatureDto> features = new();
        if (Find.WorldFeatures?.features is null)
        {
            return features;
        }

        foreach (WorldFeature feature in Find.WorldFeatures.features)
        {
            if (feature.def is null)
            {
                continue;
            }

            features.Add(new ModWorldFeatureDto
            {
                DefName = feature.def.defName,
                Label = feature.name,
                MaxDrawSizeInTiles = feature.maxDrawSizeInTiles,
                DrawCenterX = feature.drawCenter.x,
                DrawCenterY = feature.drawCenter.y,
                DrawCenterZ = feature.drawCenter.z
            });
        }

        return features;
    }

    private static List<ModWorldFactionDto> ReadCurrentWorldFactions()
    {
        return Find.World?.factionManager?.AllFactions
            .Where(faction => faction != null && faction != Faction.OfPlayer && faction.def != null)
            .Select(faction => new ModWorldFactionDto
            {
                DefName = faction.def.defName,
                Name = faction.Name,
                ColorR = faction.Color.r,
                ColorG = faction.Color.g,
                ColorB = faction.Color.b,
                ColorA = faction.Color.a
            })
            .ToList() ?? new List<ModWorldFactionDto>();
    }

    private static List<ModWorldRoadDto> ReadCurrentWorldRoads()
    {
        List<ModWorldRoadDto> roads = new();
        foreach (SurfaceTile tile in Find.WorldGrid.Tiles)
        {
            if (tile.Roads is null)
            {
                continue;
            }

            foreach (SurfaceTile.RoadLink link in tile.Roads)
            {
                if (link.road is null || roads.Any(road =>
                    road.FromTile == tile.tile && road.ToTile == link.neighbor ||
                    road.FromTile == link.neighbor && road.ToTile == tile.tile))
                {
                    continue;
                }

                roads.Add(new ModWorldRoadDto
                {
                    FromTile = tile.tile,
                    ToTile = link.neighbor,
                    RoadDefName = link.road.defName
                });
            }
        }

        return roads;
    }

    private static List<ModWorldObjectBaselineDto> ReadCurrentWorldObjects()
    {
        List<ModWorldObjectBaselineDto> worldObjects = new();
        foreach (WorldObject worldObject in Find.WorldObjects.AllWorldObjects)
        {
            if (worldObject.def is null || worldObject.Faction == Faction.OfPlayer)
            {
                continue;
            }

            string? label = worldObject is Settlement settlement
                ? settlement.Name
                : Convert.ToString(ReadMember(worldObject, "Name") ?? ReadMember(worldObject, "Label"), CultureInfo.InvariantCulture);
            worldObjects.Add(new ModWorldObjectBaselineDto
            {
                DefName = worldObject.def.defName,
                Tile = worldObject.Tile,
                Label = label,
                FactionDefName = worldObject.Faction?.def?.defName
            });
        }

        return worldObjects;
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

    private static List<ModPlayerColonySiteDto> ReadCurrentPlayerColonySites(
        string? userId,
        string? colonyId,
        ModColonyAppearanceDto? appearance = null)
    {
        List<ModPlayerColonySiteDto> sites = new();
        if (Find.Maps is null)
        {
            return sites;
        }

        foreach (Map map in Find.Maps)
        {
            if (map is null || !IsPlayerColonyMap(map))
            {
                continue;
            }

            int? tile = ReadFirstInt(map, "Tile", "tile");
            if (!tile.HasValue || !IsValidTile(tile.Value))
            {
                continue;
            }

            object? parent = ReadMember(map, "Parent") ?? ReadMember(map, "parent");
            sites.Add(new ModPlayerColonySiteDto
            {
                UserId = userId ?? string.Empty,
                ColonyId = colonyId ?? string.Empty,
                WorldObjectId = ReadWorldObjectId(parent, tile.Value, ReadFirstTileLayerId(map, "Tile", "tile")),
                MapUniqueId = "Map_" + map.uniqueID,
                Tile = tile.Value,
                TileLayerId = ReadFirstTileLayerId(map, "Tile", "tile"),
                Label = ReadPlayerColonyLabel(parent, colonyId),
                FactionName = ReadPlayerFactionName(),
                Appearance = appearance
            });
        }

        if (Find.WorldObjects?.AllWorldObjects is not null)
        {
            foreach (WorldObject worldObject in Find.WorldObjects.AllWorldObjects)
            {
                if (!IsPlayerColonyWorldObject(worldObject))
                {
                    continue;
                }

                int? tile = ReadFirstInt(worldObject, "Tile", "tile");
                if (!tile.HasValue || !IsValidTile(tile.Value))
                {
                    continue;
                }

                int tileLayerId = ReadFirstTileLayerId(worldObject, "Tile", "tile");
                string? worldObjectId = ReadWorldObjectId(worldObject, tile.Value, tileLayerId);
                if (sites.Any(site => string.Equals(site.WorldObjectId, worldObjectId, StringComparison.Ordinal)
                                      || (site.Tile == tile.Value && site.TileLayerId == tileLayerId)))
                {
                    continue;
                }

                sites.Add(new ModPlayerColonySiteDto
                {
                    UserId = userId ?? string.Empty,
                    ColonyId = colonyId ?? string.Empty,
                    WorldObjectId = worldObjectId,
                    MapUniqueId = ReadMapUniqueIdFromWorldObject(worldObject),
                    Tile = tile.Value,
                    TileLayerId = tileLayerId,
                    Label = ReadPlayerColonyLabel(worldObject, colonyId),
                    FactionName = ReadPlayerFactionName(),
                    Appearance = appearance
                });
            }
        }

        return sites
            .GroupBy(site => site.MapUniqueId ?? "tile:" + site.Tile + "," + site.TileLayerId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(site => site.Tile)
            .ThenBy(site => site.TileLayerId)
            .ToList();
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

    private static bool IsPlayerColonyMap(Map map)
    {
        bool? isPlayerHome = ReadFirstBool(map, "IsPlayerHome", "isPlayerHome");
        if (isPlayerHome.HasValue)
        {
            return isPlayerHome.Value;
        }

        object? parent = ReadMember(map, "Parent") ?? ReadMember(map, "parent");
        object? faction = ReadMember(parent, "Faction") ?? ReadMember(parent, "factionInt");
        return ReferenceEquals(faction, Faction.OfPlayer) || ReferenceEquals(map, Find.CurrentMap);
    }

    private static bool IsPlayerColonyWorldObject(WorldObject? worldObject)
    {
        if (worldObject is null || worldObject.Destroyed)
        {
            return false;
        }

        string? defName = ReadFirstString(ReadMember(worldObject, "def"), "defName", "Name");
        if (string.Equals(defName, "PlayerColony", StringComparison.Ordinal))
        {
            return true;
        }

        if (worldObject is not MapParent)
        {
            return false;
        }

        object? faction = ReadMember(worldObject, "Faction") ?? ReadMember(worldObject, "factionInt");
        return ReferenceEquals(faction, Faction.OfPlayer);
    }

    private static string? ReadMapUniqueIdFromWorldObject(WorldObject worldObject)
    {
        object? map = ReadMember(worldObject, "Map") ?? ReadMember(worldObject, "map");
        int? uniqueId = ReadFirstInt(map, "uniqueID", "UniqueID", "uniqueId");
        return uniqueId.HasValue ? "Map_" + uniqueId.Value.ToString(CultureInfo.InvariantCulture) : null;
    }

    private static string? ReadWorldObjectId(object? worldObject, int tile, int tileLayerId = 0)
    {
        string? id = ReadFirstString(worldObject, "UniqueLoadID", "uniqueLoadID", "ID", "id");
        if (!string.IsNullOrWhiteSpace(id))
        {
            return id!.StartsWith("WorldObject_", StringComparison.Ordinal)
                ? id
                : "WorldObject_" + id;
        }

        return "tile:" + tile.ToString(CultureInfo.InvariantCulture)
            + ","
            + Math.Max(0, tileLayerId).ToString(CultureInfo.InvariantCulture);
    }

    private static string? ReadPlayerColonyLabel(object? parent, string? colonyId)
    {
        string? label = ReadFirstString(parent, "Name", "Label", "label");
        return string.IsNullOrWhiteSpace(label) ? colonyId : label;
    }

    private static string? ReadPlayerFactionName()
    {
        string? name = Faction.OfPlayer?.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name;
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
            .Where(worldObject => worldObject is not null
                && (string.Equals(
                        ReadFirstString(ReadMember(worldObject, "def"), "defName", "Name"),
                        "PlayerColony",
                        StringComparison.Ordinal)
                    || ReferenceEquals(
                        ReadMember(worldObject, "Faction") ?? ReadMember(worldObject, "factionInt"),
                        Faction.OfPlayer)))
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

    private static bool ShouldUseLocalWorldFeatureLabels(ModWorldConfigurationDto configuration)
    {
        string? baselineLanguage = configuration.GameLanguage;
        string? currentLanguage = ReadCurrentGameLanguage();
        return !string.IsNullOrWhiteSpace(baselineLanguage)
            && !string.IsNullOrWhiteSpace(currentLanguage)
            && !string.Equals(baselineLanguage, currentLanguage, StringComparison.OrdinalIgnoreCase);
    }

    private static List<ModWorldFeatureDto>? ApplyWorldFeatures(
        ModWorldConfigurationDto configuration,
        out string? generatedFeatureNameCatalogLanguage)
    {
        generatedFeatureNameCatalogLanguage = null;
        IReadOnlyList<ModWorldFeatureDto> features = configuration.Features;
        if (features.Count == 0 || Find.WorldFeatures?.features is null)
        {
            return null;
        }

        string? currentLanguage = ReadCurrentGameLanguage();
        ModWorldFeatureNameCatalogDto? featureNameCatalog = FindWorldFeatureNameCatalog(configuration, currentLanguage);
        bool useFeatureNameCatalog = IsWorldFeatureNameCatalogUsable(featureNameCatalog, features);
        bool generateLocalLanguageCatalog = !useFeatureNameCatalog && ShouldUseLocalWorldFeatureLabels(configuration);
        List<ModWorldFeatureDto>? generatedFeatureNameCatalog = generateLocalLanguageCatalog
            ? new List<ModWorldFeatureDto>(features.Count)
            : null;
        if (generateLocalLanguageCatalog)
        {
            generatedFeatureNameCatalogLanguage = currentLanguage;
        }

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        Find.WorldFeatures.features.Clear();
        for (int index = 0; index < features.Count; index++)
        {
            ModWorldFeatureDto feature = features[index];
            FeatureDef? def = DefDatabase<FeatureDef>.GetNamedSilentFail(feature.DefName);
            if (def is null)
            {
                continue;
            }

            ModWorldFeatureDto labelFeature = useFeatureNameCatalog
                ? featureNameCatalog!.Features[index]
                : feature;
            string label = ResolveWorldFeatureLabel(
                def,
                labelFeature,
                index,
                generateLocalLanguageCatalog,
                usedNames);
            Find.WorldFeatures.features.Add(new WorldFeature
            {
                def = def,
                uniqueID = index,
                name = label,
                maxDrawSizeInTiles = feature.MaxDrawSizeInTiles,
                drawCenter = new Vector3(feature.DrawCenterX, feature.DrawCenterY, feature.DrawCenterZ),
                layer = PlanetLayer.Selected
            });
            generatedFeatureNameCatalog?.Add(CopyWorldFeatureLabel(feature, label));
        }

        Find.WorldFeatures.textsCreated = false;
        return generatedFeatureNameCatalog is { Count: > 0 }
            ? generatedFeatureNameCatalog
            : null;
    }

    private static ModWorldFeatureNameCatalogDto? FindWorldFeatureNameCatalog(
        ModWorldConfigurationDto configuration,
        string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        string normalizedLanguage = language!.Trim();
        return configuration.FeatureNameCatalogs.FirstOrDefault(catalog =>
            string.Equals(catalog.Language?.Trim(), normalizedLanguage, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWorldFeatureNameCatalogUsable(
        ModWorldFeatureNameCatalogDto? catalog,
        IReadOnlyList<ModWorldFeatureDto> features)
    {
        if (catalog is null || catalog.Features.Count != features.Count)
        {
            return false;
        }

        for (int index = 0; index < features.Count; index++)
        {
            ModWorldFeatureDto expected = features[index];
            ModWorldFeatureDto actual = catalog.Features[index];
            if (!string.Equals(expected.DefName, actual.DefName, StringComparison.Ordinal)
                || !NearlyEqual(expected.MaxDrawSizeInTiles, actual.MaxDrawSizeInTiles)
                || !NearlyEqual(expected.DrawCenterX, actual.DrawCenterX)
                || !NearlyEqual(expected.DrawCenterY, actual.DrawCenterY)
                || !NearlyEqual(expected.DrawCenterZ, actual.DrawCenterZ))
            {
                return false;
            }
        }

        return true;
    }

    private static bool NearlyEqual(float left, float right)
    {
        return Math.Abs(left - right) <= 0.001f;
    }

    private static ModWorldFeatureDto CopyWorldFeatureLabel(ModWorldFeatureDto source, string label)
    {
        return new ModWorldFeatureDto
        {
            DefName = source.DefName,
            Label = label,
            MaxDrawSizeInTiles = source.MaxDrawSizeInTiles,
            DrawCenterX = source.DrawCenterX,
            DrawCenterY = source.DrawCenterY,
            DrawCenterZ = source.DrawCenterZ
        };
    }

    private static string ResolveWorldFeatureLabel(
        FeatureDef def,
        ModWorldFeatureDto feature,
        int index,
        bool useLocalLanguageLabel,
        HashSet<string> usedNames)
    {
        if (useLocalLanguageLabel && TryGenerateLocalWorldFeatureName(def, feature, index, usedNames, out string generatedName))
        {
            return generatedName;
        }

        string label = string.IsNullOrWhiteSpace(feature.Label)
            ? def.label ?? def.defName
            : feature.Label!;
        usedNames.Add(label);
        return label;
    }

    private static bool TryGenerateLocalWorldFeatureName(
        FeatureDef def,
        ModWorldFeatureDto feature,
        int index,
        HashSet<string> usedNames,
        out string name)
    {
        name = string.Empty;
        if (def.nameMaker is null)
        {
            return false;
        }

        int seed = GenText.StableStringHash(
            string.Join(
                "\u001f",
                def.defName,
                index.ToString(CultureInfo.InvariantCulture),
                feature.MaxDrawSizeInTiles.ToString(CultureInfo.InvariantCulture),
                feature.DrawCenterX.ToString(CultureInfo.InvariantCulture),
                feature.DrawCenterY.ToString(CultureInfo.InvariantCulture),
                feature.DrawCenterZ.ToString(CultureInfo.InvariantCulture),
                ReadCurrentGameLanguage() ?? string.Empty));
        Rand.PushState(seed);
        try
        {
            name = NameGenerator.GenerateName(
                def.nameMaker,
                candidate => !string.IsNullOrWhiteSpace(candidate) && !usedNames.Contains(candidate),
                appendNumberIfNameUsed: false,
                rootKeyword: "r_name");
        }
        catch (Exception ex)
        {
            Log.Warning($"[ClashOfRim] Failed to localize world feature name for {def.defName}: {ex.Message}");
            return false;
        }
        finally
        {
            Rand.PopState();
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        usedNames.Add(name);
        return true;
    }

    private static void RefreshWorldFeatureTexts()
    {
        if (Find.WorldFeatures is null)
        {
            return;
        }

        Find.WorldFeatures.textsCreated = false;
        Find.WorldFeatures.UpdateFeatures();
    }

    private static void ApplyWorldFactions(IReadOnlyList<ModWorldFactionDto> factions)
    {
        foreach (ModWorldFactionDto baseline in factions)
        {
            Faction? faction = Find.World?.factionManager?.AllFactions
                .FirstOrDefault(item => item.def?.defName == baseline.DefName);
            if (faction is null)
            {
                faction = CreateWorldBaselineFaction(baseline);
                if (faction is null)
                {
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(baseline.Name))
            {
                faction.Name = baseline.Name;
            }

            faction.color = new Color(baseline.ColorR, baseline.ColorG, baseline.ColorB, baseline.ColorA);
        }
    }

    private static Faction? CreateWorldBaselineFaction(ModWorldFactionDto baseline)
    {
        if (Find.World?.factionManager is null || string.IsNullOrWhiteSpace(baseline.DefName))
        {
            return null;
        }

        FactionDef? factionDef = DefDatabase<FactionDef>.GetNamedSilentFail(baseline.DefName);
        if (factionDef is null || factionDef.isPlayer)
        {
            return null;
        }

        Faction faction = new()
        {
            def = factionDef,
            loadID = Find.UniqueIDsManager.GetNextFactionID(),
            hidden = false,
            color = new Color(baseline.ColorR, baseline.ColorG, baseline.ColorB, baseline.ColorA)
        };
        faction.Name = !string.IsNullOrWhiteSpace(baseline.Name)
            ? baseline.Name
            : factionDef.LabelCap;
        ClashOfRimCompatibilityApi.NotifyFactionPrepared(faction, "WorldBaseline");

        foreach (Faction other in Find.World.factionManager.AllFactionsListForReading.ToList())
        {
            if (other is null || other == faction)
            {
                continue;
            }

            faction.SetRelation(new FactionRelation(other, FactionRelationKind.Neutral));
            other.SetRelation(new FactionRelation(faction, FactionRelationKind.Neutral));
        }

        Find.World.factionManager.Add(faction);
        ClashLog.Message("[ClashOfRim] Created server baseline faction: " + baseline.DefName + ".");
        return faction;
    }

    private static void ApplyWorldRoads(IReadOnlyList<ModWorldRoadDto> roads)
    {
        PlanetLayer layer = Find.World.grid.FirstLayerOfDef(PlanetLayerDefOf.Surface);
        foreach (SurfaceTile tile in layer.Tiles)
        {
            tile.Roads?.Clear();
            tile.potentialRoads = null;
        }

        foreach (ModWorldRoadDto road in roads)
        {
            if (!IsValidTile(road.FromTile) || !IsValidTile(road.ToTile))
            {
                continue;
            }

            RoadDef? roadDef = DefDatabase<RoadDef>.GetNamedSilentFail(road.RoadDefName);
            if (roadDef is null)
            {
                continue;
            }

            AddRoadLink(Find.WorldGrid[road.FromTile], road.ToTile, roadDef);
            AddRoadLink(Find.WorldGrid[road.ToTile], road.FromTile, roadDef);
        }
    }

    private static void AddRoadLink(SurfaceTile tile, int neighborTile, RoadDef roadDef)
    {
        SurfaceTile.RoadLink link = new()
        {
            neighbor = neighborTile,
            road = roadDef
        };
        tile.potentialRoads ??= new List<SurfaceTile.RoadLink>();
        tile.potentialRoads.Add(link);
    }

    private static void ApplyWorldObjectLabels(IReadOnlyList<ModWorldObjectBaselineDto> worldObjects)
    {
        SyncWorldObjectsToBaseline(worldObjects);
        foreach (ModWorldObjectBaselineDto baseline in worldObjects)
        {
            WorldObject? worldObject = Find.WorldObjects.AllWorldObjects
                .FirstOrDefault(item => item.Tile == baseline.Tile && item.def?.defName == baseline.DefName);
            if (worldObject is null)
            {
                continue;
            }

            Faction? faction = string.IsNullOrWhiteSpace(baseline.FactionDefName)
                ? null
                : Find.World.factionManager.AllFactions.FirstOrDefault(item => item.def?.defName == baseline.FactionDefName);
            if (faction is not null)
            {
                worldObject.SetFaction(faction);
            }

            if (!string.IsNullOrWhiteSpace(baseline.Label) && worldObject is Settlement settlement)
            {
                settlement.Name = baseline.Label;
            }
        }
    }

    private static void SyncWorldObjectsToBaseline(IReadOnlyList<ModWorldObjectBaselineDto> worldObjects)
    {
        // Vanilla and quest-created NPC world objects remain local simulation state.
        // Multiplayer only adds its own remote markers; server baselines may rename
        // matching objects, but must not create or delete vanilla world objects.
    }

    private static bool IsValidTile(int tile)
    {
        return tile >= 0 && tile < Find.WorldGrid.TilesCount;
    }

    private static List<FactionDef> ResolveFactionDefs(IEnumerable<string>? defNames)
    {
        List<FactionDef> factions = new();
        if (defNames is not null)
        {
            foreach (string defName in defNames.Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                FactionDef? faction = DefDatabase<FactionDef>.GetNamedSilentFail(defName);
                if (faction is not null)
                {
                    factions.Add(faction);
                }
            }
        }

        return factions.Count > 0
            ? factions
            : DefDatabase<FactionDef>.AllDefsListForReading.ToList();
    }

    private static float ParseFloat(string? value, float fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : fallback;
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (Enum.TryParse(value, ignoreCase: true, out TEnum parsed))
        {
            return parsed;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric)
            && Enum.IsDefined(typeof(TEnum), numeric)
            ? (TEnum)Enum.ToObject(typeof(TEnum), numeric)
            : fallback;
    }

}
