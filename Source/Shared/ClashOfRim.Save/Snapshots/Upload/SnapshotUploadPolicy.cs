namespace AIRsLight.ClashOfRim.Save;

public sealed record SnapshotUploadPolicy(IReadOnlySet<string> AllowedRimWorldVersions)
{
    public const long DefaultMaximumOriginalSaveBytes = 200L * 1024L * 1024L;
    public static SnapshotUploadPolicy AllowAnyVersion { get; } = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public long MaximumOriginalSaveBytes { get; init; } = DefaultMaximumOriginalSaveBytes;

    public bool IsVersionAllowed(string rimWorldVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rimWorldVersion);

        return AllowedRimWorldVersions.Count == 0 || AllowedRimWorldVersions.Contains(rimWorldVersion);
    }
}
