using System.Globalization;
using System.Text.Json;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

namespace AIRsLight.ClashOfRim.Network;

/// <summary>
/// Versions the SQLite persistence schema independently from the wire protocol.
/// Add exactly one explicit migration step for every schema change.
/// </summary>
public static class ServerDatabaseSchema
{
    // Version 1 is the known JSON-document persistence layout.
    public const int LegacyJsonDocumentVersion = 1;
    public const int CurrentVersion = 2;
}

public sealed record ServerDatabaseMigrationOptions(int? DeclaredSourceVersion = null);

public sealed class ServerDatabaseMigrationRequiredException : InvalidOperationException
{
    public ServerDatabaseMigrationRequiredException(string message)
        : base(message)
    {
    }
}

public sealed record ServerDatabaseMigrationResult(
    int StartingVersion,
    int FinalVersion,
    bool CreatedNewDatabase,
    bool RequiresWorldSubstrateRebaseline,
    string? RecoveredWorldSubstrateSnapshotId,
    IReadOnlyList<string> AppliedMigrations);

public static class ServerDatabaseMigrator
{
    private const string MetadataTable = "server_schema_metadata";
    private const string SchemaVersionKey = "schema_version";

    private static readonly IReadOnlyList<MigrationStep> Steps =
    [
        new(
            FromVersion: 1,
            ToVersion: 2,
            Apply: RemoveLegacyTileGeometry)
    ];

    public static ServerDatabaseMigrationResult ValidateForStartup(string databasePath, string? snapshotDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string? directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using SqliteConnection connection = OpenConnection(databasePath);
        bool hasExistingServerData = HasExistingServerData(connection);
        if (!hasExistingServerData && !TableExists(connection, MetadataTable))
        {
            EnsureMetadataTable(connection);
            WriteVersion(connection, transaction: null, ServerDatabaseSchema.CurrentVersion);
            return new ServerDatabaseMigrationResult(
                0,
                ServerDatabaseSchema.CurrentVersion,
                true,
                false,
                null,
                Array.Empty<string>());
        }

        if (!TableExists(connection, MetadataTable))
        {
            throw CreateMigrationRequiredException("The database has no schema version metadata.");
        }

        int storedVersion = ReadStoredVersion(connection);
        EnsureSupportedVersion(storedVersion);
        if (storedVersion < ServerDatabaseSchema.CurrentVersion)
        {
            throw CreateMigrationRequiredException(
                $"The database schema is version {storedVersion}, but this server requires {ServerDatabaseSchema.CurrentVersion}.");
        }

        return new ServerDatabaseMigrationResult(
            storedVersion,
            storedVersion,
            false,
            false,
            null,
            Array.Empty<string>());
    }

    public static ServerDatabaseMigrationResult Migrate(
        string databasePath,
        string? snapshotDirectory = null,
        ServerDatabaseMigrationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string? directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using SqliteConnection connection = OpenConnection(databasePath);
        bool hadSchemaMetadata = TableExists(connection, MetadataTable);
        int storedVersion = hadSchemaMetadata ? ReadStoredVersion(connection) : 0;
        bool hasExistingServerData = HasExistingServerData(connection);
        bool createdNewDatabase = !hadSchemaMetadata && !hasExistingServerData;
        int startingVersion;
        if (storedVersion > 0)
        {
            startingVersion = storedVersion;
        }
        else if (createdNewDatabase)
        {
            startingVersion = 0;
            EnsureMetadataTable(connection);
            WriteVersion(connection, transaction: null, ServerDatabaseSchema.CurrentVersion);
        }
        else
        {
            startingVersion = ResolveUnversionedDatabaseVersion(connection, options);
        }

        EnsureSupportedVersion(startingVersion);

        int currentVersion = startingVersion == 0
            ? ServerDatabaseSchema.CurrentVersion
            : startingVersion;
        var appliedMigrations = new List<string>();
        bool requiresWorldSubstrateRebaseline = false;
        string? recoveredWorldSubstrateSnapshotId = null;
        var context = new MigrationContext(snapshotDirectory);
        while (currentVersion < ServerDatabaseSchema.CurrentVersion)
        {
            MigrationStep? step = Steps.SingleOrDefault(candidate => candidate.FromVersion == currentVersion);
            if (step is null)
            {
                throw new InvalidOperationException(
                    $"No database migration is registered for schema version {currentVersion}. " +
                    $"Supported target version is {ServerDatabaseSchema.CurrentVersion}.");
            }

            if (step.FromVersion == ServerDatabaseSchema.LegacyJsonDocumentVersion)
            {
                LegacyJsonStructuredRegistryMigrator.Import(databasePath);
            }

            EnsureMetadataTable(connection);
            using SqliteTransaction transaction = connection.BeginTransaction();
            MigrationApplicationResult application = step.Apply(connection, transaction, context);
            WriteVersion(connection, transaction, step.ToVersion);
            transaction.Commit();

            currentVersion = step.ToVersion;
            appliedMigrations.Add($"{step.FromVersion}->{step.ToVersion}");
            requiresWorldSubstrateRebaseline |= application.RequiresWorldSubstrateRebaseline;
            recoveredWorldSubstrateSnapshotId ??= application.RecoveredWorldSubstrateSnapshotId;
        }

        return new ServerDatabaseMigrationResult(
            startingVersion,
            currentVersion,
            createdNewDatabase,
            requiresWorldSubstrateRebaseline,
            recoveredWorldSubstrateSnapshotId,
            appliedMigrations);
    }

