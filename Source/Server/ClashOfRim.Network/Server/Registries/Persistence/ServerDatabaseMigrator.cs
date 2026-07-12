using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIRsLight.ClashOfRim.Events;
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
    public const int CurrentVersion = 6;
}

public sealed record ServerDatabaseMigrationOptions(int? DeclaredSourceVersion = null);

public enum ServerPersistenceMigrationStatus
{
    Ready,
    SafeMigrationAvailable,
    SourceVersionRequired,
    ServerUpgradeRequired,
    MigrationStepMissing
}

public sealed record ServerDatabaseMigrationAssessment(
    ServerPersistenceMigrationStatus Status,
    int SourceVersion,
    int TargetVersion);

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
            Apply: RemoveLegacyTileGeometry),
        new(
            FromVersion: 2,
            ToVersion: 3,
            Apply: MigrateItemDeliveryPurpose),
        new(
            FromVersion: 3,
            ToVersion: 4,
            Apply: MigrateItemDeliveryEventHierarchy),
        new(
            FromVersion: 4,
            ToVersion: 5,
            Apply: CreateSnapshotPostUploadJobTable),
        new(
            FromVersion: 5,
            ToVersion: 6,
            Apply: AddSnapshotPostUploadJobState)
    ];

    public static ServerDatabaseMigrationAssessment Assess(
        string databasePath,
        ServerDatabaseMigrationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath))
        {
            return new ServerDatabaseMigrationAssessment(
                ServerPersistenceMigrationStatus.Ready,
                0,
                ServerDatabaseSchema.CurrentVersion);
        }

        using SqliteConnection connection = OpenReadOnlyConnection(databasePath);
        bool hasSchemaMetadata = TableExists(connection, MetadataTable);
        bool hasExistingServerData = HasExistingServerData(connection);
        if (!hasSchemaMetadata && !hasExistingServerData)
        {
            return new ServerDatabaseMigrationAssessment(
                ServerPersistenceMigrationStatus.Ready,
                0,
                ServerDatabaseSchema.CurrentVersion);
        }

        int sourceVersion;
        if (hasSchemaMetadata)
        {
            sourceVersion = ReadStoredVersion(connection);
        }
        else if (options?.DeclaredSourceVersion is int declaredSourceVersion)
        {
            sourceVersion = declaredSourceVersion;
        }
        else if (!TryInferUnversionedDatabaseVersion(connection, out sourceVersion))
        {
            return new ServerDatabaseMigrationAssessment(
                ServerPersistenceMigrationStatus.SourceVersionRequired,
                0,
                ServerDatabaseSchema.CurrentVersion);
        }

        if (sourceVersion > ServerDatabaseSchema.CurrentVersion)
        {
            return new ServerDatabaseMigrationAssessment(
                ServerPersistenceMigrationStatus.ServerUpgradeRequired,
                sourceVersion,
                ServerDatabaseSchema.CurrentVersion);
        }

        if (sourceVersion == ServerDatabaseSchema.CurrentVersion && hasSchemaMetadata)
        {
            return new ServerDatabaseMigrationAssessment(
                ServerPersistenceMigrationStatus.Ready,
                sourceVersion,
                ServerDatabaseSchema.CurrentVersion);
        }

        return new ServerDatabaseMigrationAssessment(
            HasCompleteMigrationPath(sourceVersion)
                ? ServerPersistenceMigrationStatus.SafeMigrationAvailable
                : ServerPersistenceMigrationStatus.MigrationStepMissing,
            sourceVersion,
            ServerDatabaseSchema.CurrentVersion);
    }

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

        if (!hadSchemaMetadata && startingVersion == ServerDatabaseSchema.CurrentVersion)
        {
            EnsureMetadataTable(connection);
            WriteVersion(connection, transaction: null, ServerDatabaseSchema.CurrentVersion);
        }

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

        bool hasLegacyDocuments = TableExists(connection, "server_documents");
        bool hasStructuredRecords = HasStructuredRegistryData(connection);
        if (hasLegacyDocuments && hasStructuredRecords)
        {
            throw CreateMigrationRequiredException(
                "The database contains both legacy JSON documents and structured records, so its migration state cannot be inferred. " +
                "Run with '--migrate --from 1' to import the JSON layout, or '--migrate --from 2' only after verifying the structured records are complete.");
        }

        if (HasLegacyTileGeometry(connection))
        {
            return ServerDatabaseSchema.LegacyJsonDocumentVersion;
        }

        if (hasLegacyDocuments && !hasStructuredRecords)
        {
            return ServerDatabaseSchema.LegacyJsonDocumentVersion;
        }

        if (!hasLegacyDocuments && hasStructuredRecords)
        {
            return ServerDatabaseSchema.CurrentVersion;
        }

        throw CreateMigrationRequiredException(
            "The database uses an unrecognized persistence layout. " +
            $"Review the backup, then run with '--migrate --from {ServerDatabaseSchema.LegacyJsonDocumentVersion}' only if it is a JSON-document server database.");
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
            detail + " Run the server with the '--migrate' startup argument before starting normally.");
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

    private static MigrationApplicationResult MigrateItemDeliveryPurpose(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MigrationContext context)
    {
        if (!TableExists(connection, "events", transaction))
        {
            return MigrationApplicationResult.None;
        }

        var payloads = new List<(string EventId, string PayloadJson)>();
        using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "select event_id, payload_json from events;";
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                payloads.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach ((string eventId, string payloadJson) in payloads)
        {
            JsonObject payload = JsonNode.Parse(payloadJson)?.AsObject()
                ?? throw new InvalidOperationException($"Event payload is not a JSON object: {eventId}");
            if (!string.Equals(payload["payloadType"]?.GetValue<string>(), "gift", StringComparison.Ordinal)
                || payload.ContainsKey(nameof(ItemDeliveryEventPayload.Purpose)))
            {
                continue;
            }

            string? legacyMessage = payload[nameof(ItemDeliveryEventPayload.Message)]?.GetValue<string>();
            ItemDeliveryPurpose purpose = legacyMessage switch
            {
                "TradeCompletedOwnerDelivery" => ItemDeliveryPurpose.TradeCompletedOwnerDelivery,
                "TradeCompletedAcceptorDelivery" => ItemDeliveryPurpose.TradeCompletedAcceptorDelivery,
                "TradeExpiredOwnerReturn" => ItemDeliveryPurpose.TradeExpiredOwnerReturn,
                "TradeBaselineChangedOwnerReturn" => ItemDeliveryPurpose.TradeBaselineChangedOwnerReturn,
                "TradeCancelledOwnerReturn" => ItemDeliveryPurpose.TradeCancelledOwnerReturn,
                "TradeApplicationFailedOwnerReturn" => ItemDeliveryPurpose.TradeApplicationFailedOwnerReturn,
                _ => ItemDeliveryPurpose.Gift
            };
            payload[nameof(ItemDeliveryEventPayload.Purpose)] = (int)purpose;

            using SqliteCommand update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "update events set payload_json = $payload_json where event_id = $event_id;";
            update.Parameters.AddWithValue("$payload_json", payload.ToJsonString());
            update.Parameters.AddWithValue("$event_id", eventId);
            update.ExecuteNonQuery();
        }

        return MigrationApplicationResult.None;
    }

    private static MigrationApplicationResult MigrateItemDeliveryEventHierarchy(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MigrationContext context)
    {
        if (!TableExists(connection, "events", transaction))
        {
            return MigrationApplicationResult.None;
        }

        var events = new List<(string EventId, string Type, string PayloadJson)>();
        using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "select event_id, type, payload_json from events;";
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                events.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        foreach ((string eventId, string legacyType, string payloadJson) in events)
        {
            JsonObject payload = JsonNode.Parse(payloadJson)?.AsObject()
                ?? throw new InvalidOperationException($"Event payload is not a JSON object: {eventId}");
            string migratedType = legacyType;
            bool changed = false;

            if (legacyType is "Gift" or "GiftReturn")
            {
                migratedType = ServerEventType.ItemDelivery.ToString();
                changed = true;
            }

            if (string.Equals(payload["payloadType"]?.GetValue<string>(), "gift", StringComparison.Ordinal))
            {
                payload["payloadType"] = "itemDelivery";
                changed = true;

                if (legacyType == "GiftReturn"
                    && payload[nameof(ItemDeliveryEventPayload.Purpose)]?.GetValue<int>() == (int)ItemDeliveryPurpose.Gift)
                {
                    payload[nameof(ItemDeliveryEventPayload.Purpose)] = (int)ItemDeliveryPurpose.RejectedGiftReturn;
                }
            }

            if (string.Equals(payload["payloadType"]?.GetValue<string>(), "serverNotification", StringComparison.Ordinal)
                && payload[nameof(ServerNotificationEventPayload.RelatedEventType)] is JsonValue relatedTypeValue
                && relatedTypeValue.TryGetValue(out string? relatedTypeText))
            {
                if (TryMapLegacyEventType(relatedTypeText, out ServerEventType relatedType))
                {
                    payload[nameof(ServerNotificationEventPayload.RelatedEventType)] = (int)relatedType;
                }
                else
                {
                    payload.Remove(nameof(ServerNotificationEventPayload.RelatedEventType));
                }

                changed = true;
            }

            if (!changed)
            {
                continue;
            }

            using SqliteCommand update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "update events set type = $type, payload_json = $payload_json where event_id = $event_id;";
            update.Parameters.AddWithValue("$type", migratedType);
            update.Parameters.AddWithValue("$payload_json", payload.ToJsonString());
            update.Parameters.AddWithValue("$event_id", eventId);
            update.ExecuteNonQuery();
        }

        return MigrationApplicationResult.None;
    }

    private static MigrationApplicationResult CreateSnapshotPostUploadJobTable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MigrationContext context)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            create table if not exists server_snapshot_post_upload_jobs (
                job_id text primary key not null,
                processor_id text not null,
                snapshot_kind integer not null,
                user_id text not null,
                colony_id text not null,
                session_id text null,
                snapshot_id text not null,
                occurred_at_utc text not null,
                payload_json text not null,
                attempt_count integer not null,
                next_attempt_at_utc text not null,
                last_error text null
            );

            create index if not exists idx_server_snapshot_post_upload_jobs_ready
                on server_snapshot_post_upload_jobs(next_attempt_at_utc, job_id);
            """;
        command.ExecuteNonQuery();
        return MigrationApplicationResult.None;
    }

    private static MigrationApplicationResult AddSnapshotPostUploadJobState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MigrationContext context)
    {
        if (!TableExists(connection, "server_snapshot_post_upload_jobs", transaction))
        {
            return MigrationApplicationResult.None;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            alter table server_snapshot_post_upload_jobs
                add column job_state integer not null default 1;
            drop index if exists idx_server_snapshot_post_upload_jobs_ready;
            create index idx_server_snapshot_post_upload_jobs_ready
                on server_snapshot_post_upload_jobs(job_state, next_attempt_at_utc, job_id);
            """;
        command.ExecuteNonQuery();
        return MigrationApplicationResult.None;
    }

    private static bool TryMapLegacyEventType(string? value, out ServerEventType eventType)
    {
        if (value is "Gift" or "GiftReturn")
        {
            eventType = ServerEventType.ItemDelivery;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: false, out eventType);
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

    private static SqliteConnection OpenReadOnlyConnection(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        return connection;
    }

    private static bool TryInferUnversionedDatabaseVersion(SqliteConnection connection, out int version)
    {
        bool hasLegacyDocuments = TableExists(connection, "server_documents");
        bool hasStructuredRecords = HasStructuredRegistryData(connection);
        if (hasLegacyDocuments && hasStructuredRecords)
        {
            version = 0;
            return false;
        }

        if (HasLegacyTileGeometry(connection))
        {
            version = ServerDatabaseSchema.LegacyJsonDocumentVersion;
            return true;
        }

        if (hasLegacyDocuments && !hasStructuredRecords)
        {
            version = ServerDatabaseSchema.LegacyJsonDocumentVersion;
            return true;
        }

        if (!hasLegacyDocuments && hasStructuredRecords)
        {
            version = ServerDatabaseSchema.CurrentVersion;
            return true;
        }

        version = 0;
        return false;
    }

    private static bool HasCompleteMigrationPath(int sourceVersion)
    {
        if (sourceVersion == ServerDatabaseSchema.CurrentVersion)
        {
            return true;
        }

        int version = sourceVersion;
        var visited = new HashSet<int>();
        while (version < ServerDatabaseSchema.CurrentVersion && visited.Add(version))
        {
            MigrationStep? step = Steps.SingleOrDefault(candidate => candidate.FromVersion == version);
            if (step is null || step.ToVersion <= version)
            {
                return false;
            }

            version = step.ToVersion;
        }

        return version == ServerDatabaseSchema.CurrentVersion;
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
