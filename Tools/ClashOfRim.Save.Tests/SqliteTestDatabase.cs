using Microsoft.Data.Sqlite;

internal static class SqliteTestDatabase
{
    public static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + path);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
