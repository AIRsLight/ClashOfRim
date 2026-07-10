using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

/// <summary>
/// Coordinates all server-owned persistence migrations. New persistent formats
/// must register an explicit versioned migrator here before they are released.
/// </summary>
public sealed class ServerPersistenceMigrationService
{
    public ServerPersistenceMigrationService(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = dataDirectory;
    }

    public string DataDirectory { get; }

    public ServerPersistenceMigrationResult Migrate()
    {
        string snapshotDirectory = Path.Combine(DataDirectory, "snapshots");
        ServerSnapshotPackageMigrationResult snapshots = ServerSnapshotPackageMigrator.Migrate(snapshotDirectory);
        ServerDatabaseMigrationResult database = ServerDatabaseMigrator.Migrate(
            Path.Combine(DataDirectory, "server.sqlite"),
            snapshotDirectory);
        return new ServerPersistenceMigrationResult(database, snapshots);
    }
}

public sealed record ServerPersistenceMigrationResult(
    ServerDatabaseMigrationResult Database,
    ServerSnapshotPackageMigrationResult Snapshots)
{
    public bool Changed =>
        Database.AppliedMigrations.Count > 0
        || Snapshots.AppliedMigrations.Count > 0;
}

public sealed record ServerSnapshotPackageMigrationResult(
    int TotalPackages,
    int CurrentPackages,
    IReadOnlyList<string> AppliedMigrations);

public static class ServerSnapshotPackageMigrator
{
    private static readonly IReadOnlyList<SnapshotMigrationStep> Steps = Array.Empty<SnapshotMigrationStep>();

    public static ServerSnapshotPackageMigrationResult Migrate(string snapshotDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        if (!Directory.Exists(snapshotDirectory))
        {
            return new ServerSnapshotPackageMigrationResult(0, 0, Array.Empty<string>());
        }

        string packageDirectory = Path.Combine(snapshotDirectory, "packages");
        if (!Directory.Exists(packageDirectory))
        {
            return new ServerSnapshotPackageMigrationResult(0, 0, Array.Empty<string>());
        }

        int totalPackages = 0;
        int currentPackages = 0;
        var appliedMigrations = new List<string>();
        foreach (string packagePath in Directory.EnumerateFiles(
                     packageDirectory,
                     "*" + SaveSnapshotPackageFileReader.PackageExtension,
                     SearchOption.TopDirectoryOnly))
        {
            totalPackages++;
            SaveSnapshotPackageFileReadResult? read = SaveSnapshotPackageFileReader.ReadPackage(
                packagePath,
                new SaveSnapshotPackageFileReadOptions { RebuildIndex = false });
            if (read?.Persisted.Envelope.PackageVersion is not { Length: > 0 } packageVersion)
            {
                throw new InvalidOperationException(
                    $"Snapshot package '{Path.GetFileName(packagePath)}' cannot be read and cannot be migrated.");
            }

            if (string.Equals(
                    packageVersion,
                    SaveSnapshotPackageBuilder.CurrentPackageVersion,
                    StringComparison.Ordinal))
            {
                currentPackages++;
                continue;
            }

            SnapshotMigrationStep? step = Steps.SingleOrDefault(candidate =>
                string.Equals(candidate.FromVersion, packageVersion, StringComparison.Ordinal));
            if (step is null)
            {
                throw new InvalidOperationException(
                    $"Snapshot package '{Path.GetFileName(packagePath)}' uses unsupported format '{packageVersion}'. " +
                    $"Current format is '{SaveSnapshotPackageBuilder.CurrentPackageVersion}'.");
            }

            throw new InvalidOperationException(
                $"Snapshot migration {step.FromVersion}->{step.ToVersion} is registered but has no package writer.");
        }

        return new ServerSnapshotPackageMigrationResult(totalPackages, currentPackages, appliedMigrations);
    }

    private sealed record SnapshotMigrationStep(string FromVersion, string ToVersion);
}
