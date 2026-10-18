using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal sealed class SnapshotColonyLocations(ServerPluginRegistry plugins, CompatibilityBaselineRegistry compatibility)
{
    internal IReadOnlyList<SnapshotColonyAnchor> Extract(SaveSnapshotIndex index)
        => PlayerColonyAnchors.Extract(index, plugins.ActiveWorldObjectClassifiers(compatibility.Current)
            .Select(classifier => (Func<WorldObjectSummary, bool>)classifier.IsPlayerColonyAnchor).ToArray())
            .Select(anchor => anchor with { MapUniqueId = MapId(anchor.MapUniqueId) }).ToArray();

    private static string? MapId(string? id) => string.IsNullOrWhiteSpace(id) ? null
        : id.StartsWith("Map_", StringComparison.Ordinal) ? id : "Map_" + id;
}
