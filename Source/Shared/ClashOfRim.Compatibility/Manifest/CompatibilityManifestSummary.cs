namespace AIRsLight.ClashOfRim.Compatibility;

public sealed record CompatibilityManifestSummary : ICompatibilityManifestMetadata
{
    public int SchemaVersion { get; init; } = 1;
    public string ManifestId { get; init; } = "";
    public string ProtocolVersion { get; init; } = "";
    public string RimWorldVersion { get; init; } = "";
    public string GameLanguage { get; init; } = "";
    public IReadOnlyList<string> DlcIds { get; init; } = [];
    public string ConfigSha256 { get; init; } = "";
    // Older clients discard the summary when sending requested details. They must
    // receive a complete-manifest request instead of an unverifiable subset.
    public bool SupportsSelectedDetails { get; init; }
    public IReadOnlyList<ModManifestSummaryEntry> Mods { get; init; } = [];
    public IReadOnlyList<DefSummary> DefSummaries { get; init; } = [];
}

public sealed record ModManifestSummaryEntry : ICompatibilityModIdentity
{
    public int LoadOrder { get; init; }
    public string PackageId { get; init; } = "";
    public string Name { get; init; } = "";
    public ModCompatibilityRole Role { get; init; } = ModCompatibilityRole.Required;
    public string Hash { get; init; } = "";
}
