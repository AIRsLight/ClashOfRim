namespace AIRsLight.ClashOfRim.Save;

public sealed record SaveIndexReadContext(
    SnapshotIdentity Identity,
    string? MapUniqueId,
    SaveMetaSummary Meta)
{
    public bool HasMod(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        return Meta.ModIds.Any(modId =>
            string.Equals(modId, packageId, StringComparison.OrdinalIgnoreCase));
    }
}
