namespace AIRsLight.ClashOfRim.Compatibility;

public sealed record ModManifestEntry
{
    public int LoadOrder { get; init; }
    public string PackageId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public string WorkshopId { get; init; } = "";
    public string WorkshopItemState { get; init; } = "";
    public long WorkshopLocalInstalledAtUnix { get; init; }
    public ModCompatibilityRole Role { get; init; } = ModCompatibilityRole.Required;
    public IReadOnlyList<ControlledFileEntry> Files { get; init; } = [];
    public IReadOnlyList<ModConfigDigest> Configs { get; init; } = [];
}