    private static int ResolveUnversionedDatabaseVersion(
        SqliteConnection connection,
        ServerDatabaseMigrationOptions? options)
    {
        if (options?.DeclaredSourceVersion is int declaredVersion)
        {
            if (declaredVersion <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Declared source version must be positive.");
            }

            return declaredVersion;
        }

        if (HasLegacyTileGeometry(connection))
        {
            return ServerDatabaseSchema.LegacyJsonDocumentVersion;
        }

        bool hasLegacyDocuments = TableExists(connection, "server_documents");
        bool hasStructuredRecords = HasStructuredRegistryData(connection);
        if (hasLegacyDocuments && !hasStructuredRecords)
        {
            return ServerDatabaseSchema.LegacyJsonDocumentVersion;
        }

        if (!hasLegacyDocuments && hasStructuredRecords)
        {
            EnsureMetadataTable(connection);
            WriteVersion(connection, transaction: null, ServerDatabaseSchema.CurrentVersion);
            return ServerDatabaseSchema.CurrentVersion;
        }

        if (hasLegacyDocuments && hasStructuredRecords)
        {
            throw CreateMigrationRequiredException(
                "The database contains both legacy JSON documents and structured records, so its migration state cannot be inferred. " +
                "Run 'migrate --from 1' to import the JSON layout, or 'migrate --from 2' only after verifying the structured records are complete.");
        }

        throw CreateMigrationRequiredException(
            "The database uses an unrecognized persistence layout. " +
            $"Review the backup, then run 'migrate --from {ServerDatabaseSchema.LegacyJsonDocumentVersion}' only if it is a JSON-document server database.");
    }

