namespace AIRsLight.ClashOfRim.Save;

public sealed record SnapshotUploadPolicy(IReadOnlySet<string> AllowedRimWorldVersions)
{
    public static SnapshotUploadPolicy AllowAnyVersion { get; } = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public bool IsVersionAllowed(string rimWorldVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rimWorldVersion);

        return AllowedRimWorldVersions.Count == 0 || AllowedRimWorldVersions.Contains(rimWorldVersion);
    }
}
