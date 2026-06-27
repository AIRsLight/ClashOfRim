namespace AIRsLight.ClashOfRim.Compatibility;

public sealed record CompatibilityManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string ManifestId { get; init; } = "";
    public string ProtocolVersion { get; init; } = "";
    public string RimWorldVersion { get; init; } = "";
    public string GameLanguage { get; init; } = "";
    public IReadOnlyList<string> DlcIds { get; init; } = [];
    public string ConfigVersion { get; init; } = "";
    public string ConfigSha256 { get; init; } = "";
    public IReadOnlyList<ModConfigComparisonRule> ModConfigRules { get; init; } = [];
    public IReadOnlyList<ModManifestEntry> Mods { get; init; } = [];
    public IReadOnlyList<DefSummary> DefSummaries { get; init; } = [];
}
