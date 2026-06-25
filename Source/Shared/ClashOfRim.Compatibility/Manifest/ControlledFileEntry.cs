namespace AIRsLight.ClashOfRim.Compatibility;

public sealed record ControlledFileEntry
{
    public string RelativePath { get; init; } = "";
    public long Size { get; init; }
    public string Sha256 { get; init; } = "";
    public long LastWriteUtcUnix { get; init; }
    public ModFileKind Kind { get; init; } = ModFileKind.Other;
}
