using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Network.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
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
using System.Xml;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{

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
        SessionEndpoints.Map(app, tradeState.Login, tradeState.SessionConnections, tradeState.SessionPresence, tradeState.SessionNotifications, () => tradeState.RuntimeLogger);
        AccountEndpoints.Map(app, app.Services.GetRequiredService<ClashOfRimNetworkState>().Accounts);
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
        RaidEndpoints.Map(app, tradeState.RaidPreparation, tradeState.RaidStarting);
        DiplomacyEndpoints.Map(app, tradeState.Diplomacy);
        WorldEndpoints.Map(app, tradeState.WorldPreparation, tradeState.WorldRead, tradeState.WorldSubmission, tradeState.WorldFeatureNames, tradeState.WorldSubstrate, tradeState.WorldSync);
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



}
