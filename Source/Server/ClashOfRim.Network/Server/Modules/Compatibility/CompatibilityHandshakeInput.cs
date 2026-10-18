using static AIRsLight.ClashOfRim.Network.Modules.Compatibility.CompatibilityHandshakeContract;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Compatibility;

internal sealed record CompatibilityHandshakeInput(bool HasFullManifest, string ManifestId,
        CompatibilityManifest? Manifest, string? ManifestFailure, CompatibilityManifestSummary? Summary, string SummaryFailure)
{
    internal static CompatibilityHandshakeInput Prepare(string? json, string? id, string? summaryJson)
    {
        bool full = !string.IsNullOrWhiteSpace(json);
        CompatibilityManifest? manifest = null;
        CompatibilityManifestSummary? summary = null;
        string? failure = null;
        string summaryFailure = string.Empty;
        if (full)
        {
            if (!TryReadCompatibilityManifest(json, out manifest, out string reason)) failure = reason;
            else if (manifest is not null && !ValidCompatibilityManifestShape(manifest))
                failure = InvalidManifest("Manifest contains missing or duplicate fields.");
        }
        else
        {
            TryReadCompatibilityManifestSummary(summaryJson, out summary, out summaryFailure);
            if (summary is not null && (!ValidCompatibilityHeader(summary.SchemaVersion, summary.ManifestId, summary.ProtocolVersion, summary.RimWorldVersion)
                || !UniqueCompatibilityKeys(summary.DlcIds, x => x) || !UniqueCompatibilityKeys(summary.Mods, x => x.PackageId)
                || !UniqueCompatibilityKeys(summary.DefSummaries, x => x.Name)))
            {
                summary = null;
                summaryFailure = InvalidManifest("Summary contains missing or duplicate fields.");
            }
        }
        return new(full, (id ?? string.Empty).Trim(), manifest, failure, summary, summaryFailure);
    }

    private static bool ValidCompatibilityHeader(int schema, string? id, string? protocol, string? game)
        => schema > 0 && !string.IsNullOrWhiteSpace(id) && protocol == ProtocolApiVersion.Current && !string.IsNullOrWhiteSpace(game);

    private static bool UniqueCompatibilityKeys<T>(IReadOnlyList<T>? values, Func<T, string?> key) where T : class
    {
        if (values is null) return false;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return values.All(value => value is not null && key(value) is { } name && !string.IsNullOrWhiteSpace(name) && keys.Add(name.Trim()));
    }

    private static bool ValidCompatibilityManifestShape(CompatibilityManifest manifest)
        => ValidCompatibilityHeader(manifest.SchemaVersion, manifest.ManifestId, manifest.ProtocolVersion, manifest.RimWorldVersion)
            && UniqueCompatibilityKeys(manifest.DlcIds, x => x) && UniqueCompatibilityKeys(manifest.DefSummaries, x => x.Name)
            && manifest.ModConfigRules is not null && manifest.ModConfigRules.All(x => x is not null)
            && UniqueCompatibilityKeys(manifest.Mods, x => x.PackageId)
            && manifest.Mods.All(mod => UniqueCompatibilityKeys(mod.Files, x => x.RelativePath?.Replace('\\', '/').TrimStart('/'))
                && UniqueCompatibilityKeys(mod.Configs, x => x.FileName));

}
