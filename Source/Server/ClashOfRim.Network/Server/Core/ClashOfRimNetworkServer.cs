using AIRsLight.ClashOfRim.Network.Modules.Achievements;
using AIRsLight.ClashOfRim.Network.Modules.Chat;
using AIRsLight.ClashOfRim.Network.Modules.Admin;
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
using System.Text.Json;

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
        SessionEndpoints.MapDirectory(app, tradeState.PlayerDirectory);
        AchievementEndpoints.Map(app, tradeState.AchievementQueries);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.UploadSnapshot).Route, UploadSnapshot).WithMetadata(EndpointSnapshotRecovery.Instance);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.DownloadLatestSnapshot).Route, DownloadLatestSnapshot).WithMetadata(EndpointSnapshotRecovery.Instance);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.DownloadLatestSnapshotPayload).Route, DownloadLatestSnapshotPayload).WithMetadata(EndpointSnapshotRecovery.Instance);
        EventConfirmationEndpoints.Map(app, tradeState.EventConfirmations);
        EventFailureEndpoints.Map(app, tradeState.EventFailureReports);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.StorePawnPackage).Route, StorePawnPackage);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetPawnPackage).Route, GetPawnPackage);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.StorePawnPackagePreview).Route, StorePawnPackagePreview);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetPawnPackagePreview).Route, GetPawnPackagePreview);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.StoreThingPackage).Route, StoreThingPackage);
        app.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetThingPackage).Route, GetThingPackage);
        ShopEndpoints.Map(app, app.Services.GetRequiredService<ClashOfRimNetworkState>().Shop);
        RaidEndpoints.Map(app, tradeState.RaidPreparation, tradeState.RaidStarting);
        DiplomacyEndpoints.Map(app, tradeState.Diplomacy);
        WorldEndpoints.Map(app, tradeState.WorldPreparation, tradeState.WorldRead, tradeState.WorldSubmission, tradeState.WorldFeatureNames, tradeState.WorldSubstrate, tradeState.WorldSync, tradeState.ColonyRegistration, tradeState.ColonyRelocation, tradeState.ColonyAbandonment);
        AdminEndpoints.Map(app, tradeState.AdministratorBaseline, tradeState.AdministratorConfiguration, tradeState.CompatibilityOverride, tradeState.AdministratorActions);
        var mercenaryState = app.Services.GetRequiredService<ClashOfRimNetworkState>();
        MercenaryEndpoints.Map(app, mercenaryState.Mercenaries, mercenaryState.MercenaryHiring);
        ChatEndpoints.Map(app, tradeState.Chat);
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

    private static Task<MultipartSnapshotRequest<TRequest>?> ReadMultipartSnapshotRequest<TRequest>(HttpRequest httpRequest)
        => MultipartSnapshotReader.Read<TRequest>(httpRequest);

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

    private static ProtocolResponse? AuthorizeColony(ClashOfRimNetworkState state, string? authToken,
        string userId, string colonyId, string? authorizationEventId, string? authorizationScope,
        DateTimeOffset nowUtc)
        => state.ColonyAuthorization.Authorize(authToken, userId, colonyId,
            authorizationEventId, authorizationScope, nowUtc);

    private static bool AreAllied(ClashOfRimNetworkState state, string userA, string? colonyA, string userB, string? colonyB)
        => state.DiplomacyRelations.AreAllied(userA, colonyA, userB, colonyB);

    private static bool IsVisibleParty(EventParty party, string userId, string colonyId)
        => EventVisibility.MatchesParty(party, userId, colonyId);



}
