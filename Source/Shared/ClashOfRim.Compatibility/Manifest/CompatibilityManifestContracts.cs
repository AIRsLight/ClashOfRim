namespace AIRsLight.ClashOfRim.Compatibility;

// Full manifests and summaries carry the same compatibility metadata and mod identity.
public interface ICompatibilityManifestMetadata
{
    int SchemaVersion { get; }
    string ProtocolVersion { get; }
    string RimWorldVersion { get; }
    IReadOnlyList<string> DlcIds { get; }
    IReadOnlyList<DefSummary> DefSummaries { get; }
}

public interface ICompatibilityModIdentity
{
    int LoadOrder { get; }
    string PackageId { get; }
    ModCompatibilityRole Role { get; }
}
