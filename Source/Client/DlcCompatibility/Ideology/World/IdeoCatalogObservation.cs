using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

// Tracks content timestamps, not cached packages. Every explicit collection still
// exports the live ideology, including changes made by third-party mods.
internal sealed class IdeoCatalogObservation
{
    private readonly Dictionary<string, (string Content, long? Tick)> observations = new(StringComparer.Ordinal);

    internal void Observe(IReadOnlyList<ModWorldIdeoSummaryDto> ideos, long? tick)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ideo in ideos)
        {
            present.Add(ideo.GlobalKey);
            string? snapshot = ideo.SourceSnapshotId, xml = ideo.SavedIdeoPackageXml;
            string content;
            try
            {
                // Provenance and observation time are not ideology content. The
                // freshly computed hash covers the full XML without duplicating it.
                ideo.SourceSnapshotId = null;
                ideo.UpdatedAtGameTicks = null;
                if (!string.IsNullOrEmpty(ideo.SavedIdeoPackageSha256)) ideo.SavedIdeoPackageXml = null;
                content = ClientProtocolJson.Serialize(ideo);
            }
            finally { ideo.SourceSnapshotId = snapshot; ideo.SavedIdeoPackageXml = xml; }
            if (!observations.TryGetValue(ideo.GlobalKey, out var previous) || previous.Content != content)
                observations[ideo.GlobalKey] = previous = (content, tick);
            ideo.UpdatedAtGameTicks = previous.Tick;
        }
        foreach (string key in observations.Keys.Where(key => !present.Contains(key)).ToArray()) observations.Remove(key);
    }
}
