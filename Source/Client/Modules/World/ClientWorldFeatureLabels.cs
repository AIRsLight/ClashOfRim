using System;
using System.Collections.Generic;
using System.Globalization;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.World;

internal sealed record ClientWorldFeature(int Id, int Layer, ModWorldFeatureDto Data, string DefaultLabel);
internal sealed record ClientWorldFeatureLabelsPlan(string[] Labels, ClientWorldFeatureNames? Catalog, bool Matched);
internal interface IClientWorldFeatureNameGenerator
{
    string? Generate(string defName, string seed, Func<string, bool> available);
}

// One name policy for substrate restoration and later configuration projection.
// Geometry only maps labels to existing objects; it never assigns IDs or layers.
internal sealed class ClientWorldFeatureLabels
{
    private readonly IClientWorldFeatureNameGenerator generator;
    internal ClientWorldFeatureLabels(IClientWorldFeatureNameGenerator generator) => this.generator = generator;
    internal ClientWorldFeatureLabelsPlan Resolve(ModWorldConfigurationDto configuration, string? language, IReadOnlyList<ClientWorldFeature> current)
    {
        var labels = new string[current.Count];
        for (int i = 0; i < current.Count; i++) labels[i] = Fallback(current[i].Data.Label, current[i].DefaultLabel);
        var mapping = Match(configuration.Features, current);
        if (mapping is null) return new(labels, null, false);
        string normalizedLanguage = language?.Trim() ?? "";
        ModWorldFeatureNameCatalogDto? catalog = null;
        if (!string.IsNullOrWhiteSpace(configuration.WorldConfigurationId) && normalizedLanguage.Length != 0)
            foreach (var candidate in configuration.FeatureNameCatalogs)
                if (string.Equals(candidate.WorldConfigurationId?.Trim(), configuration.WorldConfigurationId.Trim(), StringComparison.Ordinal)
                    && SameLanguage(candidate.Language, normalizedLanguage)
                    && Compatible(configuration.Features, candidate.Features)) { catalog = candidate; break; }
        bool localize = catalog is null && ShouldLocalize(configuration.GameLanguage, normalizedLanguage);
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < mapping.Length; i++) used.Add(Fallback(configuration.Features[i].Label, current[mapping[i]].DefaultLabel));
        bool complete = true;
        var outgoing = new List<ModWorldFeatureDto>(mapping.Length);
        for (int i = 0; i < mapping.Length; i++)
        {
            var feature = current[mapping[i]]; var baseline = configuration.Features[i];
            string label = Fallback(catalog?.Features[i].Label, Fallback(baseline.Label, feature.DefaultLabel));
            if (localize)
            {
                bool Available(string value) => !string.IsNullOrWhiteSpace(value) && !used.Contains(value);
                string? generated = generator.Generate(feature.Data.DefName, Seed(feature, normalizedLanguage), Available);
                if (generated is not null && Available(generated)) label = generated;
                else complete = false;
            }
            labels[mapping[i]] = label; used.Add(label);
            if (localize) outgoing.Add(CopyLabel(baseline, label));
        }
        return new(labels, localize && complete && outgoing.Count > 0 ? new(normalizedLanguage, outgoing) : null, true);
    }
    internal static bool ShouldLocalize(string? baseline, string? current)
        => !string.IsNullOrWhiteSpace(baseline) && !string.IsNullOrWhiteSpace(current)
            && !SameLanguage(baseline, current);
    internal static bool SameLanguage(string? left, string? right)
        => string.Equals(left?.Trim() ?? "", right?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);
    private static int[]? Match(IReadOnlyList<ModWorldFeatureDto> baseline, IReadOnlyList<ClientWorldFeature> current)
    {
        if (baseline.Count != current.Count) return null;
        var mapping = new int[baseline.Count]; var claimed = new bool[current.Count];
        for (int i = 0; i < baseline.Count; i++)
        {
            int match = -1; var shape = Geometry(baseline[i]);
            for (int j = 0; j < current.Count; j++)
                if (shape.Matches(Geometry(current[j].Data)))
                { if (match != -1) return null; match = j; }
            // An ambiguous geometry cannot safely identify a layer or object.
            if (match < 0 || claimed[match]) return null;
            mapping[i] = match; claimed[match] = true;
        }
        return mapping;
    }
    internal static bool Compatible(IReadOnlyList<ModWorldFeatureDto> baseline, IReadOnlyList<ModWorldFeatureDto> labels)
    {
        if (baseline.Count != labels.Count) return false;
        for (int i = 0; i < baseline.Count; i++) if (!Geometry(baseline[i]).Matches(Geometry(labels[i]))) return false;
        return true;
    }
    private static WorldFeatureGeometry Geometry(ModWorldFeatureDto feature)
        => new(feature.DefName, feature.MaxDrawSizeInTiles, feature.DrawCenterX, feature.DrawCenterY, feature.DrawCenterZ);
    private static string Fallback(string? label, string fallback) => string.IsNullOrWhiteSpace(label) ? fallback : label!;
    private static ModWorldFeatureDto CopyLabel(ModWorldFeatureDto feature, string label) => new()
    { DefName = feature.DefName, Label = label, MaxDrawSizeInTiles = feature.MaxDrawSizeInTiles,
        DrawCenterX = feature.DrawCenterX, DrawCenterY = feature.DrawCenterY, DrawCenterZ = feature.DrawCenterZ };
    private static string Seed(ClientWorldFeature feature, string language)
        => string.Join("\u001f", feature.Data.DefName, feature.Layer.ToString(CultureInfo.InvariantCulture), feature.Id.ToString(CultureInfo.InvariantCulture),
            feature.Data.MaxDrawSizeInTiles.ToString("R", CultureInfo.InvariantCulture), feature.Data.DrawCenterX.ToString("R", CultureInfo.InvariantCulture),
            feature.Data.DrawCenterY.ToString("R", CultureInfo.InvariantCulture), feature.Data.DrawCenterZ.ToString("R", CultureInfo.InvariantCulture), language.ToUpperInvariant());
}
