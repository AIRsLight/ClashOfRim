namespace AIRsLight.ClashOfRim.Compatibility;

public sealed record ModConfigComparisonRule
{
    public string PackageId { get; init; } = "";
    public string? FileName { get; init; }
    public ModConfigComparisonMode Mode { get; init; } = ModConfigComparisonMode.Enforce;
}
