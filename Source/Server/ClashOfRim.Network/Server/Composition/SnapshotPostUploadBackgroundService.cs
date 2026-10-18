using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using Microsoft.Extensions.Hosting;

namespace AIRsLight.ClashOfRim.Network.Plugins;

internal sealed class SnapshotPostUploadBackgroundService(SnapshotPostUploadBackground background) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => background.RunAsync(stoppingToken);
}
