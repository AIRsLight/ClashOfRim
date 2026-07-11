namespace AIRsLight.ClashOfRim.Network;

public static class ServerPersistenceMigrationBackup
{
    public static string Create(string dataDirectory, IEnumerable<string> sourceFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(sourceFiles);

        string dataRoot = Path.GetFullPath(dataDirectory);
        IReadOnlyList<string> files = sourceFiles
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
        {
            throw new InvalidOperationException("Migration backup requires at least one existing source file.");
        }

        string backupRoot = CreateBackupDirectory(dataRoot);
        foreach (string sourcePath in files)
        {
            string relativePath = Path.GetRelativePath(dataRoot, sourcePath);
            if (relativePath == ".."
                || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Migration backup source is outside the server data directory: " + sourcePath);
            }

            string destinationPath = Path.Combine(backupRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }

        return backupRoot;
    }

    private static string CreateBackupDirectory(string dataRoot)
    {
        string backupParent = Path.Combine(dataRoot, "backups");
        Directory.CreateDirectory(backupParent);
        string baseName = "migration-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        for (int suffix = 0; ; suffix++)
        {
            string candidate = Path.Combine(backupParent, suffix == 0 ? baseName : baseName + "-" + suffix);
            if (Directory.Exists(candidate))
            {
                continue;
            }

            Directory.CreateDirectory(candidate);
            return candidate;
        }
    }
}
