using AIRsLight.ClashOfRim.Network.Modules.Support;
using AIRsLight.ClashOfRim.Network.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Network.Modules.Shop;
using AIRsLight.ClashOfRim.Network.Modules.Trades;
using AIRsLight.ClashOfRim.Network.Modules.Gifts;
using static AIRsLight.ClashOfRim.Network.Modules.Transfers.ItemTransferMapper;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Modules.Bank;
using AIRsLight.ClashOfRim.Network.Modules.Events;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{

    private static readonly JsonSerializerOptions CompatibilityJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly IReadOnlyList<ClientLifecycleHook> ClientLifecycleHooks = new ClientLifecycleHook[]
    {
        ActivateRaidProtectionOnClientLifecycle,
        ReconcilePendingConfirmationsOnClientLifecycle,
        ReconcileAbandonedColonyOnClientLifecycle
    };

    private static void UseRequestDiagnostics(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            string? language = context.Request.Headers[ServerLocalization.LanguageHeader].FirstOrDefault();
            using ServerLocalizationScope scope = ServerLocalization.BeginRequest(language);
            ILogger logger = context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("AIRsLight.ClashOfRim.Network.RequestDiagnostics");

            try
            {
                await next(context);
                if (context.Response.StatusCode >= StatusCodes.Status400BadRequest)
                {
                    logger.LogWarning(
                        "HTTP {StatusCode} for {Method} {Path}{QueryString}",
                        context.Response.StatusCode,
                        context.Request.Method,
                        context.Request.Path,
                        context.Request.QueryString);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Unhandled exception while processing {Method} {Path}{QueryString}",
                    context.Request.Method,
                    context.Request.Path,
                    context.Request.QueryString);

                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        result = ProtocolResponse.Reject(
                            ProtocolErrorCode.ServerRejected,
                            ServerLocalization.Text("Server.InternalError"))
                    });
                }
            }
        });
    }

    public static void MapEndpoints(WebApplication app)
    {
        EventReadEndpoints.Map(app, app.Services.GetRequiredService<ClashOfRimNetworkState>().EventDelivery);
        app.MapGet("/server/plugins", ListServerPlugins);
        app.MapGet(ProtocolContractManifest.Find(ProtocolMessageKind.ServerHello).Route, ServerHello);
        BankEndpoints.Map(app, app.Services.GetRequiredService<ClashOfRimNetworkState>().Bank);
        var tradeState = app.Services.GetRequiredService<ClashOfRimNetworkState>();
        TradeEndpoints.Map(app, tradeState.Trades, tradeState.TradeMarket);
        SupportEndpoints.Map(app, tradeState.SupportSending, tradeState.Support);
        GiftEndpoints.Map(app, app.Services.GetRequiredService<ClashOfRimNetworkState>().Gifts);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.Login).Route, Login).WithMetadata(EndpointSnapshotRecovery.Instance);
        app.MapGet(ProtocolContractManifest.Find(ProtocolMessageKind.StreamSession).Route, StreamSession).WithMetadata(EndpointSnapshotRecovery.Instance);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.MaintainPresence).Route, MaintainPresence).WithMetadata(EndpointSnapshotRecovery.Instance);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.Logout).Route, Logout).WithMetadata(EndpointSnapshotRecovery.Instance);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ChangeOfflinePassword).Route, ChangeOfflinePassword);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ListPlayers).Route, ListPlayers);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ListAchievements).Route, ListAchievements);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.UploadSnapshot).Route, UploadSnapshot).WithMetadata(EndpointSnapshotRecovery.Instance);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.DownloadLatestSnapshot).Route, DownloadLatestSnapshot).WithMetadata(EndpointSnapshotRecovery.Instance);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.DownloadLatestSnapshotPayload).Route, DownloadLatestSnapshotPayload).WithMetadata(EndpointSnapshotRecovery.Instance);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmEventApplication).Route, ConfirmEventApplication).WithMetadata(EndpointSnapshotRecovery.Instance);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmEventApplications).Route, ConfirmEventApplications).WithMetadata(EndpointSnapshotRecovery.Instance);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ReportEventApplicationFailure).Route, ReportEventApplicationFailure).WithMetadata(EndpointSnapshotRecovery.Instance);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.StorePawnPackage).Route, StorePawnPackage);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetPawnPackage).Route, GetPawnPackage);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.StorePawnPackagePreview).Route, StorePawnPackagePreview);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetPawnPackagePreview).Route, GetPawnPackagePreview);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.StoreThingPackage).Route, StoreThingPackage);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetThingPackage).Route, GetThingPackage);
        ShopEndpoints.Map(app, app.Services.GetRequiredService<ClashOfRimNetworkState>().Shop);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.PrepareRaid).Route, PrepareRaid);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateRaid).Route, CreateRaid).WithMetadata(EndpointSnapshotRecovery.Instance);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateRaidWithSnapshot).Route, CreateRaidWithSnapshot).WithMetadata(EndpointSnapshotRecovery.Instance);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateDiplomacyEvent).Route, CreateDiplomacyEvent).WithMetadata(EndpointSnapshotRecovery.Instance);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RespondDiplomacyEvent).Route, RespondDiplomacyEvent).WithMetadata(EndpointSnapshotRecovery.Instance);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.SyncWorldMapMarkers).Route, SyncWorldMapMarkers);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.SyncRuntimeWorldObjects).Route, SyncRuntimeWorldObjects);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.PrepareWorldSession).Route, PrepareWorldSession).WithMetadata(EndpointSnapshotRecovery.Instance);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetWorldConfiguration).Route, GetWorldConfiguration);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.SubmitWorldConfiguration).Route, SubmitWorldConfiguration).WithMetadata(EndpointSnapshotRecovery.Instance);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.UploadWorldSubstrate).Route, UploadWorldSubstrate);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.DownloadWorldSubstrate).Route, DownloadWorldSubstrate);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.SubmitWorldFeatureNames).Route, SubmitWorldFeatureNames);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RegisterPlayerColonySites).Route, RegisterPlayerColonySites);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.PreflightColonyRelocation).Route, PreflightColonyRelocation);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmColonyRelocation).Route, ConfirmColonyRelocation);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.AbandonPlayerColony).Route, AbandonPlayerColony);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetAdminBaselineRequirements).Route, GetAdminBaselineRequirements).WithMetadata(EndpointSnapshotRecovery.Instance);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.SubmitAdminBaseline).Route, SubmitAdminBaseline).WithMetadata(EndpointSnapshotRecovery.Instance);
        var mercenaryState = app.Services.GetRequiredService<ClashOfRimNetworkState>();
        MercenaryEndpoints.Map(app, mercenaryState.Mercenaries, mercenaryState.MercenaryHiring);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.SendChatMessage).Route, SendChatMessage);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ListChatMessages).Route, ListChatMessages);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.OverrideCompatibilityBaseline).Route, OverrideCompatibilityBaseline);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.AdminStatus).Route, AdminStatus);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.AdminUpdateConfiguration).Route, AdminUpdateConfiguration);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.AdminAction).Route, AdminAction);
    }

    private static string T(string key, params (string Key, string? Value)[] args) => ServerText.Format(key, args);

    private static IResult ServerHello(ClashOfRimNetworkState state)
    {
        IReadOnlyList<ServerPluginVersionDto> plugins = state.Plugins.Plugins
            .Select(plugin => new ServerPluginVersionDto(
                plugin.Id,
                plugin.Name,
                plugin.Version,
                plugin.Capabilities))
            .ToList();

        return Results.Ok(new ServerHelloResponse(
            ProtocolResponse.Ok(),
            ClashOfRimVersion.ProductName,
            ClashOfRimVersion.ProductVersion,
            ProtocolApiVersion.Current,
            ProtocolApiVersion.Major,
            ProtocolApiVersion.Minor,
            ProtocolApiVersion.MinimumSupportedMajor,
            ProtocolApiVersion.MinimumSupportedMinor,
            ClashOfRimVersion.CompatibilityApiVersion,
            DateTimeOffset.UtcNow.ToString("O"),
            plugins));
    }

    private static ILogger RuntimeLogger(ILoggerFactory loggerFactory)
    {
        return loggerFactory.CreateLogger("AIRsLight.ClashOfRim.Network.Runtime");
    }

    private static void LogEventAppend(ClashOfRimNetworkState state, LedgerAppendResult append, string source)
        => EventDiagnostics.LogAppend(state.RuntimeLogger, append, source);

    private static void TryRegisterPlayerColonySiteFromAcceptedSnapshot(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        SnapshotUploadResult upload)
    {
        if (!upload.Accepted || upload.AcceptedSnapshot is null || state.WorldConfiguration.Current is null)
        {
            return;
        }

        WorldConfigurationDto currentConfiguration = state.WorldConfiguration.Current;
        WorldConfigurationExtensionService activeWorldExtensions = ActiveWorldConfigurationExtensions(state);
        IReadOnlyList<WorldConfigurationExtensionDto> extensions =
            activeWorldExtensions.BuildExtensionsFromAcceptedSnapshot(
                new WorldConfigurationExtensionSnapshotContext(
                    upload.AcceptedSnapshot,
                    userId,
                    colonyId,
                    currentConfiguration.Extensions));
        IReadOnlyList<WorldConfigurationExtensionDto> mergedExtensions = activeWorldExtensions.MergeExtensions(
            new WorldConfigurationExtensionContext(
                userId,
                colonyId,
                currentConfiguration.WorldConfigurationId),
            currentConfiguration.Extensions,
            extensions);
        bool extensionsChanged = !WorldConfigurationExtensionsEqual(
            currentConfiguration.Extensions,
            mergedExtensions);

        IReadOnlyList<SnapshotColonyAnchor> anchors = ExtractSnapshotColonyAnchors(state, upload.AcceptedSnapshot.Index);
        IReadOnlyList<PlayerColonySiteDto> normalizedSites = Array.Empty<PlayerColonySiteDto>();
        if (anchors.Count == 1)
        {
            SnapshotColonyAnchor anchor = anchors[0];
            PlayerColonySiteDto? existingSite = currentConfiguration.PlayerColonySites.FirstOrDefault(site =>
                string.Equals(site.UserId, userId, StringComparison.Ordinal)
                && string.Equals(site.ColonyId, colonyId, StringComparison.Ordinal));
            PlayerColonySiteDto site = new(
                userId,
                colonyId,
                anchor.WorldObjectId,
                anchor.MapUniqueId,
                anchor.Tile,
                string.IsNullOrWhiteSpace(anchor.Label) ? colonyId : anchor.Label,
                existingSite?.FactionName,
                existingSite?.Appearance,
                anchor.TileLayerId);
            IReadOnlyList<PlayerColonySiteDto> candidateSites = NormalizePlayerColonySites(
                userId,
                colonyId,
                new[] { site });
            if (candidateSites.Count == 1
                && FindExistingUserColonyConflict(currentConfiguration.PlayerColonySites, candidateSites, userId, colonyId) is null
                && FindExistingSameColonySiteOnDifferentTile(currentConfiguration.PlayerColonySites, candidateSites, userId, colonyId) is null
                && FindSameTilePlayerColonyConflict(currentConfiguration.PlayerColonySites, candidateSites, userId, colonyId) is null)
            {
                normalizedSites = candidateSites;
            }
        }

        if (normalizedSites.Count == 0 && !extensionsChanged)
        {
            return;
        }

        state.WorldConfiguration.RegisterPlayerColonySites(
            userId,
            colonyId,
            normalizedSites,
            extensions,
            activeWorldExtensions);
        SignalWorldConfigurationChanged(state, userId);
    }

    private static IReadOnlyList<SnapshotColonyAnchor> ExtractSnapshotColonyAnchors(
        ClashOfRimNetworkState state,
        SaveSnapshotIndex index)
    {
        var anchors = new List<SnapshotColonyAnchor>();
        HashSet<string> playerFactionIds = BuildSnapshotPlayerFactionIds(index);
        IReadOnlyList<IWorldObjectClassifier> worldObjectClassifiers =
            state.Plugins.ActiveWorldObjectClassifiers(state.CompatibilityBaseline.Current);
        foreach (MapSummary map in index.Maps)
        {
            if (string.IsNullOrWhiteSpace(map.ParentWorldObjectId))
            {
                continue;
            }

            WorldObjectSummary? worldObject = FindSnapshotWorldObjectById(index.WorldObjects, map.ParentWorldObjectId!);
            if (worldObject is null
                || worldObject.Destroyed
                || !IsSnapshotPlayerColonyAnchor(map, worldObject, worldObjectClassifiers, playerFactionIds)
                || !TryParseSnapshotTile(worldObject.Tile, out int tile, out int tileLayerId))
            {
                continue;
            }

            anchors.Add(new SnapshotColonyAnchor(
                NormalizeSnapshotMapUniqueId(map.UniqueId),
                worldObject.UniqueLoadId ?? worldObject.Id,
                tile,
                tileLayerId,
                worldObject.Name));
        }

        return anchors
            .GroupBy(
                anchor => anchor.WorldObjectId
                    ?? anchor.MapUniqueId
                    ?? "tile:" + anchor.Tile.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + anchor.TileLayerId.ToString(CultureInfo.InvariantCulture),
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static HashSet<string> BuildSnapshotPlayerFactionIds(SaveSnapshotIndex index)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (FactionSummary faction in index.Factions)
        {
            if (!IsSnapshotPlayerFaction(faction))
            {
                continue;
            }

            AddSnapshotFactionId(result, faction.UniqueLoadId);
            AddSnapshotFactionId(result, faction.LoadId);
            if (!string.IsNullOrWhiteSpace(faction.LoadId))
            {
                AddSnapshotFactionId(result, "Faction_" + faction.LoadId);
            }
        }

        return result;
    }

    private static bool IsSnapshotPlayerFaction(FactionSummary faction)
    {
        return string.Equals(faction.Def, "PlayerColony", StringComparison.Ordinal)
            || string.Equals(faction.Def, "PlayerTribe", StringComparison.Ordinal);
    }

    private static void AddSnapshotFactionId(HashSet<string> result, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            result.Add(value);
        }
    }

    private static WorldObjectSummary? FindSnapshotWorldObjectById(
        IEnumerable<WorldObjectSummary> worldObjects,
        string worldObjectId)
    {
        return worldObjects.FirstOrDefault(candidate =>
            string.Equals(candidate.UniqueLoadId, worldObjectId, StringComparison.Ordinal)
            || string.Equals(candidate.Id, worldObjectId, StringComparison.Ordinal));
    }

    private static bool IsSnapshotSettlement(WorldObjectSummary worldObject)
    {
        return WorldObjectTypeIdentity.IsSettlement(worldObject);
    }

    private static bool IsSnapshotPlayerColonyAnchor(
        MapSummary map,
        WorldObjectSummary worldObject,
        IReadOnlyList<IWorldObjectClassifier> worldObjectClassifiers,
        IReadOnlySet<string> playerFactionIds)
    {
        if (IsSnapshotSettlement(worldObject))
        {
            return HasSnapshotPlayerColonyEvidence(map, worldObject, playerFactionIds);
        }

        return map.WasSpawnedViaGravshipLanding
            && HasSnapshotPlayerColonyEvidence(map, worldObject, playerFactionIds)
            && worldObjectClassifiers.Any(classifier => classifier.IsPlayerColonyAnchor(worldObject));
    }

    private static bool HasSnapshotPlayerColonyEvidence(
        MapSummary map,
        WorldObjectSummary worldObject,
        IReadOnlySet<string> playerFactionIds)
    {
        return map.PlayerColonistCount > 0
            || (!string.IsNullOrWhiteSpace(worldObject.Faction)
                && playerFactionIds.Contains(worldObject.Faction!));
    }

    private static bool TryParseSnapshotTile(string? value, out int tile, out int tileLayerId)
    {
        tile = default;
        tileLayerId = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value.Split(',', 2);
        if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out tile) || tile < 0)
        {
            return false;
        }

        if (parts.Length > 1
            && (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out tileLayerId)
                || tileLayerId < 0))
        {
            return false;
        }

        return true;
    }

    private static string? NormalizeSnapshotMapUniqueId(string? mapUniqueId)
    {
        if (string.IsNullOrWhiteSpace(mapUniqueId))
        {
            return null;
        }

        return mapUniqueId!.StartsWith("Map_", StringComparison.Ordinal)
            ? mapUniqueId
            : "Map_" + mapUniqueId;
    }

    private static SnapshotUploadResult ReceiveSnapshot(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        string snapshotId,
        SnapshotPackageMetadataDto package,
        byte[] payload,
        DateTimeOffset acceptedAtUtc,
        bool storeAccepted = true,
        bool validateGameplayContinuity = true,
        string? confirmationOperation = null,
        string? snapshotUploadKind = null,
        string? requiredRaidEventId = null,
        bool allowRaidSettlementEvidenceSnapshotKind = false)
        => ReceiveSnapshotCore(state, userId, colonyId, snapshotId, package, payload, acceptedAtUtc, storeAccepted,
            validateGameplayContinuity, confirmationOperation, snapshotUploadKind, requiredRaidEventId, allowRaidSettlementEvidenceSnapshotKind);

    private static SnapshotUploadResult ReceiveSnapshotCore(
        ClashOfRimNetworkState state, string userId, string colonyId, string snapshotId, SnapshotPackageMetadataDto package,
        byte[] payload, DateTimeOffset acceptedAtUtc, bool storeAccepted = true, bool validateGameplayContinuity = true,
        string? confirmationOperation = null, string? snapshotUploadKind = null, string? requiredRaidEventId = null,
        bool allowRaidSettlementEvidenceSnapshotKind = false, SnapshotPostUploadExtraData? postUploadData = null,
        Func<string?>? validateCommit = null, SnapshotConfirmationRequest? confirmation = null,
        Func<string?>? authorizeCommit = null)
    {
        var receiver = CreateSnapshotReceiver(state);
        string normalizedSnapshotUploadKind = NormalizeSnapshotUploadKind(
            snapshotUploadKind ?? package.SnapshotUploadKind, confirmationOperation);
        if (!allowRaidSettlementEvidenceSnapshotKind
            && string.Equals(normalizedSnapshotUploadKind, SnapshotUploadKinds.RaidSettlementEvidence, StringComparison.Ordinal))
        {
            return SnapshotUploadResult.Reject(SnapshotUploadResultKind.InvalidPayload,
                "Raid settlement evidence snapshots are only accepted by the raid settlement confirmation endpoint.");
        }
        var context = new SnapshotUploadContext(userId, colonyId, snapshotId, confirmationOperation,
            normalizedSnapshotUploadKind, requiredRaidEventId);
        SaveSnapshotPackage savePackage = ProtocolDtoMapper.ToSaveSnapshotPackage(package, payload);
        SnapshotUploadResult prepared = SnapshotPreparationService.Prepare(receiver, context, savePackage, acceptedAtUtc);
        if (!prepared.Accepted) return prepared;
        // Authorization is safe to repeat before/after recovery; business validation must
        // read recovered receipts and current state only after the recovery scope is acquired.
        using (var mutation = EnterRecoveredSnapshotMutation(state, () => authorizeCommit?.Invoke() is { } failure
            ? ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, failure) : null))
        {
            if (mutation.Rejection is { } denied)
                return SnapshotUploadResult.Reject(SnapshotUploadResultKind.IdentityMismatch, denied.Message ?? T("Auth.Failed"));
            if (state.SnapshotCommits.IsApplying)
                throw new InvalidOperationException("Cannot publish a nested snapshot while a commit is being applied.");
            if (validateCommit?.Invoke() is { } authFailure)
                return SnapshotUploadResult.Reject(SnapshotUploadResultKind.IdentityMismatch, authFailure);
            if (postUploadData is not null)
            {
                if (confirmation is not null && state.SnapshotCommits.FindConfirmationReceipt(prepared.AcceptedSnapshot!.Envelope) is { } receipt)
                {
                    return confirmation.Matches(receipt.Request)
                        && prepared.AcceptedSnapshot.Envelope.OriginalSha256 == receipt.Envelope.OriginalSha256
                        && prepared.AcceptedSnapshot.Envelope.PayloadSha256 == receipt.Envelope.PayloadSha256
                        ? SnapshotUploadResult.Accept(new LatestSnapshotRecord(receipt.Envelope.Identity, receipt.Envelope,
                            prepared.AcceptedSnapshot.Index, receipt.AcceptedAtUtc), normalizedSnapshotUploadKind)
                        : SnapshotUploadResult.Reject(SnapshotUploadResultKind.SnapshotReplayDetected, "Snapshot confirmation differs from its original request.");
                }
                // A lost response must not create a new lineage token or run supplied
                // candidates again. Recovery above already used the original context.
                var latest = state.SnapshotStore.GetLatest(userId, colonyId);
                if (latest?.Identity.SnapshotId == snapshotId && confirmation is null)
                {
                    return latest.Envelope.OriginalSha256 == prepared.AcceptedSnapshot!.Envelope.OriginalSha256
                        && latest.Envelope.PayloadSha256 == prepared.AcceptedSnapshot.Envelope.PayloadSha256
                        ? SnapshotUploadResult.Accept(latest, normalizedSnapshotUploadKind)
                        : SnapshotUploadResult.Reject(SnapshotUploadResultKind.SnapshotReplayDetected,
                            "Snapshot identity was already accepted with different content.");
                }
            }
            var operations = PendingSnapshotOperations.Capture(state.BankLoans, state.MercenaryContracts, prepared.AcceptedSnapshot!);
            var upload = receiver.CommitPrepared(context, savePackage, prepared,
                storeAccepted: storeAccepted && operations.IsEmpty && postUploadData is null, validateGameplayContinuity);
            if (postUploadData is not null && upload.Accepted && upload.AcceptedSnapshot is not null)
            {
                state.SnapshotCommits.CommitUpload(state.SnapshotStore, state.BankLoans, state.MercenaryContracts,
                    savePackage, upload.AcceptedSnapshot, operations, upload.SnapshotUploadKind,
                    postUploadData.ConfirmationOperation, postUploadData.AchievementCandidates,
                    (snapshot, kind, mutation) => RunCommittedOrdinaryUpload(state, snapshot, kind, mutation), confirmation, state.Ledger,
                    item => RecordAuthoritativeEventAchievements(state, item, item.AppliedAtUtc ?? item.CreatedAtUtc),
                    (mutation, snapshot) => PlanSnapshotConfirmations(state, mutation, snapshot), preparedStoragePayload: upload.PreparedStoragePayload);
                return upload;
            }
            if (!storeAccepted || !upload.Accepted || upload.AcceptedSnapshot is null || operations.IsEmpty) return upload;
            state.SnapshotCommits.CommitPendingOperations(state.SnapshotStore, state.BankLoans, state.MercenaryContracts,
                savePackage, upload.AcceptedSnapshot, operations, upload.SnapshotUploadKind, preparedStoragePayload: upload.PreparedStoragePayload);
            state.MercenaryIncidents.Recover();
            return upload;
        }
    }

    private static SnapshotUploadReceiver CreateSnapshotReceiver(ClashOfRimNetworkState state)
        => state.SnapshotTransactions.CreateSnapshotReceiver();

    private static void RecordAcceptedSnapshotReference(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        SnapshotUploadResult upload,
        DateTimeOffset acceptedAtUtc)
    {
        if (!upload.Accepted || upload.AcceptedSnapshot is null)
        {
            return;
        }

        RecordLatestSnapshotReference(state, userId, colonyId, upload.AcceptedSnapshot, acceptedAtUtc);
    }

    private static void RecordLatestSnapshotReference(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        LatestSnapshotRecord snapshot,
        DateTimeOffset acceptedAtUtc)
    {
        state.Players.RecordLatestSnapshotReference(
            userId,
            colonyId,
            snapshot.Identity.SnapshotId,
            acceptedAtUtc);
    }

    private static string NormalizeSnapshotUploadKind(string? snapshotUploadKind, string? confirmationOperation = null)
        => SnapshotTransactions.NormalizeSnapshotUploadKind(snapshotUploadKind, confirmationOperation);

    private static int? CalculateSnapshotWealth(LatestSnapshotRecord snapshot)
    {
        float total = 0f;
        bool hasWealth = false;
        foreach (MapSummary map in snapshot.Index.Maps)
        {
            if (map.WealthTotal is null)
            {
                continue;
            }

            hasWealth = true;
            total += Math.Max(0f, map.WealthTotal.Value);
        }

        return hasWealth ? (int)Math.Round(total, MidpointRounding.AwayFromZero) : null;
    }

    private readonly record struct PlayerSnapshotWealthSummary(
        string ColonyId,
        string? CurrentSnapshotId,
        int? LatestSnapshotWealth);

    private static PlayerSnapshotWealthSummary CacheLatestSnapshotWealth(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        string? currentSnapshotId,
        DateTimeOffset nowUtc,
        LatestSnapshotLookup? snapshotLookup = null)
    {
        LatestSnapshotRecord? snapshot = snapshotLookup?.Resolve(userId, colonyId)
            ?? ResolveLatestSnapshotForPlayer(state, userId, colonyId);
        if (snapshot is null)
        {
            return new PlayerSnapshotWealthSummary(colonyId, currentSnapshotId, null);
        }

        string snapshotOwnerId = string.IsNullOrWhiteSpace(snapshot.Identity.OwnerId)
            ? userId
            : snapshot.Identity.OwnerId!;
        string snapshotColonyId = string.IsNullOrWhiteSpace(snapshot.Identity.ColonyId)
            ? colonyId
            : snapshot.Identity.ColonyId!;
        int? wealth = CalculateSnapshotWealth(snapshot)
            ?? CalculateLatestSnapshotWealthFromPackage(state, snapshotOwnerId, snapshotColonyId, snapshot.Identity);
        state.Players.RecordLatestSnapshotWealth(
            snapshotOwnerId,
            snapshotColonyId,
            snapshot.Identity.SnapshotId,
            wealth,
            nowUtc);
        return new PlayerSnapshotWealthSummary(snapshotColonyId, snapshot.Identity.SnapshotId, wealth);
    }

    private static LatestSnapshotRecord? ResolveLatestSnapshotForPlayer(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId)
    {
        LatestSnapshotRecord? exact = state.SnapshotStore.GetLatest(userId, colonyId);
        if (exact is not null)
        {
            return exact;
        }

        LatestSnapshotRecord? latest = null;
        foreach (LatestSnapshotRecord snapshot in state.SnapshotStore.ListLatest())
        {
            if (!string.Equals(snapshot.Identity.OwnerId, userId, StringComparison.Ordinal))
            {
                continue;
            }

            if (latest is null || snapshot.AcceptedAtUtc > latest.AcceptedAtUtc)
            {
                latest = snapshot;
            }
        }

        return latest;
    }

    private sealed class LatestSnapshotLookup
    {
        private readonly Dictionary<string, LatestSnapshotRecord> snapshotsByColony;
        private readonly Dictionary<string, LatestSnapshotRecord> latestSnapshotsByUser;

        private LatestSnapshotLookup(
            Dictionary<string, LatestSnapshotRecord> snapshotsByColony,
            Dictionary<string, LatestSnapshotRecord> latestSnapshotsByUser)
        {
            this.snapshotsByColony = snapshotsByColony;
            this.latestSnapshotsByUser = latestSnapshotsByUser;
        }

        public static LatestSnapshotLookup Build(IReadOnlyList<LatestSnapshotRecord> snapshots)
        {
            var byColony = new Dictionary<string, LatestSnapshotRecord>(StringComparer.Ordinal);
            var byUser = new Dictionary<string, LatestSnapshotRecord>(StringComparer.Ordinal);
            foreach (LatestSnapshotRecord snapshot in snapshots)
            {
                string? ownerId = snapshot.Identity.OwnerId;
                string? colonyId = snapshot.Identity.ColonyId;
                if (string.IsNullOrWhiteSpace(ownerId))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(colonyId))
                {
                    byColony[SnapshotLookupKey(ownerId!, colonyId!)] = snapshot;
                }

                if (!byUser.TryGetValue(ownerId!, out LatestSnapshotRecord? existing)
                    || snapshot.AcceptedAtUtc > existing.AcceptedAtUtc)
                {
                    byUser[ownerId!] = snapshot;
                }
            }

            return new LatestSnapshotLookup(byColony, byUser);
        }

        public LatestSnapshotRecord? Resolve(string userId, string colonyId)
        {
            if (!string.IsNullOrWhiteSpace(userId)
                && !string.IsNullOrWhiteSpace(colonyId)
                && snapshotsByColony.TryGetValue(SnapshotLookupKey(userId, colonyId), out LatestSnapshotRecord? exact))
            {
                return exact;
            }

            return !string.IsNullOrWhiteSpace(userId)
                && latestSnapshotsByUser.TryGetValue(userId, out LatestSnapshotRecord? latest)
                    ? latest
                    : null;
        }

        private static string SnapshotLookupKey(string userId, string colonyId)
        {
            return userId + "\u001f" + colonyId;
        }
    }

    private static int? CalculateLatestSnapshotWealthFromPackage(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        SnapshotIdentity identity)
    {
        if (state.SnapshotStore is not IColonySnapshotPackageStore packageStore)
        {
            return null;
        }

        SaveSnapshotPackage? package = packageStore.GetLatestPackage(userId, colonyId);
        if (package is null
            || !string.Equals(package.Envelope.Identity.SnapshotId, identity.SnapshotId, StringComparison.Ordinal))
        {
            return null;
        }

        return CalculateSnapshotWealth(new LatestSnapshotRecord(
            package.Envelope.Identity,
            package.Envelope,
            package.Index,
            DateTimeOffset.UtcNow));
    }

    private static void ProcessWorldTileFloatLayersFromAcceptedSnapshot(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        SnapshotUploadResult upload,
        DateTimeOffset acceptedAtUtc)
    {
        using var mutation = state.EnterSnapshotMutation();
        RecoverWorldLayerNotifications(state);
        if (upload.AcceptedSnapshot is null || state.WorldConfiguration.Current is null)
        {
            return;
        }

        if (state.SnapshotStore is not IColonySnapshotPackageStore packageStore)
        {
            return;
        }

        SnapshotIdentity identity = upload.AcceptedSnapshot.Identity;
        string ownerId = string.IsNullOrWhiteSpace(identity.OwnerId) ? userId : identity.OwnerId!;
        string acceptedColonyId = string.IsNullOrWhiteSpace(identity.ColonyId) ? colonyId : identity.ColonyId!;
        SaveSnapshotPackage? package = packageStore.GetLatestPackage(ownerId, acceptedColonyId);
        if (package is null
            || !string.Equals(
                package.Envelope.Identity.SnapshotId,
                upload.AcceptedSnapshot.Identity.SnapshotId,
                StringComparison.Ordinal)
            || package.Payload.Length == 0)
        {
            return;
        }

        try
        {
            byte[] saveBytes = DecodeSnapshotPayload(package.Payload, package.Envelope.PayloadEncoding.ToString());
            WorldConfigurationExtensionService activeWorldExtensions = ActiveWorldConfigurationExtensions(state);
            IReadOnlyList<WorldTileFloatLayerProjection> projections =
                activeWorldExtensions.ProjectConfirmOnAcceptedSnapshotLayersFromSave(saveBytes);
            if (projections.Count == 0)
            {
                return;
            }

            foreach (WorldTileFloatLayerProjection projection in projections)
            {
                state.WorldConfiguration.ConfirmWorldTileFloatLayerWithNotifications(
                        projection.LayerId,
                        projection.Values,
                        activeWorldExtensions,
                        increases => new WorldLayerNotificationWork(1, Guid.NewGuid().ToString("N"),
                            state.WorldConfiguration.Current!.WorldConfigurationId,
                            new EventParty(userId, colonyId, upload.AcceptedSnapshot.Identity.SnapshotId), projection.LayerId,
                            increases, BuildWorldTileFloatLayerIncreaseNotifications(state, userId, colonyId,
                                upload.AcceptedSnapshot.Identity.SnapshotId!, increases, acceptedAtUtc),
                            state.Players.List().Select(player => player.UserId).Append(userId).Distinct(StringComparer.Ordinal).ToArray()));
            }
            RecoverWorldLayerNotifications(state);
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or FormatException)
        {
            // World-layer extensions are optional compatibility details. Snapshot acceptance must not fail because one cannot be projected.
            Console.Error.WriteLine("[ClashOfRim] Failed to project world layers from accepted snapshot "
                + upload.AcceptedSnapshot.Identity.SnapshotId
                + ": "
                + ex.GetType().Name
                + " "
                + ex.Message);
        }
    }

    private static byte[] DecodeSnapshotPayload(byte[] payload, string payloadEncoding)
    {
        if (string.Equals(payloadEncoding, SnapshotPayloadEncoding.GzipRws.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            using var source = new MemoryStream(payload);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var target = new MemoryStream();
            gzip.CopyTo(target);
            return target.ToArray();
        }

        if (string.Equals(payloadEncoding, SnapshotPayloadEncoding.RawRws.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return payload;
        }

        throw new InvalidDataException("Unsupported snapshot payload encoding: " + payloadEncoding);
    }

    private static Task<MultipartSnapshotRequest<TRequest>?> ReadMultipartSnapshotRequest<TRequest>(HttpRequest httpRequest)
        => MultipartSnapshotReader.Read<TRequest>(httpRequest);

    private static ProtocolResponse ToProtocolResponse(SnapshotUploadResult upload)
        => SnapshotTransactions.ToProtocolResponse(upload);

    private static ProtocolResponse ToProtocolResponse(RaidAttackerLossConfirmationResult result)
    {
        if (result.Accepted)
        {
            return ProtocolResponse.Ok(ServerLocalization.Text("Event.ApplicationConfirmed"));
        }

        return ProtocolResponse.Reject(
            result.Kind == RaidAttackerLossConfirmationResultKind.LossNotReflected
                ? ProtocolErrorCode.LossNotReflected
                : ProtocolErrorCode.Conflict,
            result.FailureReason ?? T("Events.ApplicationConfirmationRejected"));
    }

    private static ProtocolResponse ToProtocolResponse(ItemDeliveryApplicationConfirmationResult result)
    {
        if (result.Accepted)
        {
            return ProtocolResponse.Ok(ServerLocalization.Text("Gift.ApplicationConfirmed"));
        }

        ProtocolErrorCode errorCode = result.Kind switch
        {
            ItemDeliveryApplicationConfirmationResultKind.EventNotFound => ProtocolErrorCode.EventNotFound,
            ItemDeliveryApplicationConfirmationResultKind.NotTarget => ProtocolErrorCode.EventNotFound,
            ItemDeliveryApplicationConfirmationResultKind.SnapshotIdentityMismatch => ProtocolErrorCode.IdentityMismatch,
            ItemDeliveryApplicationConfirmationResultKind.SnapshotBaseMismatch => ProtocolErrorCode.SnapshotMismatch,
            ItemDeliveryApplicationConfirmationResultKind.NotDelivered => ProtocolErrorCode.SnapshotMismatch,
            ItemDeliveryApplicationConfirmationResultKind.RejectedByTarget => ProtocolErrorCode.Conflict,
            ItemDeliveryApplicationConfirmationResultKind.NotAnchored => ProtocolErrorCode.Conflict,
            _ => ProtocolErrorCode.ValidationFailed
        };

        return ProtocolResponse.Reject(
            errorCode,
            result.FailureReason ?? T("Gift.ApplicationConfirmationRejected"));
    }

    private static ProtocolErrorCode ToProtocolError(SnapshotUploadResultKind kind)
        => SnapshotTransactions.ToProtocolError(kind);

    // Compatibility for domains scheduled for M6/M7; the package service owns the implementation.
    private static bool TryStoreInlinePawnPackages(ClashOfRimNetworkState state, ProtocolIdentity owner,
        string idempotencyPrefix, IReadOnlyList<ThingReferenceDto> things,
        out IReadOnlyList<ThingReferenceDto> storedThings, out ProtocolResponse? failure)
        => state.TransferPackages.TryStore(owner, idempotencyPrefix, things, out storedThings, out failure);

    private static EventTargetContext? ResolveLatestDeliveryTargetContext(
        ProtocolIdentity target, ClashOfRimNetworkState state, EventLandingMode landingMode)
        => state.DeliveryTargets.ResolveLatest(target, landingMode);

    private static bool IsVisibleTo(AuthoritativeEvent ledgerEvent, string userId, string colonyId)
        => EventVisibility.IsVisibleTo(ledgerEvent, userId, colonyId);

    private static bool IsAuthorizedForColony(ClashOfRimNetworkState state, string? authToken,
        string userId, string colonyId, string? authorizationEventId, string? authorizationScope,
        DateTimeOffset nowUtc, out string failureMessage)
        => state.ColonyAuthorization.TryAuthorize(authToken, userId, colonyId,
            authorizationEventId, authorizationScope, nowUtc, out failureMessage);

    private static bool AreAllied(ClashOfRimNetworkState state, string userA, string? colonyA, string userB, string? colonyB)
        => state.DiplomacyRelations.AreAllied(userA, colonyA, userB, colonyB);

    private static string ResolveDiplomacyRelationKind(
        ClashOfRimNetworkState state,
        string userA,
        string? colonyA,
        string userB,
        string? colonyB)
    {
        return state.DiplomacyRelations.GetRelationKind(userA, colonyA, userB, colonyB);
    }

    private static bool IsVisibleParty(EventParty party, string userId, string colonyId)
        => EventVisibility.MatchesParty(party, userId, colonyId);

    private sealed record CompatibilityHandshakeResult(
        ProtocolResponse Result,
        string? SteamId,
        string? DisplayName,
        string? ServerCompatibilityManifestJson,
        IReadOnlyList<CompatibilityIssueDto> CompatibilityIssues,
        bool CanOverrideCompatibilityBaseline,
        bool RequiresFullCompatibilityManifest,
        IReadOnlyList<string> RequestedCompatibilityPackageIds);

    private sealed record CompatibilitySummaryHandshake(
        bool Accepted,
        bool RequiresFullManifest,
        IReadOnlyList<string> RequestedPackageIds,
        IReadOnlyList<CompatibilityIssue> Issues);

    private sealed record SnapshotColonyAnchor(
        string? MapUniqueId,
        string? WorldObjectId,
        int Tile,
        int TileLayerId,
        string? Label);

}
