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
    private const string PawnMetadataThingDef = "clashofrim.pawn.thingDef";
    private const string PawnMetadataPawnKindDef = "clashofrim.pawn.pawnKindDef";
    private const string PawnMetadataGender = "clashofrim.pawn.gender";
    private const string PawnMetadataBiologicalAgeTicks = "clashofrim.pawn.biologicalAgeTicks";

    private static readonly TimeSpan DeliveredEventLease = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions MultipartJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions CompatibilityJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly IReadOnlyList<ClientLifecycleHook> ClientLifecycleHooks = new ClientLifecycleHook[]
    {
        ReconcileRaidTimeoutsOnClientLifecycle,
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
        app.MapGet("/server/plugins", ListServerPlugins);
        app.MapGet(ProtocolContractManifest.Find(ProtocolMessageKind.ServerHello).Route, ServerHello);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.Login).Route, Login);
        app.MapGet(ProtocolContractManifest.Find(ProtocolMessageKind.StreamSession).Route, StreamSession);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.MaintainPresence).Route, MaintainPresence);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.Logout).Route, Logout);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ChangeOfflinePassword).Route, ChangeOfflinePassword);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ListPlayers).Route, ListPlayers);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ListAchievements).Route, ListAchievements);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.WaitForEvents).Route, WaitForEvents);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.UploadSnapshot).Route, UploadSnapshot);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.DownloadLatestSnapshot).Route, DownloadLatestSnapshot);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.DownloadLatestSnapshotPayload).Route, DownloadLatestSnapshotPayload);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.PullPendingEvents).Route, PullPendingEvents);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.PullEventDetails).Route, PullEventDetails);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmEventApplication).Route, ConfirmEventApplication);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmEventApplications).Route, ConfirmEventApplications);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ReportEventApplicationFailure).Route, ReportEventApplicationFailure);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateGift).Route, CreateGift);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateGiftWithSnapshot).Route, CreateGiftWithSnapshot);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RejectGift).Route, RejectGift);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.StorePawnPackage).Route, StorePawnPackage);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetPawnPackage).Route, GetPawnPackage);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.StoreThingPackage).Route, StoreThingPackage);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetThingPackage).Route, GetThingPackage);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.QuoteTradeOrderFee).Route, QuoteTradeOrderFee);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateTradeOrder).Route, CreateTradeOrder);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateTradeOrderWithSnapshot).Route, CreateTradeOrderWithSnapshot);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ListTradeOrders).Route, ListTradeOrders);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.AcceptTradeOrder).Route, AcceptTradeOrder);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.FulfillTradeOrder).Route, FulfillTradeOrder);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.FulfillTradeOrderWithSnapshot).Route, FulfillTradeOrderWithSnapshot);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CancelTradeOrder).Route, CancelTradeOrder);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CompleteTradeOrder).Route, CompleteTradeOrder);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ListServerShop).Route, ListServerShop);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.UpsertServerShopListing).Route, UpsertServerShopListing);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RemoveServerShopListing).Route, RemoveServerShopListing);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.PurchaseServerShopListingWithSnapshot).Route, PurchaseServerShopListingWithSnapshot);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.PrepareRaid).Route, PrepareRaid);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateRaid).Route, CreateRaid);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateRaidWithSnapshot).Route, CreateRaidWithSnapshot);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateDiplomacyEvent).Route, CreateDiplomacyEvent);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RespondDiplomacyEvent).Route, RespondDiplomacyEvent);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateSupportPawn).Route, CreateSupportPawn);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateSupportPawnWithSnapshot).Route, CreateSupportPawnWithSnapshot);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RejectSupportPawn).Route, RejectSupportPawn);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.FinishSupportPawn).Route, FinishSupportPawn);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.SyncWorldMapMarkers).Route, SyncWorldMapMarkers);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.SyncRuntimeWorldObjects).Route, SyncRuntimeWorldObjects);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.PrepareWorldSession).Route, PrepareWorldSession);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetWorldConfiguration).Route, GetWorldConfiguration);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.SubmitWorldConfiguration).Route, SubmitWorldConfiguration);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.UploadWorldSubstrate).Route, UploadWorldSubstrate);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.DownloadWorldSubstrate).Route, DownloadWorldSubstrate);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.SubmitWorldFeatureNames).Route, SubmitWorldFeatureNames);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RegisterPlayerColonySites).Route, RegisterPlayerColonySites);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.PreflightColonyRelocation).Route, PreflightColonyRelocation);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmColonyRelocation).Route, ConfirmColonyRelocation);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.AbandonPlayerColony).Route, AbandonPlayerColony);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetAdminBaselineRequirements).Route, GetAdminBaselineRequirements);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.SubmitAdminBaseline).Route, SubmitAdminBaseline);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetBankStatus).Route, GetBankStatus);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateBankLoanWithSnapshot).Route, CreateBankLoanWithSnapshot);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateBankLoan).Route, CreateBankLoan);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RepayBankLoanWithSnapshot).Route, RepayBankLoanWithSnapshot);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RepayBankLoan).Route, RepayBankLoan);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RepayBankDebtWithSnapshot).Route, RepayBankDebtWithSnapshot);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RepayBankDebt).Route, RepayBankDebt);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.QuoteMercenary).Route, QuoteMercenary);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.HireMercenaryWithSnapshot).Route, HireMercenaryWithSnapshot);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.HireMercenary).Route, HireMercenary);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.QuoteMercenaryGuard).Route, QuoteMercenaryGuard);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.HireMercenaryGuardWithSnapshot).Route, HireMercenaryGuardWithSnapshot);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ReportMercenaryIncident).Route, ReportMercenaryIncident);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.SendChatMessage).Route, SendChatMessage);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ListChatMessages).Route, ListChatMessages);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.OverrideCompatibilityBaseline).Route, OverrideCompatibilityBaseline);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.AdminStatus).Route, AdminStatus);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.AdminUpdateConfiguration).Route, AdminUpdateConfiguration);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.AdminAction).Route, AdminAction);
    }

    private static string T(string key)
    {
        return ServerLocalization.Text(key);
    }

    private static string T(string key, params (string Key, string? Value)[] args)
    {
        return ServerLocalization.Text(
            key,
            args.ToDictionary(arg => arg.Key, arg => arg.Value, StringComparer.Ordinal));
    }

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

    private static void LogEventAppend(
        ClashOfRimNetworkState state,
        LedgerAppendResult append,
        string source)
    {
        AuthoritativeEvent evt = append.Event;
        state.RuntimeLogger.LogInformation(
            "Event {Action}: source={Source} type={Type} id={EventId} status={Status} delivery={DeliveryMode} actor={ActorUser}/{ActorColony} target={TargetUser}/{TargetColony} rejection={RejectionPolicy} result={Result} payload={PayloadType}",
            append.Created ? "created" : "duplicate",
            source,
            evt.Type,
            evt.EventId,
            evt.Status,
            evt.DeliveryMode,
            evt.Actor.UserId,
            evt.Actor.ColonyId,
            evt.Target.UserId,
            evt.Target.ColonyId,
            evt.RejectionPolicy,
            evt.LastApplicationResult,
            evt.Payload.GetType().Name);
    }

    private static void ReconcileExpiredDeliveredEvents(
        ClashOfRimNetworkState state,
        string userId,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        DateTimeOffset leaseDeadline = nowUtc - DeliveredEventLease;
        bool userOnline = state.OnlinePresence.IsUserOnline(userId);
        bool changed = false;
        foreach (AuthoritativeEvent ledgerEvent in state.Ledger.ListForUser(userId)
            .Where(evt => evt.Target.UserId == userId)
            .Where(evt => evt.Status == ServerEventStatus.DeliveredToClient)
            .Where(evt => evt.DeliveredAtUtc is not null && evt.DeliveredAtUtc <= leaseDeadline)
            .ToList())
        {
            if (EventSemanticClassifier.IsOnlineOnlyServerNotification(ledgerEvent))
            {
                state.Ledger.ChangeStatus(ledgerEvent.EventId, ServerEventStatus.Cancelled);
                changed = true;
                continue;
            }

            if (EventSemanticClassifier.IsSnapshotlessNotification(ledgerEvent))
            {
                state.Ledger.MarkAccepted(
                    ledgerEvent.EventId,
                    DateTimeOffset.UtcNow,
                    T("Events.ServerNotificationDelivered"));
                changed = true;
                continue;
            }

            ServerEventStatus nextStatus = userOnline
                ? ServerEventStatus.ReadyForImmediateDelivery
                : ServerEventStatus.PendingOfflineDelivery;
            state.Ledger.ChangeStatus(ledgerEvent.EventId, nextStatus);
            changed = true;
        }

        if (changed && userOnline)
        {
            state.EventNotifications.SignalUser(userId);
        }
    }

    private static bool IsSnapshotlessServerNotification(AuthoritativeEvent ledgerEvent)
    {
        return EventSemanticClassifier.IsSnapshotlessNotification(ledgerEvent);
    }

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
    {
        var receiver = new SnapshotUploadReceiver(
            state.SnapshotStore,
            state.SnapshotUploadPolicy,
            state.Plugins.ActiveSaveIndexExtensions(state.CompatibilityBaseline.Current),
            state.Plugins
                .ActiveWorldObjectClassifiers(state.CompatibilityBaseline.Current)
                .Select(classifier => (Func<WorldObjectSummary, bool>)classifier.IsPlayerColonyAnchor)
                .ToList());
        lock (state.RaidSettlementSnapshotMutationGate)
        {
            string normalizedSnapshotUploadKind = NormalizeSnapshotUploadKind(
                snapshotUploadKind ?? package.SnapshotUploadKind,
                confirmationOperation);
            if (!allowRaidSettlementEvidenceSnapshotKind
                && string.Equals(normalizedSnapshotUploadKind, SnapshotUploadKinds.RaidSettlementEvidence, StringComparison.Ordinal))
            {
                return SnapshotUploadResult.Reject(
                    SnapshotUploadResultKind.InvalidPayload,
                    "Raid settlement evidence snapshots are only accepted by the raid settlement confirmation endpoint.");
            }

            SnapshotUploadResult result = receiver.Receive(
                new SnapshotUploadContext(
                    userId,
                    colonyId,
                    snapshotId,
                    confirmationOperation,
                    normalizedSnapshotUploadKind,
                    requiredRaidEventId),
                ProtocolDtoMapper.ToSaveSnapshotPackage(package, payload),
                acceptedAtUtc,
                storeAccepted,
                validateGameplayContinuity);
            return result;
        }
    }

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
    {
        if (!string.IsNullOrWhiteSpace(snapshotUploadKind))
        {
            return snapshotUploadKind!;
        }

        if (string.Equals(confirmationOperation, SnapshotConfirmationOperations.ColonyRelocation, StringComparison.Ordinal))
        {
            return SnapshotUploadKinds.ColonyRelocation;
        }

        if (string.Equals(confirmationOperation, SnapshotConfirmationOperations.EndgameAchievement, StringComparison.Ordinal))
        {
            return SnapshotUploadKinds.EndgameAchievement;
        }

        return SnapshotUploadKinds.ManualUpload;
    }

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
        if (upload.AcceptedSnapshot is null)
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

            bool changed = false;
            foreach (WorldTileFloatLayerProjection projection in projections)
            {
                IReadOnlyList<WorldTileFloatLayerIncrease> increases =
                    state.WorldConfiguration.ConfirmWorldTileFloatLayer(
                        projection.LayerId,
                        projection.Values,
                        activeWorldExtensions);
                if (increases.Count == 0)
                {
                    continue;
                }

                changed = true;
                CreateWorldTileFloatLayerIncreaseNotifications(
                    state,
                    userId,
                    colonyId,
                    upload.AcceptedSnapshot.Identity.SnapshotId ?? string.Empty,
                    increases,
                    acceptedAtUtc);
            }

            if (changed)
            {
                SignalWorldConfigurationChanged(state, userId);
            }
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

    private static async Task<MultipartSnapshotRequest<TRequest>?> ReadMultipartSnapshotRequest<TRequest>(HttpRequest httpRequest)
    {
        if (!httpRequest.HasFormContentType)
        {
            return null;
        }

        IFormCollection form = await httpRequest.ReadFormAsync();
        string? requestJson = form["request"].FirstOrDefault();
        IFormFile? payloadFile = form.Files.GetFile("payload");
        if (string.IsNullOrWhiteSpace(requestJson) || payloadFile is null)
        {
            return null;
        }

        TRequest? request = JsonSerializer.Deserialize<TRequest>(requestJson, MultipartJsonOptions);
        if (payloadFile.Length < 0 || payloadFile.Length > int.MaxValue)
        {
            return null;
        }

        await using Stream stream = payloadFile.OpenReadStream();
        byte[] payload = new byte[(int)payloadFile.Length];
        int offset = 0;
        while (offset < payload.Length)
        {
            int read = await stream.ReadAsync(payload.AsMemory(offset, payload.Length - offset));
            if (read == 0)
            {
                return null;
            }

            offset += read;
        }

        return new MultipartSnapshotRequest<TRequest>(request, payload);
    }

    private static async Task<TRequest?> ReadJsonRequest<TRequest>(HttpRequest httpRequest)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<TRequest>(httpRequest.Body, MultipartJsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (NotSupportedException)
        {
            return default;
        }
    }

    private sealed record MultipartSnapshotRequest<TRequest>(TRequest? Request, byte[] Payload);

    private static void ProcessSupportPawnDeathsFromSnapshot(
        ClashOfRimNetworkState state,
        SnapshotUploadResult upload)
    {
        if (!upload.Accepted || upload.AcceptedSnapshot is null)
        {
            return;
        }

        SnapshotIdentity identity = upload.AcceptedSnapshot.Identity;
        if (string.IsNullOrWhiteSpace(identity.OwnerId) || string.IsNullOrWhiteSpace(identity.ColonyId))
        {
            return;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        foreach (AuthoritativeEvent supportEvent in state.Ledger.ListForUser(identity.OwnerId!)
            .Where(evt => evt.Type == ServerEventType.SupportPawn)
            .Where(evt => evt.Status is ServerEventStatus.PendingOfflineDelivery
                or ServerEventStatus.ReadyForImmediateDelivery
                or ServerEventStatus.DeliveredToClient)
            .Where(evt => IsVisibleParty(evt.Target, identity.OwnerId!, identity.ColonyId!)))
        {
            if (supportEvent.Payload is not SupportPawnEventPayload payload
                || payload.ReturnToSender
                || !payload.TemporaryControl)
            {
                continue;
            }

            string? localId = ExtractPawnLocalId(payload.PawnGlobalKey);
            if (string.IsNullOrWhiteSpace(localId))
            {
                continue;
            }

            PawnSummary? pawn = upload.AcceptedSnapshot.Index.Pawns.FirstOrDefault(candidate =>
                string.Equals(candidate.LocalId, localId, StringComparison.Ordinal)
                || candidate.GlobalKey.EndsWith("/pawn:" + localId, StringComparison.Ordinal));
            if (pawn?.Dead != true)
            {
                continue;
            }

            AuthoritativeEvent lossEvent = CreateSupportPawnLossReturnEvent(
                supportEvent,
                payload,
                pawn.Name ?? payload.PawnName,
                "SnapshotDeath",
                state.OnlinePresence.IsUserOnline(supportEvent.Actor.UserId),
                nowUtc);
            LedgerAppendResult append = state.Ledger.Append(lossEvent);
            state.Ledger.ChangeStatus(supportEvent.EventId, ServerEventStatus.Failed);
            SignalIfCreated(state, append, supportEvent.Actor.UserId);
        }
    }

    private static ProtocolResponse ToProtocolResponse(SnapshotUploadResult upload)
    {
        return upload.Accepted
            ? ProtocolResponse.Ok(upload.Message)
            : ProtocolResponse.Reject(ToProtocolError(upload.Kind), upload.Message);
    }

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
    {
        return kind switch
        {
            SnapshotUploadResultKind.Accepted => ProtocolErrorCode.None,
            SnapshotUploadResultKind.IdentityMismatch => ProtocolErrorCode.IdentityMismatch,
            SnapshotUploadResultKind.MissingIdentity => ProtocolErrorCode.IdentityMismatch,
            SnapshotUploadResultKind.IncompatibleRimWorldVersion => ProtocolErrorCode.ServerRejected,
            SnapshotUploadResultKind.SnapshotReplayDetected => ProtocolErrorCode.ServerRejected,
            SnapshotUploadResultKind.SnapshotLineageMismatch => ProtocolErrorCode.ServerRejected,
            SnapshotUploadResultKind.SnapshotTimeRegression => ProtocolErrorCode.ServerRejected,
            SnapshotUploadResultKind.SnapshotContinuityMismatch => ProtocolErrorCode.ServerRejected,
            _ => ProtocolErrorCode.ValidationFailed
        };
    }

    private static EventCreationResponse ToEventCreationResponse(
        LedgerAppendResult append,
        ProtocolDeliverySemantics deliverySemantics,
        string createdMessage)
    {
        return new EventCreationResponse(
            append.Created
                ? ProtocolResponse.Ok(createdMessage)
                : new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Events.DuplicateCreation")),
            append.Event.EventId,
            deliverySemantics);
    }

    private static EventParty ToEventParty(ProtocolIdentity identity)
    {
        return new EventParty(identity.UserId, identity.ColonyId);
    }

    private static EventThingReference ToEventThingReference(ThingReferenceDto thing, string? sourceSnapshotId)
    {
        return new EventThingReference(
            thing.GlobalKey,
            thing.DefName,
            thing.StackCount,
            sourceSnapshotId,
            thing.Quality,
            thing.HitPoints,
            thing.MinifiedInnerDefName,
            thing.MinifiedInnerStuffDefName,
            thing.MinifiedInnerQuality,
            thing.MinifiedInnerHitPoints,
            thing.WornByCorpse,
            thing.Biocoded,
            thing.BiocodedPawnLabel,
            thing.BiocodedPawnGlobalId,
            thing.DisplayLabel,
            thing.MarketValue,
            thing.UniqueWeapon,
            thing.UniqueWeaponTraits,
            ToPawnExchangePackage(thing.PawnPackage),
            thing.PawnPackageId,
            thing.StuffDefName,
            thing.MaxHitPoints,
            thing.MinifiedInnerMaxHitPoints,
            Metadata: CopyMetadata(thing.Metadata),
            ThingPackage: ToThingStatePackage(thing.ThingPackage),
            ThingPackageId: thing.ThingPackageId);
    }

    private static bool TryStoreInlinePawnPackages(
        ClashOfRimNetworkState state,
        ProtocolIdentity owner,
        string idempotencyPrefix,
        IReadOnlyList<ThingReferenceDto> things,
        out IReadOnlyList<ThingReferenceDto> storedThings,
        out ProtocolResponse? failure)
    {
        storedThings = Array.Empty<ThingReferenceDto>();
        failure = null;
        if (!TryValidateInlinePawnPackages(things, out failure)
            || !TryValidateInlineThingPackages(things, out failure))
        {
            return false;
        }

        storedThings = things.Select((thing, index) =>
        {
            StoredThingPackageRecord? thingPackageRecord = null;
            if (thing.ThingPackage is not null)
            {
                if (!TryToThingStatePackage(thing.ThingPackage, out ThingStatePackage? thingPackage, out string thingPackageFailure)
                    || thingPackage is null)
                {
                    throw new InvalidOperationException(thingPackageFailure);
                }

                thingPackageRecord = state.ThingPackages.Store(
                    idempotencyPrefix + ":thing:" + index + ":" + thing.GlobalKey,
                    owner.UserId,
                    owner.ColonyId,
                    owner.SnapshotId,
                    thingPackage,
                    DateTimeOffset.UtcNow);
            }

            if (thing.PawnPackage is null)
            {
                return thingPackageRecord is null
                    ? thing
                    : CloneThingReferenceWithStoredPackages(
                        thing,
                        pawnPackageId: thing.PawnPackageId,
                        thingPackageId: string.IsNullOrWhiteSpace(thing.ThingPackageId)
                            ? thingPackageRecord.PackageId
                            : thing.ThingPackageId,
                        metadata: CopyMetadata(thing.Metadata));
            }

            if (!TryToPawnExchangePackage(thing.PawnPackage, out PawnExchangePackage? package, out string packageFailure)
                || package is null)
            {
                throw new InvalidOperationException(packageFailure);
            }

            StoredPawnPackageRecord record = state.PawnPackages.Store(
                idempotencyPrefix + ":" + index + ":" + thing.GlobalKey,
                owner.UserId,
                owner.ColonyId,
                owner.SnapshotId,
                package,
                DateTimeOffset.UtcNow);

            Dictionary<string, string?> metadata = EnrichPawnSummaryMetadata(thing.Metadata, package);
            return new ThingReferenceDto(
                thing.GlobalKey,
                thing.DefName,
                thing.StackCount,
                thing.Quality,
                thing.HitPoints,
                thing.MinifiedInnerDefName,
                thing.MinifiedInnerStuffDefName,
                thing.MinifiedInnerQuality,
                thing.MinifiedInnerHitPoints,
                thing.WornByCorpse,
                thing.Biocoded,
                thing.BiocodedPawnLabel,
                thing.BiocodedPawnGlobalId,
                thing.DisplayLabel,
                thing.MarketValue,
                thing.UniqueWeapon,
                thing.UniqueWeaponTraits,
                pawnPackage: null,
                pawnPackageId: string.IsNullOrWhiteSpace(thing.PawnPackageId) ? record.PackageId : thing.PawnPackageId,
                stuffDefName: thing.StuffDefName,
                maxHitPoints: thing.MaxHitPoints,
                minifiedInnerMaxHitPoints: thing.MinifiedInnerMaxHitPoints,
                metadata: metadata,
                thingPackage: null,
                thingPackageId: thingPackageRecord is null
                    ? thing.ThingPackageId
                    : string.IsNullOrWhiteSpace(thing.ThingPackageId)
                        ? thingPackageRecord.PackageId
                        : thing.ThingPackageId);
        }).ToList();
        return true;
    }

    private static ThingReferenceDto CloneThingReferenceWithStoredPackages(
        ThingReferenceDto thing,
        string? pawnPackageId,
        string? thingPackageId,
        Dictionary<string, string?> metadata)
    {
        return new ThingReferenceDto(
            thing.GlobalKey,
            thing.DefName,
            thing.StackCount,
            thing.Quality,
            thing.HitPoints,
            thing.MinifiedInnerDefName,
            thing.MinifiedInnerStuffDefName,
            thing.MinifiedInnerQuality,
            thing.MinifiedInnerHitPoints,
            thing.WornByCorpse,
            thing.Biocoded,
            thing.BiocodedPawnLabel,
            thing.BiocodedPawnGlobalId,
            thing.DisplayLabel,
            thing.MarketValue,
            thing.UniqueWeapon,
            thing.UniqueWeaponTraits,
            pawnPackage: null,
            pawnPackageId: pawnPackageId,
            stuffDefName: thing.StuffDefName,
            maxHitPoints: thing.MaxHitPoints,
            minifiedInnerMaxHitPoints: thing.MinifiedInnerMaxHitPoints,
            metadata: metadata,
            thingPackage: null,
            thingPackageId: thingPackageId);
    }

    private static bool TryValidateInlinePawnPackages(
        IReadOnlyList<ThingReferenceDto> things,
        out ProtocolResponse? failure)
    {
        failure = null;
        foreach (ThingReferenceDto thing in things)
        {
            if (thing.PawnPackage is null)
            {
                continue;
            }

            if (!TryToPawnExchangePackage(thing.PawnPackage, out _, out string packageFailure))
            {
                failure = ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    T("PawnPackage.Invalid", ("MESSAGE", packageFailure)));
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateInlineThingPackages(
        IReadOnlyList<ThingReferenceDto> things,
        out ProtocolResponse? failure)
    {
        failure = null;
        foreach (ThingReferenceDto thing in things)
        {
            if (!ThingTransferPolicy.IsAcceptedConcreteReference(thing, out string policyFailure))
            {
                failure = ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    T("ThingTransfer.Invalid", ("MESSAGE", policyFailure)));
                return false;
            }

            if (thing.ThingPackage is null)
            {
                continue;
            }

            if (!TryToThingStatePackage(thing.ThingPackage, out _, out string packageFailure))
            {
                failure = ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    "invalid thing package: " + packageFailure);
                return false;
            }
        }

        return true;
    }

    private static bool TryToPawnExchangePackage(
        PawnExchangePackageDto? dto,
        out PawnExchangePackage? package,
        out string failure)
    {
        package = null;
        failure = string.Empty;
        if (dto is null)
        {
            failure = T("PawnPackage.ParseFailed");
            return false;
        }

        if (dto.Reference is null || string.IsNullOrWhiteSpace(dto.Reference.GlobalId))
        {
            failure = "missing reference";
            return false;
        }

        if (dto.Identity is null)
        {
            failure = "missing identity";
            return false;
        }

        if (dto.Appearance is null)
        {
            failure = "missing appearance";
            return false;
        }

        if (dto.Status is null)
        {
            failure = "missing status";
            return false;
        }

        if (dto.Scribe is null || string.IsNullOrWhiteSpace(dto.Scribe.Xml))
        {
            failure = T("PawnPackage.MissingScribe");
            return false;
        }

        try
        {
            package = ToPawnExchangePackage(dto);
            if (package is null)
            {
                failure = T("PawnPackage.ParseFailed");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException)
        {
            failure = ex.Message;
            return false;
        }
    }

    private static ThingReferenceDto ToThingReferenceDto(EventThingReference thing)
    {
        return new ThingReferenceDto(
            thing.GlobalKey,
            thing.Def,
            thing.StackCount,
            thing.Quality,
            thing.HitPoints,
            thing.MinifiedInnerDefName,
            thing.MinifiedInnerStuffDefName,
            thing.MinifiedInnerQuality,
            thing.MinifiedInnerHitPoints,
            thing.WornByCorpse,
            thing.Biocoded,
            thing.BiocodedPawnLabel,
            thing.BiocodedPawnGlobalId,
            thing.DisplayLabel,
            thing.MarketValue,
            thing.UniqueWeapon,
            thing.UniqueWeaponTraits,
            ToPawnExchangePackageDto(thing.PawnPackage),
            thing.PawnPackageId,
            thing.StuffDefName,
            thing.MaxHitPoints,
            thing.MinifiedInnerMaxHitPoints,
            metadata: CopyMetadata(thing.Metadata),
            thingPackage: ToThingStatePackageDto(thing.ThingPackage),
            thingPackageId: thing.ThingPackageId);
    }

    private static ThingStatePackageDto? ToThingStatePackageDto(ThingStatePackage? package)
    {
        return package is null
            ? null
            : new ThingStatePackageDto(
                package.PackageVersion,
                package.GlobalKey,
                package.DefName,
                package.Label,
                package.StackCount,
                new ThingScribePayloadDto(
                    package.Scribe.XmlGzipBase64,
                    package.Scribe.XmlSha256,
                    package.Scribe.UncompressedBytes),
                package.Fingerprint);
    }

    private static PawnExchangePackageDto? ToPawnExchangePackageDto(PawnExchangePackage? package)
    {
        return package is null
            ? null
            : new PawnExchangePackageDto(
                package.PackageVersion,
                ToCrossMapPawnReferenceDto(package.Reference),
                new PawnExchangeIdentityDto(
                    package.Identity.ThingDef,
                    package.Identity.PawnKindDef,
                    package.Identity.FactionDef,
                    package.Identity.Gender),
                new PawnExchangeAppearanceDto(
                    package.Appearance.DisplayName,
                    package.Appearance.BodyTypeDef,
                    package.Appearance.HeadTypeDef,
                    package.Appearance.HairDef,
                    package.Appearance.BeardDef,
                    package.Appearance.SkinColor,
                    package.Appearance.HairColor),
                new PawnExchangeStatusDto(
                    package.Status.Dead,
                    package.Status.BiologicalAgeTicks,
                    package.Status.ChronologicalAgeTicks,
                    package.Status.DeathCauseDef,
                    package.Status.HealthState),
                package.Apparel.Select(ToPawnExchangeEquipmentItemDto).ToList(),
                package.Equipment.Select(ToPawnExchangeEquipmentItemDto).ToList(),
                package.Relationships.Select(ToPawnExchangeRelationshipStubDto).ToList(),
                ToPawnScribePayloadDto(package.Scribe),
                (package.Extensions ?? Array.Empty<PawnExchangeExtensionPackage>())
                    .Select(ToPawnExchangeExtensionPackageDto)
                    .ToList());
    }

    private static PawnExchangeExtensionPackageDto ToPawnExchangeExtensionPackageDto(PawnExchangeExtensionPackage extension)
    {
        return new PawnExchangeExtensionPackageDto(
            extension.ProviderId,
            extension.Kind,
            CopyMetadata(extension.Metadata),
            extension.PayloadJson);
    }

    private static CrossMapPawnReferenceDto ToCrossMapPawnReferenceDto(CrossMapPawnReference reference)
    {
        return new CrossMapPawnReferenceDto(
            reference.GlobalId,
            reference.SourceSnapshotId,
            reference.Name,
            reference.Dead,
            reference.Faction,
            CopyMetadata(reference.Metadata));
    }

    private static Dictionary<string, string?> CopyMetadata(IReadOnlyDictionary<string, string?>? metadata)
    {
        return metadata is null
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private static Dictionary<string, string?> EnrichPawnSummaryMetadata(
        IReadOnlyDictionary<string, string?>? source,
        PawnExchangePackage package)
    {
        Dictionary<string, string?> metadata = CopyMetadata(source);
        AddMetadataIfMissing(metadata, PawnMetadataThingDef, package.Identity.ThingDef);
        AddMetadataIfMissing(metadata, PawnMetadataPawnKindDef, package.Identity.PawnKindDef);
        AddMetadataIfMissing(metadata, PawnMetadataGender, package.Identity.Gender);
        AddMetadataIfMissing(
            metadata,
            PawnMetadataBiologicalAgeTicks,
            package.Status.BiologicalAgeTicks?.ToString(CultureInfo.InvariantCulture));
        return metadata;
    }

    private static void AddMetadataIfMissing(Dictionary<string, string?> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !metadata.ContainsKey(key))
        {
            metadata[key] = value;
        }
    }

    private static PawnExchangeEquipmentItemDto ToPawnExchangeEquipmentItemDto(PawnExchangeEquipmentItem item)
    {
        return new PawnExchangeEquipmentItemDto(
            item.GlobalId,
            item.Def,
            item.Label,
            item.StackCount,
            item.Quality,
            item.HitPoints,
            item.WornByCorpse,
            item.Biocoded,
            item.BiocodedPawnGlobalId,
            item.UniqueWeapon,
            item.UniqueWeaponName,
            item.UniqueWeaponTraits);
    }

    private static PawnExchangeRelationshipStubDto ToPawnExchangeRelationshipStubDto(PawnExchangeRelationshipStub stub)
    {
        return new PawnExchangeRelationshipStubDto(
            stub.OtherPawnGlobalId,
            stub.OtherPawnName,
            stub.OtherPawnDead,
            stub.RelationDef);
    }

    private static PawnScribePayloadDto? ToPawnScribePayloadDto(PawnScribePayload? scribe)
    {
        return scribe is null
            ? null
            : new PawnScribePayloadDto(
                scribe.Xml,
                scribe.XmlSha256,
                scribe.PawnReferenceReplacements.Select(ToPawnScribeReplacementDto).ToList());
    }

    private static PawnScribePawnReferenceReplacementDto ToPawnScribeReplacementDto(PawnScribePawnReferenceReplacement replacement)
    {
        return new PawnScribePawnReferenceReplacementDto(
            replacement.SourceLoadId,
            replacement.PlaceholderLoadId,
            ToCrossMapPawnReferenceDto(replacement.Reference));
    }

    private static CrossMapPawnReference? ToCrossMapPawnReference(CrossMapPawnReferenceDto? dto)
    {
        return dto is null
            ? null
            : new CrossMapPawnReference(
                dto.GlobalId,
                dto.SourceSnapshotId,
                dto.Name,
                dto.Dead,
                dto.Faction,
                dto.Metadata);
    }

    private static PawnExchangePackage? ToPawnExchangePackage(PawnExchangePackageDto? dto)
    {
        return dto is null
            ? null
            : new PawnExchangePackage(
                dto.PackageVersion,
                ToCrossMapPawnReference(dto.Reference)!,
                new PawnExchangeIdentity(
                    dto.Identity.ThingDef,
                    dto.Identity.PawnKindDef,
                    dto.Identity.FactionDef,
                    dto.Identity.Gender),
                new PawnExchangeAppearance(
                    dto.Appearance.DisplayName,
                    dto.Appearance.BodyTypeDef,
                    dto.Appearance.HeadTypeDef,
                    dto.Appearance.HairDef,
                    dto.Appearance.BeardDef,
                    dto.Appearance.SkinColor,
                    dto.Appearance.HairColor),
                new PawnExchangeStatus(
                    dto.Status.Dead,
                    dto.Status.BiologicalAgeTicks,
                    dto.Status.ChronologicalAgeTicks,
                    dto.Status.DeathCauseDef,
                    dto.Status.HealthState),
                (dto.Apparel ?? Array.Empty<PawnExchangeEquipmentItemDto>())
                    .Where(item => item is not null)
                    .Select(ToPawnExchangeEquipmentItem)
                    .ToList(),
                (dto.Equipment ?? Array.Empty<PawnExchangeEquipmentItemDto>())
                    .Where(item => item is not null)
                    .Select(ToPawnExchangeEquipmentItem)
                    .ToList(),
                (dto.Relationships ?? Array.Empty<PawnExchangeRelationshipStubDto>())
                    .Where(relationship => relationship is not null)
                    .Select(ToPawnExchangeRelationshipStub)
                    .ToList(),
                ToPawnScribePayload(dto.Scribe),
                (dto.Extensions ?? Array.Empty<PawnExchangeExtensionPackageDto>())
                    .Where(extension => extension is not null)
                    .Select(ToPawnExchangeExtensionPackage)
                    .ToList());
    }

    private static bool TryToThingStatePackage(
        ThingStatePackageDto? dto,
        out ThingStatePackage? package,
        out string failure)
    {
        package = null;
        failure = string.Empty;
        if (dto is null)
        {
            failure = "missing thing package";
            return false;
        }

        if (dto.Scribe is null)
        {
            failure = "missing thing scribe";
            return false;
        }

        try
        {
            package = ToThingStatePackage(dto);
            if (package is null)
            {
                failure = "thing package parse failed";
                return false;
            }

            SafeThingStatePackageSerializer.Serialize(package);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or IOException)
        {
            failure = ex.Message;
            return false;
        }
    }

    private static ThingStatePackage? ToThingStatePackage(ThingStatePackageDto? dto)
    {
        return dto?.Scribe is null
            ? null
            : new ThingStatePackage(
                dto.PackageVersion,
                dto.GlobalKey,
                dto.DefName,
                dto.Label,
                dto.StackCount,
                new ThingScribePayload(
                    dto.Scribe.XmlGzipBase64,
                    dto.Scribe.XmlSha256,
                    dto.Scribe.UncompressedBytes),
                dto.Fingerprint);
    }

    private static PawnExchangeExtensionPackage ToPawnExchangeExtensionPackage(PawnExchangeExtensionPackageDto dto)
    {
        return new PawnExchangeExtensionPackage(
            dto.ProviderId,
            dto.Kind,
            CopyMetadata(dto.Metadata),
            dto.PayloadJson);
    }

    private static PawnExchangeEquipmentItem ToPawnExchangeEquipmentItem(PawnExchangeEquipmentItemDto dto)
    {
        return new PawnExchangeEquipmentItem(
            dto.GlobalId,
            dto.Def,
            dto.Label,
            dto.StackCount,
            dto.Quality,
            dto.HitPoints,
            dto.WornByCorpse,
            dto.Biocoded,
            dto.BiocodedPawnGlobalId,
            dto.UniqueWeapon,
            dto.UniqueWeaponName,
            dto.UniqueWeaponTraits);
    }

    private static PawnExchangeRelationshipStub ToPawnExchangeRelationshipStub(PawnExchangeRelationshipStubDto dto)
    {
        return new PawnExchangeRelationshipStub(
            dto.OtherPawnGlobalId,
            dto.OtherPawnName,
            dto.OtherPawnDead,
            dto.RelationDef);
    }

    private static PawnScribePayload? ToPawnScribePayload(PawnScribePayloadDto? dto)
    {
        return dto is null
            ? null
            : new PawnScribePayload(
                dto.Xml,
                dto.XmlSha256,
                (dto.PawnReferenceReplacements ?? Array.Empty<PawnScribePawnReferenceReplacementDto>())
                    .Where(replacement => replacement is not null)
                    .Select(ToPawnScribeReplacement)
                    .ToList());
    }

    private static PawnScribePawnReferenceReplacement ToPawnScribeReplacement(PawnScribePawnReferenceReplacementDto dto)
    {
        return new PawnScribePawnReferenceReplacement(
            dto.SourceLoadId,
            dto.PlaceholderLoadId,
            ToCrossMapPawnReference(dto.Reference)!);
    }

    private static TradePostageQuoteDto BuildTradePostageQuote(
        ClashOfRimNetworkState state,
        AuthoritativeEvent tradeOrder,
        string viewerUserId,
        string viewerColonyId,
        string viewerSnapshotId)
    {
        if (tradeOrder.Payload is not TradeEventPayload payload
            || payload.FulfillmentMode is not TradeFulfillmentMode.ServerDropPod and not TradeFulfillmentMode.Unspecified)
        {
            return UnreachablePostage(T("Trade.PostageDropPodDisabled"));
        }

        TradeDeliveryEndpoint ownerEndpoint = ResolveTradeDeliveryEndpoint(
            state,
            tradeOrder.Actor.UserId,
            tradeOrder.Actor.ColonyId,
            snapshotId: null,
            tradeOrder.TargetContext);
        if (!ownerEndpoint.Available)
        {
            return UnreachablePostage(T("Trade.PostageOwnerUnavailable", ("STATUS", ownerEndpoint.Status)));
        }

        TradeDeliveryEndpoint viewerEndpoint = ResolveTradeDeliveryEndpoint(
            state,
            viewerUserId,
            viewerColonyId,
            viewerSnapshotId,
            preferredContext: null);
        if (!viewerEndpoint.Available)
        {
            return UnreachablePostage(T("Trade.PostageReceiverUnavailable", ("STATUS", viewerEndpoint.Status)));
        }

        if (ownerEndpoint.TileRef is not WorldTileRef fromTile || viewerEndpoint.TileRef is not WorldTileRef toTile)
        {
            return UnreachablePostage(T("Trade.PostageMissingTiles"));
        }

        ClashOfRimServerConfiguration configuration = state.ServerConfiguration;
        int? distance = fromTile == toTile
            ? 0
            : state.WorldTileDistanceCalculator.TryCalculateDistance(
                BuildWorldTileGeometryDistanceSource(state.WorldConfiguration.Current),
                fromTile,
                toTile,
                configuration.TradePostageCrossLayerOverheadDistanceTiles);
        if (distance is null)
        {
            return UnreachablePostage(T("Trade.PostageMissingGeometry"));
        }

        int postage = configuration.TradePostageBaseSilver
            + Math.Max(0, distance.Value) * configuration.TradePostageSilverPerTile;
        return new TradePostageQuoteDto(
            reachable: true,
            postageSilver: postage,
            distanceTiles: Math.Max(0, distance.Value),
            status: fromTile.LayerId == toTile.LayerId
                ? T("Trade.PostageSurfaceDistance")
                : T("Trade.PostageCrossLayerDistance"));
    }

    private static WorldTileGeometryDistanceSource? BuildWorldTileGeometryDistanceSource(WorldConfigurationDto? configuration)
    {
        WorldTileGeometryDto? geometry = configuration?.TileGeometry;
        if (geometry is null || geometry.Layers.Count == 0)
        {
            return null;
        }

        return new WorldTileGeometryDistanceSource(
            geometry.Layers
                .Where(layer => layer.AverageTileSize > 0f && layer.TileCenters.Count > 0)
                .Select(layer => new WorldTileLayerGeometry(
                    layer.LayerId,
                    layer.AverageTileSize,
                    layer.TileCenters.Select(center => new WorldTileCenter(
                        center.Tile,
                        center.X,
                        center.Y,
                        center.Z)))));
    }

    private static TradePostageQuoteDto UnreachablePostage(string status)
    {
        return new TradePostageQuoteDto(
            reachable: false,
            postageSilver: null,
            distanceTiles: null,
            status: status);
    }

    private static EventTargetContext? ResolveTradeOwnerTargetContext(
        CreateTradeOrderRequest request,
        ClashOfRimNetworkState state)
    {
        TradeDeliveryEndpoint endpoint = ResolveTradeDeliveryEndpoint(
            state,
            request.Owner.UserId,
            request.Owner.ColonyId,
            request.Owner.SnapshotId,
            preferredContext: null);
        if (endpoint.WorldObjectId is null && endpoint.MapUniqueId is null && endpoint.Tile is null)
        {
            return null;
        }

        return new EventTargetContext(
            endpoint.WorldObjectId,
            endpoint.MapUniqueId,
            endpoint.Tile,
            EventLandingMode.DropPod);
    }

    private static TradeDeliveryEndpoint ResolveTradeDeliveryEndpoint(
        ClashOfRimNetworkState state,
        string userId,
        string? colonyId,
        string? snapshotId,
        EventTargetContext? preferredContext)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(colonyId))
        {
            return TradeDeliveryEndpoint.Unavailable(T("Trade.EndpointMissingIdentity"));
        }

        LatestSnapshotRecord? snapshot = state.SnapshotStore.GetLatest(userId, colonyId!);
        if (snapshot is null)
        {
            return TradeDeliveryEndpoint.Unavailable(T("Trade.EndpointMissingSnapshot"));
        }

        if (!string.IsNullOrWhiteSpace(snapshotId)
            && !string.Equals(snapshot.Identity.SnapshotId, snapshotId, StringComparison.Ordinal))
        {
            return TradeDeliveryEndpoint.Unavailable(T("Trade.EndpointSnapshotMismatch"));
        }

        MapSummary? map = ResolveEndpointMap(snapshot.Index, preferredContext);
        WorldObjectSummary? worldObject = ResolveEndpointWorldObject(snapshot.Index, map, preferredContext);
        if (worldObject is null)
        {
            return preferredContext?.Tile is int contextTile
                ? TradeDeliveryEndpoint.Surface(
                    new WorldTileRef(contextTile, 0),
                    preferredContext.WorldObjectId,
                    preferredContext.MapUniqueId,
                    T("Trade.EndpointContextTile"))
                : TradeDeliveryEndpoint.Unavailable(T("Trade.EndpointMissingWorldObject"));
        }

        string? worldObjectId = worldObject.UniqueLoadId ?? worldObject.Id ?? preferredContext?.WorldObjectId;
        string? mapUniqueId = map?.UniqueId ?? preferredContext?.MapUniqueId;
        if (TryParsePlanetTile(worldObject.Tile, out WorldTileRef tile))
        {
            return IsOrbitalWorldObject(state, worldObject)
                ? TradeDeliveryEndpoint.Orbital(tile, worldObjectId, mapUniqueId, T("Trade.EndpointOrbital"))
                : TradeDeliveryEndpoint.Surface(tile, worldObjectId, mapUniqueId, T("Trade.EndpointSurface"));
        }

        if (IsOrbitalWorldObject(state, worldObject))
        {
            return TradeDeliveryEndpoint.Orbital(null, worldObjectId, mapUniqueId, T("Trade.EndpointOrbital"));
        }

        return string.IsNullOrWhiteSpace(worldObject.Tile)
            ? TradeDeliveryEndpoint.Unavailable(T("Trade.EndpointWorldObjectMissingTile"))
            : TradeDeliveryEndpoint.Unavailable(T("Trade.EndpointWorldObjectUnrecognizedTile"));
    }

    private static MapSummary? ResolveEndpointMap(SaveSnapshotIndex index, EventTargetContext? preferredContext)
    {
        if (!string.IsNullOrWhiteSpace(preferredContext?.MapUniqueId))
        {
            MapSummary? matched = index.Maps.FirstOrDefault(map =>
                string.Equals(map.UniqueId, preferredContext.MapUniqueId, StringComparison.Ordinal));
            if (matched is not null)
            {
                return matched;
            }
        }

        if (!string.IsNullOrWhiteSpace(preferredContext?.WorldObjectId))
        {
            MapSummary? matched = index.Maps.FirstOrDefault(map =>
                string.Equals(map.ParentWorldObjectId, preferredContext.WorldObjectId, StringComparison.Ordinal));
            if (matched is not null)
            {
                return matched;
            }
        }

        return index.Maps.FirstOrDefault(map => !string.IsNullOrWhiteSpace(map.UniqueId));
    }

    private static WorldObjectSummary? ResolveEndpointWorldObject(
        SaveSnapshotIndex index,
        MapSummary? map,
        EventTargetContext? preferredContext)
    {
        string? worldObjectId = preferredContext?.WorldObjectId ?? map?.ParentWorldObjectId;
        if (!string.IsNullOrWhiteSpace(worldObjectId))
        {
            WorldObjectSummary? matched = index.WorldObjects.FirstOrDefault(candidate =>
                string.Equals(candidate.UniqueLoadId, worldObjectId, StringComparison.Ordinal)
                || string.Equals(candidate.Id, worldObjectId, StringComparison.Ordinal));
            if (matched is not null)
            {
                return matched;
            }
        }

        if (preferredContext?.Tile is int tile)
        {
            return index.WorldObjects.FirstOrDefault(candidate =>
                TryParsePlanetTile(candidate.Tile, out WorldTileRef candidateTile)
                && candidateTile.LayerId == 0
                && candidateTile.Tile == tile);
        }

        if (!string.IsNullOrWhiteSpace(map?.ParentWorldObjectId))
        {
            return index.WorldObjects.FirstOrDefault(candidate =>
                string.Equals(candidate.UniqueLoadId, map.ParentWorldObjectId, StringComparison.Ordinal)
                || string.Equals(candidate.Id, map.ParentWorldObjectId, StringComparison.Ordinal));
        }

        return null;
    }

    private static bool IsOrbitalWorldObject(ClashOfRimNetworkState state, WorldObjectSummary worldObject)
    {
        if (TryParsePlanetTile(worldObject.Tile, out WorldTileRef tileRef) && tileRef.LayerId > 0)
        {
            return true;
        }

        return state.Plugins.ActiveWorldObjectClassifiers(state.CompatibilityBaseline.Current).Any(classifier =>
            {
                try
                {
                    return classifier.IsOrbitalWorldObject(worldObject);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        "[ClashOfRim][ServerPlugin][Warning] WorldObjectClassifierFailed: "
                        + (worldObject.UniqueLoadId ?? worldObject.Id ?? "<unknown>")
                        + " "
                        + ex.GetType().Name
                        + " "
                        + ex.Message);
                    return false;
                }
            });
    }

    private static TradeOrderSummaryDto ToTradeOrderSummary(
        AuthoritativeEvent ledgerEvent,
        int acceptedMemoCount,
        ClashOfRimNetworkState state,
        string viewerUserId,
        string viewerColonyId,
        string viewerSnapshotId,
        AuthoritativeEvent? viewerMemo = null,
        ProtocolIdentity? counterparty = null)
    {
        TradeEventPayload payload = (TradeEventPayload)ledgerEvent.Payload;
        return new TradeOrderSummaryDto(
            ledgerEvent.EventId,
            new ProtocolIdentity(
                ledgerEvent.Actor.UserId,
                ledgerEvent.Actor.ColonyId,
                snapshotId: null),
            payload.OfferedItems.Select(ToThingReferenceDto).ToList(),
            payload.RequestedItems.Select(ToThingReferenceDto).ToList(),
            payload.FeeSilver,
            payload.FulfillmentMode is TradeFulfillmentMode.SelfDelivery or TradeFulfillmentMode.Unspecified,
            payload.FulfillmentMode is TradeFulfillmentMode.ServerDropPod or TradeFulfillmentMode.Unspecified,
            acceptedMemoCount,
            ledgerEvent.CreatedAtUtc,
            ledgerEvent.Status.ToString(),
            viewerMemo is not null,
            viewerMemo?.EventId,
            BuildTradePostageQuote(state, ledgerEvent, viewerUserId, viewerColonyId, viewerSnapshotId),
            ToEventTargetContextDto(ledgerEvent.TargetContext),
            ledgerEvent.CreatedAtUtc + state.ServerConfiguration.TradeOrderExpiration,
            counterparty);
    }

    private static IReadOnlyDictionary<string, ProtocolIdentity> BuildTradeCounterpartyByTradeId(
        IReadOnlyList<AuthoritativeEvent> events,
        IReadOnlySet<string> tradeIds)
    {
        var result = new Dictionary<string, (DateTimeOffset CreatedAtUtc, ProtocolIdentity Identity)>(StringComparer.Ordinal);
        foreach (AuthoritativeEvent ledgerEvent in events)
        {
            if (ledgerEvent.Type != ServerEventType.Trade
                || ledgerEvent.Payload is not TradeEventPayload payload
                || !tradeIds.Contains(payload.TradeId)
                || payload.Stage is not (TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange)
                || ledgerEvent.Status is ServerEventStatus.Cancelled or ServerEventStatus.Failed or ServerEventStatus.Conflict)
            {
                continue;
            }

            AddLatestTradeCounterparty(result, payload.TradeId, ledgerEvent.Actor, ledgerEvent.CreatedAtUtc);
        }

        foreach (AuthoritativeEvent ledgerEvent in events)
        {
            if (ledgerEvent.Type != ServerEventType.Trade
                || ledgerEvent.Payload is not TradeEventPayload payload
                || !tradeIds.Contains(payload.TradeId)
                || result.ContainsKey(payload.TradeId)
                || payload.Stage != TradeStage.AcceptedMemo
                || ledgerEvent.Status is ServerEventStatus.Cancelled or ServerEventStatus.Failed or ServerEventStatus.RejectedByTarget or ServerEventStatus.Conflict)
            {
                continue;
            }

            AddLatestTradeCounterparty(result, payload.TradeId, ledgerEvent.Actor, ledgerEvent.CreatedAtUtc);
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Identity,
            StringComparer.Ordinal);
    }

    private static void AddLatestTradeCounterparty(
        Dictionary<string, (DateTimeOffset CreatedAtUtc, ProtocolIdentity Identity)> result,
        string tradeId,
        EventParty party,
        DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(tradeId) || string.IsNullOrWhiteSpace(party.UserId))
        {
            return;
        }

        if (!result.TryGetValue(tradeId, out (DateTimeOffset CreatedAtUtc, ProtocolIdentity Identity) current)
            || createdAtUtc > current.CreatedAtUtc)
        {
            result[tradeId] = (
                createdAtUtc,
                new ProtocolIdentity(party.UserId, party.ColonyId, snapshotId: null));
        }
    }

    private static EventTargetContextDto? ToEventTargetContextDto(EventTargetContext? context)
    {
        return context is null
            ? null
            : new EventTargetContextDto(
                context.WorldObjectId,
                context.MapUniqueId,
                context.Tile,
                context.LandingMode.ToString());
    }

    private static bool IsTradeOrderVisibleInScope(
        AuthoritativeEvent ledgerEvent,
        string viewerUserId,
        string scope,
        bool viewerHasAccepted,
        bool viewerWasInvolved)
    {
        if (ledgerEvent.Type != ServerEventType.Trade
            || ledgerEvent.Payload is not TradeEventPayload payload
            || payload.Stage != TradeStage.MarketOrder)
        {
            return false;
        }

        bool isOwner = string.Equals(ledgerEvent.Actor.UserId, viewerUserId, StringComparison.Ordinal);
        bool isOpen = IsOpenMarketTradeOrderForOwner(ledgerEvent);
        return scope.Equals("AcceptedByMe", StringComparison.OrdinalIgnoreCase)
            ? viewerHasAccepted && isOpen
            : scope.Equals("Mine", StringComparison.OrdinalIgnoreCase)
                ? isOwner && isOpen
                : scope.Equals("History", StringComparison.OrdinalIgnoreCase)
                    ? !isOpen && (viewerWasInvolved || isOwner)
                    : IsOpenMarketTradeOrder(ledgerEvent, viewerUserId);
    }

    private static bool IsOpenMarketTradeOrder(AuthoritativeEvent ledgerEvent, string viewerUserId)
    {
        if (ledgerEvent.Type != ServerEventType.Trade
            || ledgerEvent.Payload is not TradeEventPayload payload
            || payload.Stage != TradeStage.MarketOrder)
        {
            return false;
        }

        if (string.Equals(ledgerEvent.Actor.UserId, viewerUserId, StringComparison.Ordinal))
        {
            return false;
        }

        return ledgerEvent.Status is ServerEventStatus.PendingOfflineDelivery
            or ServerEventStatus.ReadyForImmediateDelivery
            or ServerEventStatus.Recorded;
    }

    private static bool IsOpenMarketTradeOrderForOwner(AuthoritativeEvent ledgerEvent)
    {
        return ledgerEvent.Type == ServerEventType.Trade
            && ledgerEvent.Payload is TradeEventPayload payload
            && payload.Stage == TradeStage.MarketOrder
            && ledgerEvent.Status is ServerEventStatus.PendingOfflineDelivery
                or ServerEventStatus.ReadyForImmediateDelivery
                or ServerEventStatus.Recorded;
    }

    private static bool IsActiveTradeAcceptanceMemo(AuthoritativeEvent ledgerEvent)
    {
        return ledgerEvent.Type == ServerEventType.Trade
            && ledgerEvent.Payload is TradeEventPayload payload
            && payload.Stage == TradeStage.AcceptedMemo
            && ledgerEvent.Status is not ServerEventStatus.Cancelled
                and not ServerEventStatus.Failed
                and not ServerEventStatus.RejectedByTarget
                and not ServerEventStatus.AppliedToSnapshot
                and not ServerEventStatus.Conflict;
    }

    private static bool IsTradeAcceptanceMemoForViewer(
        AuthoritativeEvent ledgerEvent,
        string viewerUserId,
        bool includeTerminal)
    {
        if (ledgerEvent.Type != ServerEventType.Trade
            || ledgerEvent.Payload is not TradeEventPayload payload
            || payload.Stage != TradeStage.AcceptedMemo
            || !string.Equals(payload.AcceptedByUserId, viewerUserId, StringComparison.Ordinal))
        {
            return false;
        }

        return includeTerminal || IsActiveTradeAcceptanceMemo(ledgerEvent);
    }

    private static bool IsMatchingActiveTradeAcceptanceMemo(
        AuthoritativeEvent ledgerEvent,
        string tradeEventId,
        string acceptorUserId,
        string? acceptorColonyId)
    {
        return IsActiveTradeAcceptanceMemo(ledgerEvent)
            && ledgerEvent.Payload is TradeEventPayload payload
            && string.Equals(payload.TradeId, tradeEventId, StringComparison.Ordinal)
            && string.Equals(payload.AcceptedByUserId, acceptorUserId, StringComparison.Ordinal)
            && string.Equals(ledgerEvent.Actor.UserId, acceptorUserId, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(acceptorColonyId)
                || string.Equals(ledgerEvent.Actor.ColonyId, acceptorColonyId, StringComparison.Ordinal));
    }

    private static AuthoritativeEvent? FindTradeExchangeByIdempotencyKey(
        IAuthoritativeEventLedger ledger,
        string idempotencyKey)
    {
        AuthoritativeEvent? ledgerEvent = string.IsNullOrWhiteSpace(idempotencyKey)
            ? null
            : ledger.FindByIdempotencyKey(idempotencyKey);
        return ledgerEvent?.Type == ServerEventType.Trade
            && ledgerEvent.Payload is TradeEventPayload payload
            && payload.Stage is TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange
                ? ledgerEvent
                : null;
    }

    private static AuthoritativeEvent? FindTradeAcceptanceMemo(
        IAuthoritativeEventLedger ledger,
        string tradeEventId,
        string acceptorUserId,
        string? acceptorColonyId)
    {
        return ledger.ListForUser(acceptorUserId)
            .FirstOrDefault(ledgerEvent =>
                IsActiveTradeAcceptanceMemo(ledgerEvent)
                && ledgerEvent.Payload is TradeEventPayload payload
                && string.Equals(payload.TradeId, tradeEventId, StringComparison.Ordinal)
                && string.Equals(payload.AcceptedByUserId, acceptorUserId, StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(acceptorColonyId)
                    || string.Equals(ledgerEvent.Target.ColonyId, acceptorColonyId, StringComparison.Ordinal)));
    }

    private static IReadOnlyList<AuthoritativeEvent> FindActiveTradeAcceptanceMemos(
        IAuthoritativeEventLedger ledger,
        string tradeEventId)
    {
        return ledger.ListByType(ServerEventType.Trade)
            .Where(ledgerEvent =>
                IsActiveTradeAcceptanceMemo(ledgerEvent)
                && ledgerEvent.Payload is TradeEventPayload payload
                && string.Equals(payload.TradeId, tradeEventId, StringComparison.Ordinal))
            .ToList();
    }

    private static ProtocolResponse? ValidateTradeFulfillmentRequest(FulfillTradeOrderRequest request)
    {
        TradeFulfillmentMode fulfillmentMode = ResolveRequestedTradeFulfillmentMode(request.FulfillmentMode);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.TradeEventId)
            || (fulfillmentMode == TradeFulfillmentMode.SelfDelivery && string.IsNullOrWhiteSpace(request.AcceptedMemoEventId))
            || request.Acceptor is null
            || string.IsNullOrWhiteSpace(request.Acceptor.UserId)
            || string.IsNullOrWhiteSpace(request.Acceptor.ColonyId)
            || string.IsNullOrWhiteSpace(request.Acceptor.SnapshotId)
            || request.DeliveredThings is null)
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T("Trade.FulfillmentMissingFields"));
        }

        foreach (ThingReferenceDto thing in request.DeliveredThings)
        {
            if (string.IsNullOrWhiteSpace(thing.GlobalKey)
                || string.IsNullOrWhiteSpace(thing.DefName)
                || thing.StackCount <= 0)
            {
                return ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    T("Trade.FulfillmentInvalidDeliveredThing"));
            }
        }

        return null;
    }

    private static ProtocolResponse? ValidateTradeAcceptanceRequest(AcceptTradeOrderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.TradeEventId)
            || request.Acceptor is null
            || string.IsNullOrWhiteSpace(request.Acceptor.UserId)
            || string.IsNullOrWhiteSpace(request.Acceptor.ColonyId)
            || string.IsNullOrWhiteSpace(request.Acceptor.SnapshotId))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T("Trade.AcceptanceMissingFields"));
        }

        return null;
    }

    private static ProtocolResponse? ValidateTradeCloseRequest(CloseTradeOrderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.TradeEventId)
            || request.Owner is null
            || string.IsNullOrWhiteSpace(request.Owner.UserId)
            || string.IsNullOrWhiteSpace(request.Owner.ColonyId)
            || string.IsNullOrWhiteSpace(request.Owner.SnapshotId))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T("Trade.CloseMissingFields"));
        }

        return null;
    }

    private static TradeFulfillmentMode ResolveTradeFulfillmentMode(CreateTradeOrderRequest request)
    {
        if (request.AllowSelfPickup && request.AllowServerDropPod)
        {
            return TradeFulfillmentMode.Unspecified;
        }

        if (request.AllowServerDropPod)
        {
            return TradeFulfillmentMode.ServerDropPod;
        }

        return request.AllowSelfPickup
            ? TradeFulfillmentMode.SelfDelivery
            : TradeFulfillmentMode.Unspecified;
    }

    private static TradeFulfillmentMode ResolveRequestedTradeFulfillmentMode(string? fulfillmentMode)
    {
        return Enum.TryParse(fulfillmentMode, ignoreCase: true, out TradeFulfillmentMode parsed)
            ? parsed
            : TradeFulfillmentMode.Unspecified;
    }

    private static LedgerAppendResult AppendTradeItemDeliveryEvent(
        ClashOfRimNetworkState state,
        string idempotencyKey,
        EventParty actor,
        EventParty target,
        IReadOnlyList<EventThingReference> things,
        ItemDeliveryPurpose purpose,
        EventTargetContext? targetContext,
        DateTimeOffset nowUtc)
    {
        AuthoritativeEvent delivery = AuthoritativeEventFactory.Create(
            ServerEventType.ItemDelivery,
            actor,
            target,
            idempotencyKey,
            state.OnlinePresence.IsUserOnline(target.UserId),
            new ItemDeliveryEventPayload(things, Message: null, Purpose: purpose),
            nowUtc,
            targetContext);
        LedgerAppendResult append = state.Ledger.Append(delivery);
        SignalIfCreated(state, append, target.UserId);
        return append;
    }

    private static EventTargetContext? ResolveTradeTargetContext(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        string snapshotId,
        EventLandingMode landingMode)
    {
        TradeDeliveryEndpoint endpoint = ResolveTradeDeliveryEndpoint(
            state,
            userId,
            colonyId,
            snapshotId,
            preferredContext: null);
        if (!endpoint.Available && endpoint.WorldObjectId is null && endpoint.MapUniqueId is null)
        {
            return null;
        }

        return new EventTargetContext(
            endpoint.WorldObjectId,
            endpoint.MapUniqueId,
            endpoint.Tile,
            landingMode);
    }

    private static EventTargetContext? ResolveGiftTargetContext(CreateGiftRequest request, ClashOfRimNetworkState state)
    {
        if (request.TargetContext is not null)
        {
            EventTargetContext requestedContext = ToEventTargetContext(request.TargetContext);
            if (!string.IsNullOrWhiteSpace(requestedContext.MapUniqueId))
            {
                return requestedContext;
            }

            EventLandingMode requestedLandingMode = requestedContext.LandingMode == EventLandingMode.Unspecified
                ? EventLandingMode.DropPod
                : requestedContext.LandingMode;
            return ResolveLatestGiftTargetContext(request, state, requestedLandingMode);
        }

        return ResolveLatestGiftTargetContext(request, state, EventLandingMode.DropPod);
    }

    private static EventTargetContext? ResolveLatestGiftTargetContext(
        CreateGiftRequest request,
        ClashOfRimNetworkState state,
        EventLandingMode landingMode)
    {
        if (string.IsNullOrWhiteSpace(request.Target.UserId)
            || string.IsNullOrWhiteSpace(request.Target.ColonyId))
        {
            return null;
        }

        LatestSnapshotRecord? snapshot = state.SnapshotStore.GetLatest(
            request.Target.UserId,
            request.Target.ColonyId!);
        if (snapshot is null)
        {
            return null;
        }

        MapSummary? map = snapshot.Index.Maps
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.UniqueId));
        if (map is null)
        {
            return null;
        }

        return new EventTargetContext(
            map.ParentWorldObjectId,
            map.UniqueId,
            Tile: null,
            landingMode);
    }

    private static EventTargetContext? ResolveSupportPawnTargetContext(LatestSnapshotRecord targetSnapshot)
    {
        MapSummary? map = targetSnapshot.Index.Maps
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.UniqueId));
        if (map is null)
        {
            return null;
        }

        return new EventTargetContext(
            map.ParentWorldObjectId,
            map.UniqueId,
            Tile: null,
            EventLandingMode.MapEdge);
    }

    private static EventTargetContext ToEventTargetContext(EventTargetContextDto dto)
    {
        EventLandingMode landingMode = Enum.TryParse(dto.LandingMode, ignoreCase: true, out EventLandingMode parsed)
            ? parsed
            : EventLandingMode.Unspecified;

        return new EventTargetContext(
            dto.WorldObjectId,
            dto.MapUniqueId,
            dto.Tile,
            landingMode);
    }

    private static bool IsVisibleTo(AuthoritativeEvent ledgerEvent, string userId, string colonyId)
    {
        return IsVisibleParty(ledgerEvent.Actor, userId, colonyId)
            || IsVisibleParty(ledgerEvent.Target, userId, colonyId);
    }

    private static bool IsAuthorizedForColony(
        ClashOfRimNetworkState state,
        string? authToken,
        string userId,
        string colonyId,
        string? authorizationEventId,
        string? authorizationScope,
        DateTimeOffset nowUtc,
        out string failureMessage)
    {
        failureMessage = ServerLocalization.Text("Auth.Failed");
        if (!state.AuthTokens.TryGetPrincipal(authToken, nowUtc, out AuthTokenPrincipal? principal) || principal is null)
        {
            return false;
        }

        if (!state.LoginSessions.Refresh(principal.UserId, principal.ColonyId, principal.SessionId, nowUtc))
        {
            failureMessage = ServerLocalization.Text("Auth.SessionExpired");
            return false;
        }

        if (string.Equals(principal.UserId, userId, StringComparison.Ordinal)
            && string.Equals(principal.ColonyId, colonyId, StringComparison.Ordinal))
        {
            return true;
        }

        string scope = authorizationScope ?? string.Empty;
        if (string.Equals(scope, "Scout", StringComparison.OrdinalIgnoreCase)
            && IsAuthorizedScoutTarget(state, principal, userId, colonyId))
        {
            return true;
        }

        if (string.Equals(scope, "FriendlyObservation", StringComparison.OrdinalIgnoreCase)
            && IsAuthorizedFriendlyObservationTarget(state, principal, userId, colonyId))
        {
            return true;
        }

        if (string.Equals(scope, "RaidTarget", StringComparison.OrdinalIgnoreCase)
            && IsAuthorizedRaidTargetDownload(state, principal, userId, colonyId))
        {
            return true;
        }

        if (string.Equals(scope, "RaidPreparation", StringComparison.OrdinalIgnoreCase)
            && state.RaidPreparations.TryAuthorizeDownload(
                authorizationEventId,
                principal.UserId,
                principal.ColonyId,
                userId,
                colonyId,
                nowUtc))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(authorizationEventId)
            && IsAuthorizedByRaidEvent(
                state,
                principal,
                userId,
                colonyId,
                authorizationEventId!,
                scope))
        {
            return true;
        }

        failureMessage = ServerLocalization.Text("Auth.IdentityDenied");
        return false;
    }

    private static bool IsAuthorizedByRaidEvent(
        ClashOfRimNetworkState state,
        AuthTokenPrincipal principal,
        string targetUserId,
        string targetColonyId,
        string authorizationEventId,
        string authorizationScope)
    {
        AuthoritativeEvent? ledgerEvent = state.Ledger.Find(authorizationEventId);
        if (ledgerEvent is null
            || ledgerEvent.Type != ServerEventType.Raid
            || ledgerEvent.Payload is not RaidEventPayload { OpponentKind: RaidOpponentKind.Player }
            || (!IsVisibleParty(ledgerEvent.Target, targetUserId, targetColonyId)
                && !IsVisibleParty(ledgerEvent.Actor, targetUserId, targetColonyId)))
        {
            return false;
        }

        if (IsVisibleParty(ledgerEvent.Actor, principal.UserId, principal.ColonyId))
        {
            return true;
        }

        if (!string.Equals(authorizationScope, "RaidObservation", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return AreAllied(state, principal.UserId, principal.ColonyId, ledgerEvent.Actor.UserId, ledgerEvent.Actor.ColonyId)
            || AreAllied(state, principal.UserId, principal.ColonyId, ledgerEvent.Target.UserId, ledgerEvent.Target.ColonyId);
    }

    private static bool IsAuthorizedScoutTarget(
        ClashOfRimNetworkState state,
        AuthTokenPrincipal principal,
        string targetUserId,
        string targetColonyId)
    {
        if (string.Equals(principal.UserId, targetUserId, StringComparison.Ordinal)
            && string.Equals(principal.ColonyId, targetColonyId, StringComparison.Ordinal))
        {
            return false;
        }

        return !AreAllied(state, principal.UserId, principal.ColonyId, targetUserId, targetColonyId);
    }

    private static bool IsAuthorizedFriendlyObservationTarget(
        ClashOfRimNetworkState state,
        AuthTokenPrincipal principal,
        string targetUserId,
        string targetColonyId)
    {
        if (string.Equals(principal.UserId, targetUserId, StringComparison.Ordinal)
            && string.Equals(principal.ColonyId, targetColonyId, StringComparison.Ordinal))
        {
            return false;
        }

        return AreAllied(state, principal.UserId, principal.ColonyId, targetUserId, targetColonyId);
    }

    private static bool IsAuthorizedRaidTargetDownload(
        ClashOfRimNetworkState state,
        AuthTokenPrincipal principal,
        string targetUserId,
        string targetColonyId)
    {
        if (!state.ServerConfiguration.PvpEnabled)
        {
            return false;
        }

        if (string.Equals(principal.UserId, targetUserId, StringComparison.Ordinal)
            && string.Equals(principal.ColonyId, targetColonyId, StringComparison.Ordinal))
        {
            return false;
        }

        if (AreAllied(state, principal.UserId, principal.ColonyId, targetUserId, targetColonyId))
        {
            return false;
        }

        return FindUnsettledAttackerRaid(state, principal.UserId, principal.ColonyId, excludedIdempotencyKey: null) is null;
    }

    private static bool AreAllied(
        ClashOfRimNetworkState state,
        string userA,
        string? colonyA,
        string userB,
        string? colonyB)
    {
        return string.Equals(
            ResolveDiplomacyRelationKind(state, userA, colonyA, userB, colonyB),
            "Ally",
            StringComparison.OrdinalIgnoreCase);
    }

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
    {
        if (!string.Equals(party.UserId, userId, StringComparison.Ordinal))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(party.ColonyId)
            || string.Equals(party.ColonyId, colonyId, StringComparison.Ordinal);
    }

    private static AuthoritativeEvent? FindGiftReturnFor(IAuthoritativeEventLedger ledger, AuthoritativeEvent giftEvent)
    {
        string returnIdempotencyKey = $"{giftEvent.IdempotencyKey}:return";
        AuthoritativeEvent? ledgerEvent = ledger.FindByIdempotencyKey(returnIdempotencyKey);
        return ledgerEvent?.Type == ServerEventType.ItemDelivery
            && ledgerEvent.Payload is ItemDeliveryEventPayload { Purpose: ItemDeliveryPurpose.RejectedGiftReturn }
                ? ledgerEvent
                : null;
    }

    private static AuthoritativeEvent? FindSupportPawnReturnFor(IAuthoritativeEventLedger ledger, AuthoritativeEvent supportEvent)
    {
        string returnIdempotencyKey = $"{supportEvent.IdempotencyKey}:rejected-return";
        AuthoritativeEvent? ledgerEvent = ledger.FindByIdempotencyKey(returnIdempotencyKey);
        return ledgerEvent?.Type == ServerEventType.SupportPawn ? ledgerEvent : null;
    }

    private static AuthoritativeEvent? FindSupportFinishEventFor(
        IAuthoritativeEventLedger ledger,
        AuthoritativeEvent supportEvent,
        string finishReason)
    {
        string idempotencyKey = $"support-finish:{supportEvent.EventId}:{finishReason}";
        return ledger.FindByIdempotencyKey(idempotencyKey)
            ?? ledger.FindByIdempotencyKey($"support-loss:{supportEvent.EventId}:{finishReason}");
    }

    private static AuthoritativeEvent CreateSupportPawnFinishReturnEvent(
        AuthoritativeEvent supportEvent,
        SupportPawnEventPayload payload,
        FinishSupportPawnRequest request,
        PawnExchangePackage? pawnPackage,
        bool originalActorOnline,
        DateTimeOffset createdAtUtc)
    {
        var returnPayload = new SupportPawnEventPayload(
            request.PawnGlobalKey,
            payload.SourceSnapshotId,
            request.PawnName ?? payload.PawnName,
            TemporaryControl: false,
            ExpectedReturnAtUtc: null,
            payload.PawnReference,
            pawnPackage ?? payload.PawnPackage,
            payload.SourceTile,
            payload.SourceCaravanLoadId,
            ReturnToSender: true,
            RejectionReason: null,
            payload.PermanentSupport,
            payload.SupportDurationDays,
            payload.ExpiresAtGameTicks,
            payload.AutoReturnOnSettlement,
            supportEvent.EventId,
            request.FinishReason);
        EventTargetContext? returnContext = payload.SourceTile is null
            ? null
            : new EventTargetContext(null, null, payload.SourceTile, EventLandingMode.MapEdge);

        return AuthoritativeEventFactory.Create(
            ServerEventType.SupportPawn,
            supportEvent.Target,
            supportEvent.Actor,
            $"support-finish:{supportEvent.EventId}:{request.FinishReason}",
            originalActorOnline,
            returnPayload,
            createdAtUtc,
            returnContext);
    }

    private static AuthoritativeEvent CreateSupportPawnLossReturnEvent(
        AuthoritativeEvent supportEvent,
        SupportPawnEventPayload payload,
        string? pawnName,
        string finishReason,
        bool originalActorOnline,
        DateTimeOffset createdAtUtc)
    {
        var returnPayload = new SupportPawnEventPayload(
            payload.PawnGlobalKey,
            payload.SourceSnapshotId,
            pawnName ?? payload.PawnName,
            TemporaryControl: false,
            ExpectedReturnAtUtc: null,
            payload.PawnReference,
            PawnPackage: null,
            payload.SourceTile,
            payload.SourceCaravanLoadId,
            ReturnToSender: true,
            RejectionReason: null,
            payload.PermanentSupport,
            payload.SupportDurationDays,
            payload.ExpiresAtGameTicks,
            payload.AutoReturnOnSettlement,
            supportEvent.EventId,
            finishReason);
        EventTargetContext? returnContext = payload.SourceTile is null
            ? null
            : new EventTargetContext(null, null, payload.SourceTile, EventLandingMode.MapEdge);

        return AuthoritativeEventFactory.Create(
            ServerEventType.SupportPawn,
            supportEvent.Target,
            supportEvent.Actor,
            $"support-loss:{supportEvent.EventId}:{finishReason}",
            originalActorOnline,
            returnPayload,
            createdAtUtc,
            returnContext);
    }

    private static AuthoritativeEvent CreateRejectedSupportPawnReturnEvent(
        AuthoritativeEvent rejectedSupportEvent,
        SupportPawnEventPayload payload,
        string? rejectionReason,
        bool originalActorOnline,
        DateTimeOffset createdAtUtc)
    {
        string returnIdempotencyKey = $"{rejectedSupportEvent.IdempotencyKey}:rejected-return";
        var returnPayload = new SupportPawnEventPayload(
            payload.PawnGlobalKey,
            payload.SourceSnapshotId,
            payload.PawnName,
            TemporaryControl: false,
            ExpectedReturnAtUtc: null,
            payload.PawnReference,
            payload.PawnPackage,
            payload.SourceTile,
            payload.SourceCaravanLoadId,
            ReturnToSender: true,
            RejectionReason: rejectionReason,
            payload.PermanentSupport,
            payload.SupportDurationDays,
            payload.ExpiresAtGameTicks,
            payload.AutoReturnOnSettlement,
            rejectedSupportEvent.EventId,
            "Rejected");
        EventTargetContext? returnContext = payload.SourceTile is null
            ? null
            : new EventTargetContext(
                WorldObjectId: null,
                MapUniqueId: null,
                Tile: payload.SourceTile,
                LandingMode: EventLandingMode.MapEdge);

        return AuthoritativeEventFactory.Create(
            ServerEventType.SupportPawn,
            rejectedSupportEvent.Target,
            rejectedSupportEvent.Actor,
            returnIdempotencyKey,
            originalActorOnline,
            returnPayload,
            createdAtUtc,
            returnContext);
    }

    private static string? ExtractPawnLocalId(string globalKey)
    {
        const string marker = "/pawn:";
        int index = globalKey.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        string value = globalKey.Substring(index + marker.Length);
        int slash = value.IndexOf('/');
        return slash >= 0 ? value.Substring(0, slash) : value;
    }

    private sealed record CompatibilityHandshakeResult(
        ProtocolResponse Result,
        string? SteamId,
        string? DisplayName,
        string? ServerCompatibilityManifestJson,
        IReadOnlyList<CompatibilityIssueDto> CompatibilityIssues,
        bool CanOverrideCompatibilityBaseline,
        bool RequiresFullCompatibilityManifest,
        IReadOnlyList<string> RequestedCompatibilityPackageIds);

    private sealed record AuthenticationValidationResult(
        bool Accepted,
        string? AuthenticatedUserId,
        string? DisplayName,
        string? Message,
        ProtocolErrorCode ErrorCode)
    {
        public static AuthenticationValidationResult Accept(string authenticatedUserId, string? displayName = null)
        {
            return new AuthenticationValidationResult(true, authenticatedUserId, displayName, null, ProtocolErrorCode.None);
        }

        public static AuthenticationValidationResult Reject(
            string message,
            ProtocolErrorCode errorCode = ProtocolErrorCode.Unauthorized)
        {
            return new AuthenticationValidationResult(false, null, null, message, errorCode);
        }
    }

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

    private sealed record TradeDeliveryEndpoint(
        bool Available,
        bool IsOrbital,
        WorldTileRef? TileRef,
        string? WorldObjectId,
        string? MapUniqueId,
        string Status)
    {
        public int? Tile => TileRef is WorldTileRef tileRef && tileRef.LayerId == 0 ? tileRef.Tile : null;

        public static TradeDeliveryEndpoint Unavailable(string status)
        {
            return new TradeDeliveryEndpoint(
                Available: false,
                IsOrbital: false,
                TileRef: null,
                WorldObjectId: null,
                MapUniqueId: null,
                Status: status);
        }

        public static TradeDeliveryEndpoint Surface(
            WorldTileRef tile,
            string? worldObjectId,
            string? mapUniqueId,
            string status)
        {
            return new TradeDeliveryEndpoint(
                Available: true,
                IsOrbital: false,
                TileRef: tile,
                WorldObjectId: worldObjectId,
                MapUniqueId: mapUniqueId,
                Status: status);
        }

        public static TradeDeliveryEndpoint Orbital(
            WorldTileRef? tile,
            string? worldObjectId,
            string? mapUniqueId,
            string status)
        {
            return new TradeDeliveryEndpoint(
                Available: true,
                IsOrbital: true,
                TileRef: tile,
                WorldObjectId: worldObjectId,
                MapUniqueId: mapUniqueId,
                Status: status);
        }
    }
}