    private static void EnsureSupportedVersion(int version)
    {
        if (version > ServerDatabaseSchema.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {version} is newer than this server supports " +
                $"({ServerDatabaseSchema.CurrentVersion}). Update the server before opening this database.");
        }
    }

    private static ServerDatabaseMigrationRequiredException CreateMigrationRequiredException(string detail)
    {
        return new ServerDatabaseMigrationRequiredException(
            detail + " Stop the server and run the 'migrate' console command before starting normally.");
    }

    public static int ReadVersion(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath))
        {
            return 0;
        }

        using SqliteConnection connection = OpenConnection(databasePath);
        return TableExists(connection, MetadataTable)
            ? ReadStoredVersion(connection)
            : 0;
    }

    private static MigrationApplicationResult RemoveLegacyTileGeometry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MigrationContext context)
    {
        if (!TableExists(connection, "server_binary_documents", transaction))
        {
            return MigrationApplicationResult.None;
        }

        byte[]? existingWorldSubstrate = ReadBinaryDocument(
            connection,
            transaction,
            "world-configuration",
            "world-substrate");
        bool hasWorldSubstrate = existingWorldSubstrate is not null
            && WorldSubstratePackageCodec.TryDecode(existingWorldSubstrate, out WorldSubstratePackage? decodedSubstrate, out _)
            && decodedSubstrate is not null
            && WorldTileGeometryBinaryCodec.Decode(decodedSubstrate.TileGeometryPayload) is { Layers.Count: > 0 };
        byte[]? legacyTileGeometry = ReadBinaryDocument(
            connection,
            transaction,
            "world-configuration",
            "tile-geometry");
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            delete from server_binary_documents
            where document_key = 'world-configuration'
                and binary_key = 'tile-geometry';
            """;
        int removed = command.ExecuteNonQuery();
        if (hasWorldSubstrate || removed == 0)
        {
            return MigrationApplicationResult.None;
        }

        WorldSubstrateRecoveryResult recovery = TryRecoverWorldSubstrateFromAdministratorSnapshot(
            connection,
            transaction,
            context.SnapshotDirectory,
            legacyTileGeometry);
        if (recovery.Payload is not null)
        {
            WriteBinaryDocument(
                connection,
                transaction,
                "world-configuration",
                "world-substrate",
                recovery.Payload);
            return new MigrationApplicationResult(false, recovery.SnapshotId);
        }

        return new MigrationApplicationResult(true, null);
    }

    private static WorldSubstrateRecoveryResult TryRecoverWorldSubstrateFromAdministratorSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? snapshotDirectory,
        byte[]? legacyTileGeometry)
    {
        if (legacyTileGeometry is null
            || WorldTileGeometryBinaryCodec.Decode(legacyTileGeometry) is null
            || string.IsNullOrWhiteSpace(snapshotDirectory)
            || !Directory.Exists(snapshotDirectory))
        {
            return WorldSubstrateRecoveryResult.None;
        }

        HashSet<string> administratorUserIds = ReadAdministratorUserIds(connection, transaction);
        if (administratorUserIds.Count == 0)
        {
            return WorldSubstrateRecoveryResult.None;
        }

        string packageDirectory = Directory.Exists(Path.Combine(snapshotDirectory, "packages"))
            ? Path.Combine(snapshotDirectory, "packages")
            : snapshotDirectory;
        if (!Directory.Exists(packageDirectory))
        {
            return WorldSubstrateRecoveryResult.None;
        }

        foreach (string packagePath in Directory
                     .EnumerateFiles(packageDirectory, "*.snapshot.gz", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(path => SaveSnapshotPackageFileReader.ReadMetadata(path)?.AcceptedAtUtc))
        {
            SaveSnapshotPackageFileReadResult? read = SaveSnapshotPackageFileReader.ReadPackage(
                packagePath,
                new SaveSnapshotPackageFileReadOptions { RebuildIndex = false });
            if (read?.Persisted.Identity.OwnerId is not { Length: > 0 } ownerUserId
                || !administratorUserIds.Contains(ownerUserId)
                || read.EncodedPayload is null)
            {
                continue;
            }

            try
            {
                byte[] saveBytes = SaveSnapshotPackageFileReader.DecodePayload(
                    read.EncodedPayload,
                    read.Persisted.Envelope.PayloadEncoding);
                if (WorldSubstratePackage.TryExtract(saveBytes, out WorldSubstratePackage? substrate, out _)
                    && substrate is not null)
                {
                    var recovered = new WorldSubstratePackage(
                        substrate.PersistentRandomValue,
                        substrate.GridXml,
                        substrate.FeaturesXml,
                        substrate.LandmarksXml,
                        legacyTileGeometry);
                    return new WorldSubstrateRecoveryResult(
                        WorldSubstratePackageCodec.Encode(recovered),
                        read.Persisted.Identity.SnapshotId);
                }
            }
            catch (InvalidDataException)
            {
                // Skip an unreadable administrator snapshot and continue with older packages.
            }
            catch (NotSupportedException)
            {
                // Skip unsupported historical encodings without blocking the database migration.
            }
        }

        return WorldSubstrateRecoveryResult.None;
    }

    private static HashSet<string> ReadAdministratorUserIds(SqliteConnection connection, SqliteTransaction transaction)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (TableExists(connection, "server_keyed_json_records", transaction))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                select content_json
                from server_keyed_json_records
                where collection_key = 'world-configuration'
                    and item_key = 'state';
                """;
            AddAdministratorUserIds(command.ExecuteScalar() as string, result);
        }

        if (result.Count == 0 && TableExists(connection, "server_documents", transaction))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                select content_json
                from server_documents
                where document_key = 'world-configuration';
                """;
            AddAdministratorUserIds(command.ExecuteScalar() as string, result);
        }

        return result;
    }

    private static void AddAdministratorUserIds(string? json, ISet<string> result)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (TryGetProperty(root, "AdministratorUserId", out JsonElement primary)
                && primary.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(primary.GetString()))
            {
                result.Add(primary.GetString()!);
            }

            if (!TryGetProperty(root, "AdministratorUserIds", out JsonElement administrators)
                || administrators.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement administrator in administrators.EnumerateArray())
            {
                if (administrator.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(administrator.GetString()))
                {
                    result.Add(administrator.GetString()!);
                }
            }
        }
        catch (JsonException)
        {
            // A malformed legacy row cannot be used to select an administrator snapshot.
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static void WriteBinaryDocument(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string documentKey,
        string binaryKey,
        byte[] content)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
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
        command.Parameters.AddWithValue("$content_blob", content);
        command.Parameters.AddWithValue("$updated_at_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenConnection(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void EnsureMetadataTable(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $$"""
            create table if not exists {{MetadataTable}} (
                metadata_key text primary key not null,
                metadata_value text not null,
                updated_at_utc text not null
            );
            """;
        command.ExecuteNonQuery();
    }

    private static int ReadStoredVersion(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $$"""
            select metadata_value
            from {{MetadataTable}}
            where metadata_key = $metadata_key;
            """;
        command.Parameters.AddWithValue("$metadata_key", SchemaVersionKey);
        object? value = command.ExecuteScalar();
        if (value is null || value == DBNull.Value)
        {
            return 0;
        }

        if (!int.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int version) || version <= 0)
        {
            throw new InvalidOperationException("Database schema metadata contains an invalid schema version.");
        }

        return version;
    }

    private static void WriteVersion(SqliteConnection connection, SqliteTransaction? transaction, int version)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $$"""
            insert into {{MetadataTable}} (
                metadata_key,
                metadata_value,
                updated_at_utc
            ) values (
                $metadata_key,
                $metadata_value,
                $updated_at_utc
            )
            on conflict(metadata_key) do update set
                metadata_value = excluded.metadata_value,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$metadata_key", SchemaVersionKey);
        command.Parameters.AddWithValue("$metadata_value", version.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated_at_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static bool HasExistingServerData(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $$"""
            select exists(
                select 1
                from sqlite_master
                where type = 'table'
                    and name not like 'sqlite_%'
                    and name <> '{{MetadataTable}}'
            );
            """;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static bool HasStructuredRegistryData(SqliteConnection connection)
    {
        string[] knownTables =
        [
            "server_structured_registry_markers",
            "server_keyed_json_records",
            "server_players",
            "server_pawn_packages",
            "server_thing_packages"
        ];
        foreach (string table in knownTables)
        {
            if (TableExists(connection, table))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasLegacyTileGeometry(SqliteConnection connection)
    {
        if (!TableExists(connection, "server_binary_documents"))
        {
            return false;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select exists(
                select 1
                from server_binary_documents
                where document_key = 'world-configuration'
                    and binary_key = 'tile-geometry'
            );
            """;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static bool TableExists(
        SqliteConnection connection,
        string tableName,
        SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            select exists(
                select 1
                from sqlite_master
                where type = 'table'
                    and name = $table_name
            );
            """;
        command.Parameters.AddWithValue("$table_name", tableName);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static byte[]? ReadBinaryDocument(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string documentKey,
        string binaryKey)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
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

    private sealed record MigrationStep(
        int FromVersion,
        int ToVersion,
        Func<SqliteConnection, SqliteTransaction, MigrationContext, MigrationApplicationResult> Apply);

    private sealed record MigrationContext(string? SnapshotDirectory);

    private sealed record MigrationApplicationResult(
        bool RequiresWorldSubstrateRebaseline,
        string? RecoveredWorldSubstrateSnapshotId)
    {
        public static readonly MigrationApplicationResult None = new(false, null);
    }

    private sealed record WorldSubstrateRecoveryResult(byte[]? Payload, string? SnapshotId)
    {
        public static readonly WorldSubstrateRecoveryResult None = new(null, null);
    }
}
