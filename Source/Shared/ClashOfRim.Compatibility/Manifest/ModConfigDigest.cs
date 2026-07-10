namespace AIRsLight.ClashOfRim.Compatibility;

public sealed record ModConfigDigest
{
    public string FileName { get; init; } = "";
    public bool HasSavedFile { get; init; } = true;
    public string Sha256 { get; init; } = "";
    public string CanonicalXml { get; init; } = "";
}
