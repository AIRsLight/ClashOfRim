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

    public ServerPersistenceMigrationAssessment Assess(ServerDatabaseMigrationOptions? databaseOptions = null)
    {
        string databasePath = Path.Combine(DataDirectory, "server.sqlite");
        string snapshotDirectory = Path.Combine(DataDirectory, "snapshots");
        ServerDatabaseMigrationAssessment database = ServerDatabaseMigrator.Assess(databasePath, databaseOptions);
        ServerSnapshotPackageMigrationAssessment snapshots = ServerSnapshotPackageMigrator.Assess(snapshotDirectory);
        ServerPersistenceMigrationStatus status = CombineStatus(database.Status, snapshots.Status);
        var filesToModify = new List<string>();
        if (database.Status == ServerPersistenceMigrationStatus.SafeMigrationAvailable)
        {
            filesToModify.Add(databasePath);
            filesToModify.Add(databasePath + "-wal");
            filesToModify.Add(databasePath + "-shm");
        }

        filesToModify.AddRange(snapshots.FilesToModify);
        return new ServerPersistenceMigrationAssessment(
            status,
            database,
            snapshots,
            filesToModify.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    public ServerPersistenceStartupResult PrepareForStartup()
    {
        ServerPersistenceMigrationAssessment assessment = Assess();
        if (assessment.Status == ServerPersistenceMigrationStatus.Ready)
        {
            return new ServerPersistenceStartupResult(assessment, null);
        }

        if (assessment.Status != ServerPersistenceMigrationStatus.SafeMigrationAvailable)
        {
            return new ServerPersistenceStartupResult(assessment, null);
        }

        return new ServerPersistenceStartupResult(assessment, Migrate());
    }

    public ServerPersistenceMigrationResult ValidateForStartup()
    {
        string snapshotDirectory = Path.Combine(DataDirectory, "snapshots");
        ServerSnapshotPackageMigrationAssessment snapshotAssessment = ServerSnapshotPackageMigrator.Assess(snapshotDirectory);
        if (snapshotAssessment.Status != ServerPersistenceMigrationStatus.Ready)
        {
            throw new ServerDatabaseMigrationRequiredException(
                $"Snapshot packages are not ready for startup: {snapshotAssessment.Status}.");
        }

        var snapshots = new ServerSnapshotPackageMigrationResult(
            snapshotAssessment.TotalPackages,
            snapshotAssessment.CurrentPackages,
            Array.Empty<string>());
        ServerDatabaseMigrationResult database = ServerDatabaseMigrator.ValidateForStartup(
            Path.Combine(DataDirectory, "server.sqlite"),
            snapshotDirectory);
        return new ServerPersistenceMigrationResult(database, snapshots);
    }

    public ServerPersistenceMigrationResult Migrate(ServerDatabaseMigrationOptions? databaseOptions = null)
    {
        ServerPersistenceMigrationAssessment assessment = Assess(databaseOptions);
        if (assessment.Status is not (ServerPersistenceMigrationStatus.Ready
            or ServerPersistenceMigrationStatus.SafeMigrationAvailable))
        {
            throw new ServerDatabaseMigrationRequiredException(
                $"Persistence migration cannot continue with status {assessment.Status}.");
        }

        string? backupDirectory = assessment.FilesToModify.Count == 0
            ? null
            : ServerPersistenceMigrationBackup.Create(DataDirectory, assessment.FilesToModify);
        string snapshotDirectory = Path.Combine(DataDirectory, "snapshots");
        ServerSnapshotPackageMigrationResult snapshots = ServerSnapshotPackageMigrator.Migrate(snapshotDirectory);
        ServerDatabaseMigrationResult database = ServerDatabaseMigrator.Migrate(
            Path.Combine(DataDirectory, "server.sqlite"),
            snapshotDirectory,
            databaseOptions);
        return new ServerPersistenceMigrationResult(database, snapshots, backupDirectory);
    }

    private static ServerPersistenceMigrationStatus CombineStatus(
        ServerPersistenceMigrationStatus database,
        ServerPersistenceMigrationStatus snapshots)
    {
        ServerPersistenceMigrationStatus[] precedence =
        [
            ServerPersistenceMigrationStatus.ServerUpgradeRequired,
            ServerPersistenceMigrationStatus.SourceVersionRequired,
            ServerPersistenceMigrationStatus.MigrationStepMissing,
            ServerPersistenceMigrationStatus.SafeMigrationAvailable
        ];
        return precedence.FirstOrDefault(status => database == status || snapshots == status, ServerPersistenceMigrationStatus.Ready);
    }
}

public sealed record ServerPersistenceMigrationResult(
    ServerDatabaseMigrationResult Database,
    ServerSnapshotPackageMigrationResult Snapshots,
    string? BackupDirectory = null)
{
    public bool Changed =>
        !string.IsNullOrWhiteSpace(BackupDirectory)
        || Database.AppliedMigrations.Count > 0
        || Snapshots.AppliedMigrations.Count > 0;
}

public sealed record ServerPersistenceMigrationAssessment(
    ServerPersistenceMigrationStatus Status,
    ServerDatabaseMigrationAssessment Database,
    ServerSnapshotPackageMigrationAssessment Snapshots,
    IReadOnlyList<string> FilesToModify);

public sealed record ServerPersistenceStartupResult(
    ServerPersistenceMigrationAssessment Assessment,
    ServerPersistenceMigrationResult? Migration)
{
    public bool CanStart => Assessment.Status is ServerPersistenceMigrationStatus.Ready
        or ServerPersistenceMigrationStatus.SafeMigrationAvailable;
}

public sealed record ServerSnapshotPackageMigrationResult(
    int TotalPackages,
    int CurrentPackages,
    IReadOnlyList<string> AppliedMigrations);

public sealed record ServerSnapshotPackageMigrationAssessment(
    ServerPersistenceMigrationStatus Status,
    int TotalPackages,
    int CurrentPackages,
    string? UnsupportedVersion,
    IReadOnlyList<string> FilesToModify);

public static class ServerSnapshotPackageMigrator
{
    private static readonly IReadOnlyList<SnapshotMigrationStep> Steps = Array.Empty<SnapshotMigrationStep>();

    public static ServerSnapshotPackageMigrationAssessment Assess(string snapshotDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        string packageDirectory = Path.Combine(snapshotDirectory, "packages");
        if (!Directory.Exists(packageDirectory))
        {
            return new ServerSnapshotPackageMigrationAssessment(
                ServerPersistenceMigrationStatus.Ready,
                0,
                0,
                null,
                Array.Empty<string>());
        }

        int totalPackages = 0;
        int currentPackages = 0;
        var filesToModify = new List<string>();
        foreach (string packagePath in Directory.EnumerateFiles(
                     packageDirectory,
                     "*" + SaveSnapshotPackageFileReader.PackageExtension,
                     SearchOption.TopDirectoryOnly))
        {
            totalPackages++;
            PersistedSaveSnapshotPackage? persisted = SaveSnapshotPackageFileReader.ReadMetadata(packagePath);
            string? version = persisted?.Envelope.PackageVersion;
            if (string.IsNullOrWhiteSpace(version))
            {
                throw new InvalidDataException(
                    $"Snapshot package '{Path.GetFileName(packagePath)}' is corrupt or unreadable.");
            }

            if (string.Equals(version, SaveSnapshotPackageBuilder.CurrentPackageVersion, StringComparison.Ordinal))
            {
                currentPackages++;
                continue;
            }

            if (!HasCompleteMigrationPath(version))
            {
                return new ServerSnapshotPackageMigrationAssessment(
                    ServerPersistenceMigrationStatus.MigrationStepMissing,
                    totalPackages,
                    currentPackages,
                    version,
                    Array.Empty<string>());
            }

            filesToModify.Add(packagePath);
        }

        return new ServerSnapshotPackageMigrationAssessment(
            filesToModify.Count == 0
                ? ServerPersistenceMigrationStatus.Ready
                : ServerPersistenceMigrationStatus.SafeMigrationAvailable,
            totalPackages,
            currentPackages,
            null,
            filesToModify);
    }

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

            string currentVersion = packageVersion;
            while (!string.Equals(currentVersion, SaveSnapshotPackageBuilder.CurrentPackageVersion, StringComparison.Ordinal))
            {
                SnapshotMigrationStep? step = Steps.SingleOrDefault(candidate =>
                    string.Equals(candidate.FromVersion, currentVersion, StringComparison.Ordinal));
                if (step is null)
                {
                    throw new InvalidOperationException(
                        $"Snapshot package '{Path.GetFileName(packagePath)}' uses unsupported format '{currentVersion}'. " +
                        $"Current format is '{SaveSnapshotPackageBuilder.CurrentPackageVersion}'.");
                }

                step.Apply(packagePath);
                PersistedSaveSnapshotPackage? migrated = SaveSnapshotPackageFileReader.ReadMetadata(packagePath);
                if (!string.Equals(migrated?.Envelope.PackageVersion, step.ToVersion, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Snapshot migration {step.FromVersion}->{step.ToVersion} did not produce the expected package version.");
                }

                appliedMigrations.Add($"{Path.GetFileName(packagePath)}:{step.FromVersion}->{step.ToVersion}");
                currentVersion = step.ToVersion;
            }

            currentPackages++;
        }

        return new ServerSnapshotPackageMigrationResult(totalPackages, currentPackages, appliedMigrations);
    }

    private sealed record SnapshotMigrationStep(
        string FromVersion,
        string ToVersion,
        Action<string> Apply);

    private static bool HasCompleteMigrationPath(string sourceVersion)
    {
        string version = sourceVersion;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.Equals(version, SaveSnapshotPackageBuilder.CurrentPackageVersion, StringComparison.Ordinal)
               && visited.Add(version))
        {
            SnapshotMigrationStep? step = Steps.SingleOrDefault(candidate =>
                string.Equals(candidate.FromVersion, version, StringComparison.Ordinal));
            if (step is null || string.Equals(step.ToVersion, version, StringComparison.Ordinal))
            {
                return false;
            }

            version = step.ToVersion;
        }

        return string.Equals(version, SaveSnapshotPackageBuilder.CurrentPackageVersion, StringComparison.Ordinal);
    }
}
