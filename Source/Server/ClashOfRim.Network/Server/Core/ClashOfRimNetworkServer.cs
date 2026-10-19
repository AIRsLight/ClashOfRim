using AIRsLight.ClashOfRim.Network.Modules.Transfers;
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
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Modules.Bank;
using AIRsLight.ClashOfRim.Network.Modules.Events;
using AIRsLight.ClashOfRim.Network.Hosting;
using AIRsLight.ClashOfRim.Network.Modules.Compatibility;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace AIRsLight.ClashOfRim.Network;

public static class ClashOfRimNetworkServer
{
    public static WebApplication Build(string[] args, ClashOfRimNetworkState? state = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = ServerStartupConfiguration.MaxRequestBodySize(builder.Configuration);
        });
        string logFilePath = ServerStartupConfiguration.LogFilePath(builder.Environment.ContentRootPath);
        builder.Logging.AddFilter<ConsoleLoggerProvider>("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Logging.AddFilter<ConsoleLoggerProvider>("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
        builder.Logging.AddFilter<ConsoleLoggerProvider>("Microsoft.AspNetCore.Http.Result", LogLevel.Warning);
        ClashOfRimFileLoggerProvider? fileLogger = null;
        WebApplication? app = null;
        ClashOfRimNetworkState? networkState = state;
        ILogger? previousRuntimeLogger = null;
        try
        {
            // A factory registration gives the host ownership; defer opening until its services need logging.
            builder.Services.AddSingleton<ILoggerProvider>(_ => fileLogger = new ClashOfRimFileLoggerProvider(logFilePath));
            ServerLocalizationFileLoader.Load(builder.Environment.ContentRootPath);
            string serverLanguage = ServerStartupConfiguration.ResolveLanguage(args, builder.Configuration);
            if (!ServerLocalization.HasLanguage(serverLanguage))
            {
                throw new InvalidOperationException(ServerText.Format("Server.LanguageUnavailable", ("LANGUAGE", serverLanguage)));
            }

            ServerLocalization.SetDefaultLanguage(serverLanguage);
            ServerPluginRegistry plugins = ServerPluginLoader
                .Load(builder.Environment.ContentRootPath)
                .WithBuiltInPlugins(BuiltInServerPlugins.Descriptors);
            ServerPersistenceMigrationResult? persistenceMigrationResult = null;
            ServerPersistenceMigrationService? persistenceMigrations = null;
            if (state is null)
            {
                string dataDirectory = ServerStartupConfiguration.DataDirectory(builder.Configuration, builder.Environment.ContentRootPath);
                persistenceMigrations = new ServerPersistenceMigrationService(dataDirectory);
                (networkState, persistenceMigrationResult) = PersistentServerState.Create(
                    dataDirectory,
                    ServerStartupConfiguration.ServerConfiguration(builder.Configuration),
                    plugins,
                    persistenceMigrations);
            }
            else
            {
                networkState = state;
            }
            networkState.SnapshotPostUploads.ValidateRegistrations();
            builder.Services.AddSingleton(networkState);
            builder.Services.AddSingleton(networkState.SnapshotPostUploadBackground);
            builder.Services.AddHostedService<SnapshotPostUploadBackgroundService>();
            app = builder.Build();
            previousRuntimeLogger = networkState.RuntimeLogger;
            networkState.SetRuntimeLogger(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AIRsLight.ClashOfRim.Network.Runtime"));
            networkState.SnapshotMutations.Recover();
            app.Logger.LogInformation(ServerText.Format("Server.LocalizationLoaded", ("LANGUAGES", string.Join(", ", ServerLocalization.LoadedLanguages))));
            if (plugins.Plugins.Count > 0)
            {
                app.Logger.LogInformation(
                    ServerText.Format("Server.PluginsLoaded", ("PLUGINS", "{Plugins}")),
                    string.Join(", ", plugins.Plugins.Select(plugin => $"{plugin.Id}:{string.Join("+", plugin.Capabilities)}")));
            }

            app.Logger.LogInformation(ServerText.Format("Server.LogFilePath", ("PATH", logFilePath)));
            int recoveredWorldExtensionSnapshots = networkState.ColonyProjection.RecoverExtensions(networkState.SnapshotStore.ListLatest());
            if (recoveredWorldExtensionSnapshots > 0)
            {
                app.Logger.LogInformation(
                    ServerText.Format("Server.WorldExtensionsRecovered", ("COUNT", "{SnapshotCount}")),
                    recoveredWorldExtensionSnapshots);
            }

            if (persistenceMigrationResult is not null)
            {
                ServerDatabaseMigrationResult databaseMigration = persistenceMigrationResult.Database;
                app.Logger.LogInformation(
                    ServerText.Format(
                        "Server.DatabaseSchemaReady",
                        ("FROM", databaseMigration.StartingVersion.ToString()),
                        ("TO", databaseMigration.FinalVersion.ToString()),
                        ("MIGRATIONS", databaseMigration.AppliedMigrations.Count == 0
                            ? "none"
                            : string.Join(", ", databaseMigration.AppliedMigrations))));
                if (databaseMigration.RequiresWorldSubstrateRebaseline)
                {
                    app.Logger.LogWarning(ServerText.Format("Server.DatabaseWorldRebaselineRequired"));
                }
                else if (!string.IsNullOrWhiteSpace(databaseMigration.RecoveredWorldSubstrateSnapshotId))
                {
                    app.Logger.LogInformation(
                        ServerText.Format("Server.DatabaseWorldSubstrateRecovered", ("SNAPSHOT", databaseMigration.RecoveredWorldSubstrateSnapshotId)));
                }
                app.Logger.LogInformation(
                    ServerText.Format(
                        "Server.SnapshotPackagesReady",
                        ("TOTAL", persistenceMigrationResult.Snapshots.TotalPackages.ToString()),
                        ("CURRENT", persistenceMigrationResult.Snapshots.CurrentPackages.ToString()),
                        ("MIGRATIONS", persistenceMigrationResult.Snapshots.AppliedMigrations.Count == 0
                            ? "none"
                            : string.Join(", ", persistenceMigrationResult.Snapshots.AppliedMigrations))));
            }
            app.Logger.LogInformation(
                ServerText.Format("Server.MaxRequestBodySize", ("BYTES", ServerStartupConfiguration.MaxRequestBodySize(builder.Configuration).ToString())));
            app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(20)
            });
            ServerRequestPipeline.Use(app, networkState.SessionPrincipals, networkState.SnapshotMutations,
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AIRsLight.ClashOfRim.Network.RequestDiagnostics"));
            MapEndpoints(app);
            plugins.MapEndpoints(app);
            return app;
        }
        catch
        {
            // Build may fail after creating the service provider or during startup recovery.
            // The caller cannot dispose a host that was never returned.
            if (previousRuntimeLogger is not null) networkState?.SetRuntimeLogger(previousRuntimeLogger);
            try { app?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            finally { fileLogger?.Dispose(); }
            throw;
        }
    }

    public static void MapEndpoints(WebApplication app)
    {
        var state = app.Services.GetRequiredService<ClashOfRimNetworkState>();
        EventReadEndpoints.Map(app, state.EventDelivery);
        ServerInformationEndpoints.Map(app, state.ServerInformation);
        BankEndpoints.Map(app, state.Bank);
        TradeEndpoints.Map(app, state.Trades, state.TradeMarket);
        SupportEndpoints.Map(app, state.SupportSending, state.Support);
        GiftEndpoints.Map(app, state.Gifts);
        SessionEndpoints.Map(app, state.Login, state.SessionConnections, state.SessionPresence, state.SessionNotifications, () => state.RuntimeLogger);
        AccountEndpoints.Map(app, state.Accounts);
        SessionEndpoints.MapDirectory(app, state.PlayerDirectory);
        AchievementEndpoints.Map(app, state.AchievementQueries);
        SnapshotEndpoints.Map(app, state.SnapshotUploads, state.SnapshotDownloads);
        EventConfirmationEndpoints.Map(app, state.EventConfirmations);
        EventFailureEndpoints.Map(app, state.EventFailureReports);
        PackageEndpoints.Map(app, state.TransferPackages, state.PawnPreviews);
        ShopEndpoints.Map(app, state.Shop);
        RaidEndpoints.Map(app, state.RaidPreparation, state.RaidStarting);
        DiplomacyEndpoints.Map(app, state.Diplomacy);
        WorldEndpoints.Map(app, state.WorldPreparation, state.WorldRead, state.WorldSubmission, state.WorldFeatureNames, state.WorldSubstrate, state.WorldSync, state.ColonyRegistration, state.ColonyRelocation, state.ColonyAbandonment);
        AdminEndpoints.Map(app, state.AdministratorBaseline, state.AdministratorConfiguration, state.CompatibilityOverride, state.AdministratorActions);
        MercenaryEndpoints.Map(app, state.Mercenaries, state.MercenaryHiring);
        ChatEndpoints.Map(app, state.Chat);
    }

    // Public startup helpers are also used by the standalone server's migration commands.
    public static string ResolveServerLanguage(string[] args, IConfiguration configuration) =>
        ServerStartupConfiguration.ResolveLanguage(args, configuration);

    public static string ResolveDataDirectory(IConfiguration configuration, string contentRootPath) =>
        ServerStartupConfiguration.DataDirectory(configuration, contentRootPath);
}
