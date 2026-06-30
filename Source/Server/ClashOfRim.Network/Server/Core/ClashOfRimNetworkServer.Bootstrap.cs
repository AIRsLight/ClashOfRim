using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private const long DefaultMaxRequestBodySizeBytes = 256L * 1024L * 1024L;

    public static WebApplication Build(string[] args, ClashOfRimNetworkState? state = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = ResolveMaxRequestBodySizeBytes(builder.Configuration);
        });
        string logFilePath = ResolveLogFilePath(builder.Environment.ContentRootPath);
        builder.Logging.AddFilter<ConsoleLoggerProvider>("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Logging.AddFilter<ConsoleLoggerProvider>("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
        builder.Logging.AddFilter<ConsoleLoggerProvider>("Microsoft.AspNetCore.Http.Result", LogLevel.Warning);
        builder.Logging.AddProvider(new ClashOfRimFileLoggerProvider(logFilePath));
        ServerLocalizationFileLoader.Load(builder.Environment.ContentRootPath);
        string serverLanguage = ResolveServerLanguage(args, builder.Configuration);
        if (!ServerLocalization.HasLanguage(serverLanguage))
        {
            throw new InvalidOperationException(T("Server.LanguageUnavailable", ("LANGUAGE", serverLanguage)));
        }

        ServerLocalization.SetDefaultLanguage(serverLanguage);
        ServerPluginRegistry plugins = ServerPluginLoader
            .Load(builder.Environment.ContentRootPath)
            .WithBuiltInPlugins(BuiltInServerPlugins.Descriptors);
        ClashOfRimNetworkState networkState = state ?? CreatePersistentNetworkState(
            builder.Configuration,
            builder.Environment.ContentRootPath,
            plugins);
        builder.Services.AddSingleton(networkState);
        WebApplication app = builder.Build();
        networkState.SetRuntimeLogger(RuntimeLogger(app.Services.GetRequiredService<ILoggerFactory>()));
        app.Logger.LogInformation(T("Server.LocalizationLoaded", ("LANGUAGES", string.Join(", ", ServerLocalization.LoadedLanguages))));
        if (plugins.Plugins.Count > 0)
        {
            app.Logger.LogInformation(
                T("Server.PluginsLoaded", ("PLUGINS", "{Plugins}")),
                string.Join(", ", plugins.Plugins.Select(plugin => $"{plugin.Id}:{string.Join("+", plugin.Capabilities)}")));
        }

        app.Logger.LogInformation(T("Server.LogFilePath", ("PATH", logFilePath)));
        app.Logger.LogInformation(
            T("Server.MaxRequestBodySize", ("BYTES", ResolveMaxRequestBodySizeBytes(builder.Configuration).ToString())));
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20)
        });
        UseRequestDiagnostics(app);
        MapEndpoints(app);
        plugins.MapEndpoints(app);
        return app;
    }

    private static string ResolveServerLanguage(string[] args, IConfiguration configuration)
    {
        string? language = Environment.GetEnvironmentVariable("CLASH_OF_RIM_SERVER_LANGUAGE");
        if (string.IsNullOrWhiteSpace(language))
        {
            language = configuration["Localization:Language"];
        }

        foreach (string arg in args ?? Array.Empty<string>())
        {
            if (arg.StartsWith("--language=", StringComparison.OrdinalIgnoreCase))
            {
                language = arg["--language=".Length..];
            }
            else if (arg.StartsWith("--locale=", StringComparison.OrdinalIgnoreCase))
            {
                language = arg["--locale=".Length..];
            }
        }

        return string.IsNullOrWhiteSpace(language)
            ? ServerLocalization.DetectOperatingSystemLanguage()
            : ServerLocalization.NormalizeLanguage(language);
    }

    private static ClashOfRimNetworkState CreatePersistentNetworkState(
        IConfiguration configuration,
        string contentRootPath,
        ServerPluginRegistry plugins)
    {
        string dataDirectory = ResolveDataDirectory(configuration, contentRootPath);
        string databasePath = Path.Combine(dataDirectory, "server.sqlite");
        ServerConfigurationRegistry serverConfigurationOverrides =
            new(new SqliteJsonPersistenceSlot(databasePath, "server-configuration"));
        ClashOfRimServerConfiguration serverConfiguration = LoadServerConfiguration(configuration);
        if (serverConfigurationOverrides.Current is { } adminConfiguration)
        {
            serverConfiguration = FromAdminConfigurationDto(adminConfiguration, serverConfiguration);
        }

        return new ClashOfRimNetworkState(
            ledger: new SqliteAuthoritativeEventLedger(databasePath),
            snapshotStore: new FileColonySnapshotIndexStore(Path.Combine(dataDirectory, "snapshots")),
            serverConfiguration: serverConfiguration,
            serverConfigurationOverrides: serverConfigurationOverrides,
            worldConfigurationRegistry: new WorldConfigurationRegistry(
                new SqliteJsonPersistenceSlot(databasePath, "world-configuration"),
                new SqliteJsonPersistenceSlot(databasePath, "world-configuration"),
                WorldConfigurationExtensionService.Empty),
            compatibilityBaselineRegistry: new CompatibilityBaselineRegistry(new SqliteJsonPersistenceSlot(databasePath, "compatibility-baseline")),
            adminBaselineRegistry: new AdminBaselineRegistry(new SqliteJsonPersistenceSlot(databasePath, "admin-baseline")),
            playerRegistry: new PlayerRegistry(new SqliteJsonPersistenceSlot(databasePath, "players")),
            diplomacyRelations: new DiplomacyRelationRegistry(new SqliteJsonPersistenceSlot(databasePath, "diplomacy-relations")),
            pawnPackages: new PawnPackageRegistry(new SqliteJsonPersistenceSlot(databasePath, "pawn-packages")),
            thingPackages: new ThingPackageRegistry(new SqliteJsonPersistenceSlot(databasePath, "thing-packages")),
            raidProtectionActivations: new RaidProtectionActivationRegistry(new SqliteJsonPersistenceSlot(databasePath, "raid-protection-activations")),
            bankLoans: new BankLoanRegistry(new SqliteJsonPersistenceSlot(databasePath, "bank-loans")),
            mercenaryContracts: new MercenaryContractRegistry(new SqliteJsonPersistenceSlot(databasePath, "mercenary-contracts")),
            mercenaryGuards: new MercenaryGuardContractRegistry(new SqliteJsonPersistenceSlot(databasePath, "mercenary-guards")),
            chatMessages: new ChatMessageRegistry(new SqliteJsonPersistenceSlot(databasePath, "chat-messages")),
            serverShop: new ServerShopRegistry(new SqliteJsonPersistenceSlot(databasePath, "server-shop")),
            achievements: new AchievementRegistry(new SqliteJsonPersistenceSlot(databasePath, "achievements")),
            adminControl: new AdminControlRegistry(new SqliteJsonPersistenceSlot(databasePath, "admin-control")),
            offlineAccounts: new OfflineAccountRegistry(new SqliteJsonPersistenceSlot(databasePath, "offline-accounts")),
            steamAuthTickets: BuildSteamAuthTicketValidator(serverConfiguration),
            plugins: plugins);
    }

    private static string ResolveLogFilePath(string contentRootPath)
    {
        string? configuredDirectory = Environment.GetEnvironmentVariable("CLASH_OF_RIM_LOG_DIR");
        string logDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(contentRootPath, "Logs")
            : configuredDirectory;

        return Path.Combine(logDirectory, $"server-{DateTime.UtcNow:yyyyMMdd}.log");
    }

    private static string ResolveDataDirectory(IConfiguration configuration, string contentRootPath)
    {
        string? configuredDirectory = Environment.GetEnvironmentVariable("CLASH_OF_RIM_DATA_DIR");
        if (string.IsNullOrWhiteSpace(configuredDirectory))
        {
            configuredDirectory = configuration["Persistence:DataDirectory"];
        }

        string dataDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(contentRootPath, "Data")
            : configuredDirectory!;
        return Path.IsPathRooted(dataDirectory)
            ? dataDirectory
            : Path.Combine(contentRootPath, dataDirectory);
    }

    private static ClashOfRimServerConfiguration LoadServerConfiguration(IConfiguration configuration)
    {
        return new ClashOfRimServerConfiguration(
            authenticationDebugMode: LoadAuthenticationDebugMode(configuration),
            steamWebApiKey: LoadSteamWebApiKey(configuration),
            steamAppId: LoadSteamAppId(configuration));
    }

    private static long ResolveMaxRequestBodySizeBytes(IConfiguration configuration)
    {
        string? value = Environment.GetEnvironmentVariable("CLASH_OF_RIM_MAX_REQUEST_BODY_SIZE_BYTES");
        if (string.IsNullOrWhiteSpace(value))
        {
            value = configuration["Server:MaxRequestBodySizeBytes"];
        }

        return long.TryParse(value, out long parsed) && parsed > 0
            ? parsed
            : DefaultMaxRequestBodySizeBytes;
    }

    private static ISteamAuthTicketValidator BuildSteamAuthTicketValidator(ClashOfRimServerConfiguration configuration)
    {
        if (configuration.AuthenticationDebugMode)
        {
            return new DevelopmentSteamAuthTicketValidator();
        }

        return string.IsNullOrWhiteSpace(configuration.SteamWebApiKey)
            ? new DevelopmentSteamAuthTicketValidator()
            : new SteamWebApiAuthTicketValidator(configuration.SteamWebApiKey!, configuration.SteamAppId);
    }

    private static bool LoadAuthenticationDebugMode(IConfiguration configuration)
    {
        string? value = Environment.GetEnvironmentVariable("CLASH_OF_RIM_AUTH_DEBUG");
        if (string.IsNullOrWhiteSpace(value))
        {
            value = configuration["Authentication:DebugMode"];
        }

        return LoadBool(value, ClashOfRimServerConfiguration.DefaultAuthenticationDebugMode);
    }

    private static string? LoadSteamWebApiKey(IConfiguration configuration)
    {
        string? value = Environment.GetEnvironmentVariable("CLASH_OF_RIM_STEAM_WEB_API_KEY");
        if (string.IsNullOrWhiteSpace(value))
        {
            value = configuration["Authentication:SteamWebApiKey"];
        }

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static uint LoadSteamAppId(IConfiguration configuration)
    {
        string? value = Environment.GetEnvironmentVariable("CLASH_OF_RIM_STEAM_APP_ID");
        if (string.IsNullOrWhiteSpace(value))
        {
            value = configuration["Authentication:SteamAppId"];
        }

        return uint.TryParse(value, out uint parsed) && parsed > 0
            ? parsed
            : ClashOfRimServerConfiguration.DefaultSteamAppId;
    }

    private static bool LoadBool(string? value, bool fallback)
    {
        return bool.TryParse(value, out bool parsed) ? parsed : fallback;
    }
}
