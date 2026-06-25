using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.NetworkServer;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

ServerConfigurationFileBootstrapper.Ensure(args);
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
