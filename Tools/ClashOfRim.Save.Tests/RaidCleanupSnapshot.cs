using AIRsLight.ClashOfRim.Save;

internal static class RaidCleanupSnapshot
{
    public static SaveSnapshotPackage Create() => SaveSnapshotPackageBuilder.FromFile(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "RaidCleanup.xml"),
        new SnapshotIdentity("attacker", "colony-a", "attacker-before-timeout"),
        DateTimeOffset.UnixEpoch,
        SnapshotPayloadEncoding.RawRws);
}
