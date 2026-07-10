using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.NetworkServer;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

ServerConfigurationFileBootstrapper.Ensure(args);
if (TryRunStartupMigration(args))
{
    return;
}

WebApplication app = ClashOfRimNetworkServer.Build(args);
ILogger logger = app.Logger;

AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
{
    if (eventArgs.ExceptionObject is Exception exception)
    {
        logger.LogCritical(exception, ServerLocalization.Text("Server.ProcessUnhandledException"), eventArgs.IsTerminating);
    }
    else
    {
        logger.LogCritical(
            ServerLocalization.Text("Server.ProcessUnhandledObject"),
            eventArgs.ExceptionObject,
            eventArgs.IsTerminating);
    }
};

TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
{
    logger.LogError(eventArgs.Exception, ServerLocalization.Text("Server.ProcessUnobservedTask"));
    eventArgs.SetObserved();
};

IHostApplicationLifetime lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() => logger.LogInformation(ServerLocalization.Text("Server.ApplicationStopping")));
lifetime.ApplicationStopped.Register(() => logger.LogInformation(ServerLocalization.Text("Server.ApplicationStopped")));
ServerConsoleCommandService.Start(app, logger);

try
{
    await app.RunAsync();
}
catch (Exception ex)
{
    logger.LogCritical(ex, ServerLocalization.Text("Server.HostTerminated"));
    throw;
}
finally
{
    logger.LogInformation(ServerLocalization.Text("Server.ProcessExiting"));
}

static bool TryRunStartupMigration(string[] args)
{
    int commandIndex = Array.FindIndex(args, argument =>
        string.Equals(argument, "migrate", StringComparison.OrdinalIgnoreCase)
        || string.Equals(argument, "--migrate", StringComparison.OrdinalIgnoreCase));
    if (commandIndex < 0)
    {
        return false;
    }

    try
    {
        ServerDatabaseMigrationOptions? options = ParseMigrationOptions(args, commandIndex);
        string contentRootPath = ServerConfigurationFileBootstrapper.ResolveContentRootPath(args);
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(contentRootPath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
        ServerLocalizationFileLoader.Load(contentRootPath);
        string language = configuration["Localization:Language"] ?? ServerLocalization.DetectOperatingSystemLanguage();
        if (ServerLocalization.HasLanguage(language))
        {
            ServerLocalization.SetDefaultLanguage(language);
        }

        string dataDirectory = ClashOfRimNetworkServer.ResolveDataDirectory(configuration, contentRootPath);
        ServerPersistenceMigrationResult result = new ServerPersistenceMigrationService(dataDirectory).Migrate(options);
        Console.WriteLine(ServerLocalization.Text(
            "Cli.MigrationSummary",
            new Dictionary<string, string?>
            {
                ["DATABASE_FROM"] = result.Database.StartingVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["DATABASE_TO"] = result.Database.FinalVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["DATABASE_STEPS"] = result.Database.AppliedMigrations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["SNAPSHOTS"] = result.Snapshots.TotalPackages.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["SNAPSHOT_STEPS"] = result.Snapshots.AppliedMigrations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }));
        if (result.Database.RequiresWorldSubstrateRebaseline)
        {
            Console.WriteLine(ServerLocalization.Text("Cli.MigrationWorldRebaselineRequired"));
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.Message);
        Environment.ExitCode = 1;
    }

    return true;
}

static ServerDatabaseMigrationOptions? ParseMigrationOptions(string[] args, int commandIndex)
{
    int next = commandIndex + 1;
    if (next >= args.Length)
    {
        return null;
    }

    if (next + 1 < args.Length
        && string.Equals(args[next], "--from", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(args[next + 1], out int declaredSourceVersion)
        && declaredSourceVersion > 0)
    {
        return new ServerDatabaseMigrationOptions(declaredSourceVersion);
    }

    throw new InvalidOperationException("Usage: migrate [--from database-version]");
}
