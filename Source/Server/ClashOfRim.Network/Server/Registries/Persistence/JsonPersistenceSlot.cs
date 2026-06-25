using Microsoft.Data.Sqlite;
using System.Globalization;

namespace AIRsLight.ClashOfRim.Network;

internal interface IJsonPersistenceSlot
{
    string? Read();

    void Write(string json);
}

internal interface IBinaryPersistenceSlot
{
    byte[]? ReadBinary(string binaryKey);

    void WriteBinary(string binaryKey, byte[]? bytes);
}

internal sealed class FileJsonPersistenceSlot : IJsonPersistenceSlot
{
    private readonly string path;

    public FileJsonPersistenceSlot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = path;
    }

    public string? Read()
    {
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void Write(string json)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }
}

internal sealed class FileBinaryPersistenceSlot : IBinaryPersistenceSlot
{
    private readonly string rootPath;

    public FileBinaryPersistenceSlot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        this.rootPath = rootPath;
    }

    public byte[]? ReadBinary(string binaryKey)
    {
        string path = BinaryPath(binaryKey);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void WriteBinary(string binaryKey, byte[]? bytes)
    {
        string path = BinaryPath(binaryKey);
        if (bytes is null || bytes.Length == 0)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, path, overwrite: true);
    }

    private string BinaryPath(string binaryKey)
    {
        return Path.Combine(rootPath, SanitizeBinaryKey(binaryKey) + ".bin");
    }

    private static string SanitizeBinaryKey(string binaryKey)
    {
        return string.Concat(binaryKey.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
    }
}

internal sealed class SqliteJsonPersistenceSlot : IJsonPersistenceSlot, IBinaryPersistenceSlot
{
    private readonly string connectionString;
    private readonly string documentKey;

    public SqliteJsonPersistenceSlot(string databasePath, string documentKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKey);

        string? directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        connectionString = builder.ToString();
        this.documentKey = documentKey;
        Initialize();
    }

    public string? Read()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select content_json
            from server_documents
            where document_key = $document_key;
            """;
        command.Parameters.AddWithValue("$document_key", documentKey);
        object? value = command.ExecuteScalar();
        return value == DBNull.Value ? null : value as string;
    }

    public void Write(string json)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into server_documents (
                document_key,
                content_json,
                updated_at_utc
            ) values (
                $document_key,
                $content_json,
                $updated_at_utc
            )
            on conflict(document_key) do update set
                content_json = excluded.content_json,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$document_key", documentKey);
        command.Parameters.AddWithValue("$content_json", json);
        command.Parameters.AddWithValue("$updated_at_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public byte[]? ReadBinary(string binaryKey)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select content_blob
            from server_binary_documents
            where document_key = $document_key
                and binary_key = $binary_key;
            """;
        command.Parameters.AddWithValue("$document_key", documentKey);
        command.Parameters.AddWithValue("$binary_key", binaryKey);
        object? value = command.ExecuteScalar();
        return value == DBNull.Value ? null : value as byte[];
    }

    public void WriteBinary(string binaryKey, byte[]? bytes)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        if (bytes is null || bytes.Length == 0)
        {
            command.CommandText = """
                delete from server_binary_documents
                where document_key = $document_key
                    and binary_key = $binary_key;
                """;
            command.Parameters.AddWithValue("$document_key", documentKey);
            command.Parameters.AddWithValue("$binary_key", binaryKey);
            command.ExecuteNonQuery();
            return;
        }

        command.CommandText = """
            insert into server_binary_documents (
                document_key,
                binary_key,
                content_blob,
                updated_at_utc
            ) values (
                $document_key,
                $binary_key,
                $content_blob,
                $updated_at_utc
            )
            on conflict(document_key, binary_key) do update set
                content_blob = excluded.content_blob,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$document_key", documentKey);
        command.Parameters.AddWithValue("$binary_key", binaryKey);
        command.Parameters.AddWithValue("$content_blob", bytes);
        command.Parameters.AddWithValue("$updated_at_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private void Initialize()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists server_documents (
                document_key text primary key not null,
                content_json text not null,
                updated_at_utc text not null
            );

            create table if not exists server_binary_documents (
                document_key text not null,
                binary_key text not null,
                content_blob blob not null,
                updated_at_utc text not null,
                primary key (document_key, binary_key)
            );
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
