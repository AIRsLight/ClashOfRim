using Microsoft.Data.Sqlite;

internal static class SqliteTestDirectory
{
    public static void With(string prefix, Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
