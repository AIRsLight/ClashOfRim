namespace AIRsLight.ClashOfRim.Save;

public sealed record SaveIndexReadOptions
{
    public SnapshotIdentity Identity { get; init; } = new();

    public IReadOnlyList<ISaveIndexExtension>? Extensions { get; init; }
        = SaveIndexExtensionRegistry.Registered;
}
