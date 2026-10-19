using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace ClashOfRim.GameTests;

internal sealed record WorldPersistenceEvidence(WorldConfigurationDto? Configuration, byte[]? Substrate);

// Owns reconstruction/disposal of a fresh server container for the disk probe.
// World assertions receive detached evidence, never the full server container.
internal static class WorldPersistenceProbe
{
    internal static async Task<WorldPersistenceEvidence> Read(string root, string worldId)
    {
        await using var reload = ClashOfRimNetworkServer.Build(["--contentRoot", Path.Combine(root, "Server"),
            "--Persistence:DataDirectory", Path.Combine(root, "Server", "Data"), "--Logging:LogLevel:Default", "Warning"]);
        var restored = reload.Services.GetRequiredService<ClashOfRimNetworkState>().WorldConfiguration;
        restored.TryGetWorldSubstrate(worldId, out var blob);
        return new(restored.Current, blob);
    }
}
