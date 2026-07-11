using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.NetworkServer;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

ServerConfigurationFileBootstrapper.Ensure(args);
(bool shouldStart, ServerDataDirectoryLease? acquiredLease) = PreparePersistenceForStartup(args);
using ServerDataDirectoryLease? dataDirectoryLease = acquiredLease;
if (!shouldStart)
{
    return;
}

WebApplication app = ClashOfRimNetworkServer.Build(NormalizeHostArguments(args));
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

static (bool ShouldStart, ServerDataDirectoryLease? Lease) PreparePersistenceForStartup(string[] args)
{
    ServerDataDirectoryLease? lease = null;
    try
    {
        string contentRootPath = ServerConfigurationFileBootstrapper.ResolveContentRootPath(args);
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(contentRootPath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
        ServerLocalizationFileLoader.Load(contentRootPath);
        string language = ClashOfRimNetworkServer.ResolveServerLanguage(args, configuration);
        if (!ServerLocalization.HasLanguage(language))
        {
            throw new InvalidOperationException(ServerLocalization.Text(
                "Server.LanguageUnavailable",
                new Dictionary<string, string?> { ["LANGUAGE"] = language }));
        }

        ServerLocalization.SetDefaultLanguage(language);
        string dataDirectory = ClashOfRimNetworkServer.ResolveDataDirectory(configuration, contentRootPath);
        lease = ServerDataDirectoryLease.Acquire(dataDirectory);
        var migrations = new ServerPersistenceMigrationService(dataDirectory);
        bool migrationOnly = args.Any(argument => string.Equals(argument, "--migrate", StringComparison.OrdinalIgnoreCase));
        ServerDatabaseMigrationOptions? options = ParseMigrationOptions(args, migrationOnly);
        if (migrationOnly)
        {
            ServerPersistenceMigrationAssessment assessment = migrations.Assess(options);
            if (!CanExecuteMigration(assessment))
            {
                PrintMigrationAction(assessment);
                return (false, lease);
            }

            PrintMigrationResult(migrations.Migrate(options));
            return (false, lease);
        }

        ServerPersistenceStartupResult startup = migrations.PrepareForStartup();
        if (!startup.CanStart)
        {
            PrintMigrationAction(startup.Assessment);
            return (false, lease);
        }

        if (startup.Migration is not null)
        {
            Console.WriteLine(ServerLocalization.Text("Cli.MigrationAutomatic"));
            PrintMigrationResult(startup.Migration);
        }

        return (true, lease);
    }
    catch (Exception exception)
    {
        lease?.Dispose();
        Console.Error.WriteLine(MigrationFailureText(exception));
        Environment.ExitCode = 1;
        return (false, null);
    }
}

static ServerDatabaseMigrationOptions? ParseMigrationOptions(string[] args, bool migrationOnly)
{
    int fromIndex = Array.FindIndex(args, argument => string.Equals(argument, "--from", StringComparison.OrdinalIgnoreCase));
    if (fromIndex < 0)
    {
        return null;
    }

    if (migrationOnly
        && fromIndex + 1 < args.Length
        && int.TryParse(args[fromIndex + 1], out int declaredSourceVersion)
        && declaredSourceVersion > 0)
    {
        return new ServerDatabaseMigrationOptions(declaredSourceVersion);
    }

    throw new InvalidOperationException(ServerLocalization.Text("Cli.UsageMigrate"));
}

static bool CanExecuteMigration(ServerPersistenceMigrationAssessment assessment)
{
    return assessment.Status is ServerPersistenceMigrationStatus.Ready
        or ServerPersistenceMigrationStatus.SafeMigrationAvailable;
}

static void PrintMigrationAction(ServerPersistenceMigrationAssessment assessment)
{
    bool snapshotMigrationMissing = assessment.Status == ServerPersistenceMigrationStatus.MigrationStepMissing
        && assessment.Snapshots.Status == ServerPersistenceMigrationStatus.MigrationStepMissing
        && !string.IsNullOrWhiteSpace(assessment.Snapshots.UnsupportedVersion);
    string source = snapshotMigrationMissing
        ? assessment.Snapshots.UnsupportedVersion!
        : assessment.Database.SourceVersion > 0
            ? assessment.Database.SourceVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : ServerLocalization.Text("Cli.MigrationVersionUnknown");
    string target = snapshotMigrationMissing
        ? SaveSnapshotPackageBuilder.CurrentPackageVersion
        : assessment.Database.TargetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
    string key = assessment.Status switch
    {
        ServerPersistenceMigrationStatus.SourceVersionRequired => "Cli.MigrationSourceVersionRequired",
        ServerPersistenceMigrationStatus.ServerUpgradeRequired => "Cli.MigrationServerUpgradeRequired",
        ServerPersistenceMigrationStatus.MigrationStepMissing => "Cli.MigrationStepMissing",
        _ => "Cli.MigrationStepMissing"
    };
    Console.WriteLine(ServerLocalization.Text(
        key,
        new Dictionary<string, string?>
        {
            ["SOURCE"] = source,
            ["TARGET"] = target
        }));
}

static void PrintMigrationResult(ServerPersistenceMigrationResult result)
{
    if (!string.IsNullOrWhiteSpace(result.BackupDirectory))
    {
        Console.WriteLine(ServerLocalization.Text(
            "Cli.MigrationBackupCreated",
            new Dictionary<string, string?> { ["PATH"] = result.BackupDirectory }));
    }

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

static string[] NormalizeHostArguments(string[] args)
{
    return args.Select(argument =>
    {
        if (string.Equals(argument, "--content-root", StringComparison.OrdinalIgnoreCase))
        {
            return "--contentRoot";
        }

        const string prefix = "--content-root=";
        return argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? "--contentRoot=" + argument[prefix.Length..]
            : argument;
    }).ToArray();
}

static string MigrationFailureText(Exception exception)
{
    try
    {
        return ServerLocalization.Text(
            "Cli.MigrationFailed",
            new Dictionary<string, string?> { ["MESSAGE"] = exception.Message });
    }
    catch
    {
        return exception.Message;
    }
}
