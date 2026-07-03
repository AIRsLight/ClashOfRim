using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AIRsLight.ClashOfRim.Network;

internal interface IPlayerRegistryPersistenceStore
{
    bool IsInitialized();

    IReadOnlyList<PlayerSessionRecord> ReadPlayers();

    IReadOnlyList<PlayerColonyTombstoneRecord> ReadTombstones();

    void ReplaceAll(
        IReadOnlyList<PlayerSessionRecord> players,
        IReadOnlyList<PlayerColonyTombstoneRecord> tombstones);
}

internal interface IPawnPackagePersistenceStore
{
    bool IsInitialized();

    IReadOnlyList<StoredPawnPackageRecord> ReadAll();

    IReadOnlyDictionary<string, string> ReadIdempotencyMap();

    void Upsert(StoredPawnPackageRecord record);

    void MapIdempotencyKey(string idempotencyKey, string packageId);
}

internal interface IThingPackagePersistenceStore
{
    bool IsInitialized();

    IReadOnlyList<StoredThingPackageRecord> ReadAll();

    IReadOnlyDictionary<string, string> ReadIdempotencyMap();

    void Upsert(StoredThingPackageRecord record);

    void MapIdempotencyKey(string idempotencyKey, string packageId);
}

internal interface IKeyedJsonRecordStore
{
    bool IsInitialized();

    IReadOnlyDictionary<string, string> ReadAll();

    void ReplaceAll(IReadOnlyDictionary<string, string> records);
}

internal abstract class SqliteStructuredRegistryStore
{
    private const string MarkerTableName = "server_structured_registry_markers";
    private readonly string connectionString;

    protected SqliteStructuredRegistryStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

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
    }

    protected SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    protected static void EnsureMarkerTable(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $$"""
            create table if not exists {{MarkerTableName}} (
                registry_key text primary key not null,
                initialized_at_utc text not null
            );
            """;
        command.ExecuteNonQuery();
    }

    protected bool IsRegistryInitialized(string registryKey)
    {
        using SqliteConnection connection = OpenConnection();
        EnsureMarkerTable(connection);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $$"""
            select 1
            from {{MarkerTableName}}
            where registry_key = $registry_key
            limit 1;
            """;
        command.Parameters.AddWithValue("$registry_key", registryKey);
        return command.ExecuteScalar() is not null;
    }

    protected static void MarkRegistryInitialized(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string registryKey)
    {
        EnsureMarkerTable(connection);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $$"""
            insert into {{MarkerTableName}} (
                registry_key,
                initialized_at_utc
            ) values (
                $registry_key,
                $initialized_at_utc
            )
            on conflict(registry_key) do update set
                initialized_at_utc = excluded.initialized_at_utc;
            """;
        command.Parameters.AddWithValue("$registry_key", registryKey);
        command.Parameters.AddWithValue("$initialized_at_utc", DateString(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    protected static object DbValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
    }

    protected static object DbValue(int? value)
    {
        return value.HasValue ? value.Value : DBNull.Value;
    }

    protected static string DateString(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    protected static DateTimeOffset DateValue(SqliteDataReader reader, string name)
    {
        return DateTimeOffset.Parse(
            reader.GetString(reader.GetOrdinal(name)),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
    }

    protected static string? NullableString(SqliteDataReader reader, string name)
    {
        int ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    protected static int? NullableInt(SqliteDataReader reader, string name)
    {
        int ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }
}

internal sealed class SqliteKeyedJsonRecordStore : SqliteStructuredRegistryStore, IKeyedJsonRecordStore
{
    private const string InitializationMarkerKey = "\u0000initialized";
    private readonly string collectionKey;

    public SqliteKeyedJsonRecordStore(string databasePath, string collectionKey)
        : base(databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionKey);
        this.collectionKey = collectionKey;
        Initialize();
    }

    public bool IsInitialized()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select 1
            from server_keyed_json_records
            where collection_key = $collection_key
                and item_key = $item_key
            limit 1;
            """;
        command.Parameters.AddWithValue("$collection_key", collectionKey);
        command.Parameters.AddWithValue("$item_key", InitializationMarkerKey);
        return command.ExecuteScalar() is not null;
    }

    public IReadOnlyDictionary<string, string> ReadAll()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select item_key, content_json
            from server_keyed_json_records
            where collection_key = $collection_key
                and item_key <> $marker_key
            order by item_key;
            """;
        command.Parameters.AddWithValue("$collection_key", collectionKey);
        command.Parameters.AddWithValue("$marker_key", InitializationMarkerKey);
        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<string, string> records = new(StringComparer.Ordinal);
        while (reader.Read())
        {
            records[reader.GetString(0)] = reader.GetString(1);
        }

        return records;
    }

    public void ReplaceAll(IReadOnlyDictionary<string, string> records)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            delete from server_keyed_json_records
            where collection_key = $collection_key;
            """;
        delete.Parameters.AddWithValue("$collection_key", collectionKey);
        delete.ExecuteNonQuery();

        string updatedAtUtc = DateString(DateTimeOffset.UtcNow);
        using (SqliteCommand marker = connection.CreateCommand())
        {
            marker.Transaction = transaction;
            marker.CommandText = """
                insert into server_keyed_json_records (
                    collection_key,
                    item_key,
                    content_json,
                    updated_at_utc
                ) values (
                    $collection_key,
                    $item_key,
                    $content_json,
                    $updated_at_utc
                );
                """;
            marker.Parameters.AddWithValue("$collection_key", collectionKey);
            marker.Parameters.AddWithValue("$item_key", InitializationMarkerKey);
            marker.Parameters.AddWithValue("$content_json", "{}");
            marker.Parameters.AddWithValue("$updated_at_utc", updatedAtUtc);
            marker.ExecuteNonQuery();
        }

        foreach (KeyValuePair<string, string> pair in records)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                insert into server_keyed_json_records (
                    collection_key,
                    item_key,
                    content_json,
                    updated_at_utc
                ) values (
                    $collection_key,
                    $item_key,
                    $content_json,
                    $updated_at_utc
                );
                """;
            insert.Parameters.AddWithValue("$collection_key", collectionKey);
            insert.Parameters.AddWithValue("$item_key", pair.Key);
            insert.Parameters.AddWithValue("$content_json", pair.Value);
            insert.Parameters.AddWithValue("$updated_at_utc", updatedAtUtc);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void Initialize()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists server_keyed_json_records (
                collection_key text not null,
                item_key text not null,
                content_json text not null,
                updated_at_utc text not null,
                primary key (collection_key, item_key)
            );

            create index if not exists idx_server_keyed_json_records_collection
                on server_keyed_json_records(collection_key);
            """;
        command.ExecuteNonQuery();
    }
}

internal sealed class SqlitePlayerRegistryStore : SqliteStructuredRegistryStore, IPlayerRegistryPersistenceStore
{
    private const string RegistryKey = "players";

    public SqlitePlayerRegistryStore(string databasePath)
        : base(databasePath)
    {
        Initialize();
    }

    public bool IsInitialized()
    {
        return IsRegistryInitialized(RegistryKey);
    }

    public IReadOnlyList<PlayerSessionRecord> ReadPlayers()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select
                user_id,
                colony_id,
                current_snapshot_id,
                last_seen_at_utc,
                display_name,
                latest_snapshot_wealth,
                latest_snapshot_wealth_snapshot_id
            from server_players
            order by user_id;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        List<PlayerSessionRecord> players = new();
        while (reader.Read())
        {
            players.Add(new PlayerSessionRecord(
                reader.GetString(reader.GetOrdinal("user_id")),
                reader.GetString(reader.GetOrdinal("colony_id")),
                NullableString(reader, "current_snapshot_id"),
                DateValue(reader, "last_seen_at_utc"),
                NullableString(reader, "display_name"),
                NullableInt(reader, "latest_snapshot_wealth"),
                NullableString(reader, "latest_snapshot_wealth_snapshot_id")));
        }

        return players;
    }

    public IReadOnlyList<PlayerColonyTombstoneRecord> ReadTombstones()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select
                user_id,
                colony_id,
                last_snapshot_id,
                deleted_at_utc
            from server_player_colony_tombstones
            order by user_id, colony_id;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        List<PlayerColonyTombstoneRecord> tombstones = new();
        while (reader.Read())
        {
            tombstones.Add(new PlayerColonyTombstoneRecord(
                reader.GetString(reader.GetOrdinal("user_id")),
                reader.GetString(reader.GetOrdinal("colony_id")),
                NullableString(reader, "last_snapshot_id"),
                DateValue(reader, "deleted_at_utc")));
        }

        return tombstones;
    }

    public void ReplaceAll(
        IReadOnlyList<PlayerSessionRecord> players,
        IReadOnlyList<PlayerColonyTombstoneRecord> tombstones)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        MarkRegistryInitialized(connection, transaction, RegistryKey);
        using SqliteCommand deletePlayers = connection.CreateCommand();
        deletePlayers.Transaction = transaction;
        deletePlayers.CommandText = "delete from server_players;";
        deletePlayers.ExecuteNonQuery();

        using SqliteCommand deleteTombstones = connection.CreateCommand();
        deleteTombstones.Transaction = transaction;
        deleteTombstones.CommandText = "delete from server_player_colony_tombstones;";
        deleteTombstones.ExecuteNonQuery();

        foreach (PlayerSessionRecord player in players)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                insert into server_players (
                    user_id,
                    colony_id,
                    current_snapshot_id,
                    last_seen_at_utc,
                    display_name,
                    latest_snapshot_wealth,
                    latest_snapshot_wealth_snapshot_id
                ) values (
                    $user_id,
                    $colony_id,
                    $current_snapshot_id,
                    $last_seen_at_utc,
                    $display_name,
                    $latest_snapshot_wealth,
                    $latest_snapshot_wealth_snapshot_id
                );
                """;
            command.Parameters.AddWithValue("$user_id", player.UserId);
            command.Parameters.AddWithValue("$colony_id", player.ColonyId);
            command.Parameters.AddWithValue("$current_snapshot_id", DbValue(player.CurrentSnapshotId));
            command.Parameters.AddWithValue("$last_seen_at_utc", DateString(player.LastSeenAtUtc));
            command.Parameters.AddWithValue("$display_name", DbValue(player.DisplayName));
            command.Parameters.AddWithValue("$latest_snapshot_wealth", DbValue(player.LatestSnapshotWealth));
            command.Parameters.AddWithValue("$latest_snapshot_wealth_snapshot_id", DbValue(player.LatestSnapshotWealthSnapshotId));
            command.ExecuteNonQuery();
        }

        foreach (PlayerColonyTombstoneRecord tombstone in tombstones)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                insert into server_player_colony_tombstones (
                    user_id,
                    colony_id,
                    last_snapshot_id,
                    deleted_at_utc
                ) values (
                    $user_id,
                    $colony_id,
                    $last_snapshot_id,
                    $deleted_at_utc
                );
                """;
            command.Parameters.AddWithValue("$user_id", tombstone.UserId);
            command.Parameters.AddWithValue("$colony_id", tombstone.ColonyId);
            command.Parameters.AddWithValue("$last_snapshot_id", DbValue(tombstone.LastSnapshotId));
            command.Parameters.AddWithValue("$deleted_at_utc", DateString(tombstone.DeletedAtUtc));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void Initialize()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists server_players (
                user_id text primary key not null,
                colony_id text not null,
                current_snapshot_id text null,
                last_seen_at_utc text not null,
                display_name text null,
                latest_snapshot_wealth integer null,
                latest_snapshot_wealth_snapshot_id text null
            );

            create table if not exists server_player_colony_tombstones (
                user_id text not null,
                colony_id text not null,
                last_snapshot_id text null,
                deleted_at_utc text not null,
                primary key (user_id, colony_id)
            );

            create index if not exists idx_server_players_colony_id
                on server_players(colony_id);
            """;
        command.ExecuteNonQuery();
    }
}

internal sealed class SqlitePawnPackageStore : SqliteStructuredRegistryStore, IPawnPackagePersistenceStore
{
    private const string RegistryKey = "pawn-packages";

    public SqlitePawnPackageStore(string databasePath)
        : base(databasePath)
    {
        Initialize();
    }

    public bool IsInitialized()
    {
        return IsRegistryInitialized(RegistryKey);
    }

    public IReadOnlyList<StoredPawnPackageRecord> ReadAll()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select
                package_id,
                idempotency_key,
                owner_user_id,
                owner_colony_id,
                source_snapshot_id,
                created_at_utc,
                pawn_global_id,
                pawn_name,
                thing_def,
                package_json
            from server_pawn_packages
            order by created_at_utc, package_id;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        List<StoredPawnPackageRecord> records = new();
        while (reader.Read())
        {
            records.Add(new StoredPawnPackageRecord(
                reader.GetString(reader.GetOrdinal("package_id")),
                reader.GetString(reader.GetOrdinal("idempotency_key")),
                reader.GetString(reader.GetOrdinal("owner_user_id")),
                NullableString(reader, "owner_colony_id"),
                NullableString(reader, "source_snapshot_id"),
                DateValue(reader, "created_at_utc"),
                reader.GetString(reader.GetOrdinal("pawn_global_id")),
                NullableString(reader, "pawn_name"),
                NullableString(reader, "thing_def"),
                reader.GetString(reader.GetOrdinal("package_json"))));
        }

        return records;
    }

    public IReadOnlyDictionary<string, string> ReadIdempotencyMap()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select idempotency_key, package_id
            from server_pawn_package_idempotency;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    public void Upsert(StoredPawnPackageRecord record)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        MarkRegistryInitialized(connection, transaction, RegistryKey);
        UpsertPackage(connection, transaction, record);
        MapIdempotencyKey(connection, transaction, record.IdempotencyKey, record.PackageId);
        transaction.Commit();
    }

    public void MapIdempotencyKey(string idempotencyKey, string packageId)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        MarkRegistryInitialized(connection, transaction, RegistryKey);
        MapIdempotencyKey(connection, transaction, idempotencyKey, packageId);
        transaction.Commit();
    }

    private static void UpsertPackage(SqliteConnection connection, SqliteTransaction transaction, StoredPawnPackageRecord record)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into server_pawn_packages (
                package_id,
                idempotency_key,
                owner_user_id,
                owner_colony_id,
                source_snapshot_id,
                created_at_utc,
                pawn_global_id,
                pawn_name,
                thing_def,
                package_json
            ) values (
                $package_id,
                $idempotency_key,
                $owner_user_id,
                $owner_colony_id,
                $source_snapshot_id,
                $created_at_utc,
                $pawn_global_id,
                $pawn_name,
                $thing_def,
                $package_json
            )
            on conflict(package_id) do update set
                idempotency_key = excluded.idempotency_key,
                owner_user_id = excluded.owner_user_id,
                owner_colony_id = excluded.owner_colony_id,
                source_snapshot_id = excluded.source_snapshot_id,
                created_at_utc = excluded.created_at_utc,
                pawn_global_id = excluded.pawn_global_id,
                pawn_name = excluded.pawn_name,
                thing_def = excluded.thing_def,
                package_json = excluded.package_json;
            """;
        command.Parameters.AddWithValue("$package_id", record.PackageId);
        command.Parameters.AddWithValue("$idempotency_key", record.IdempotencyKey);
        command.Parameters.AddWithValue("$owner_user_id", record.OwnerUserId);
        command.Parameters.AddWithValue("$owner_colony_id", DbValue(record.OwnerColonyId));
        command.Parameters.AddWithValue("$source_snapshot_id", DbValue(record.SourceSnapshotId));
        command.Parameters.AddWithValue("$created_at_utc", DateString(record.CreatedAtUtc));
        command.Parameters.AddWithValue("$pawn_global_id", record.PawnGlobalId);
        command.Parameters.AddWithValue("$pawn_name", DbValue(record.PawnName));
        command.Parameters.AddWithValue("$thing_def", DbValue(record.ThingDef));
        command.Parameters.AddWithValue("$package_json", record.PackageJson);
        command.ExecuteNonQuery();
    }

    private static void MapIdempotencyKey(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        string packageId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into server_pawn_package_idempotency (
                idempotency_key,
                package_id
            ) values (
                $idempotency_key,
                $package_id
            )
            on conflict(idempotency_key) do update set
                package_id = excluded.package_id;
            """;
        command.Parameters.AddWithValue("$idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("$package_id", packageId);
        command.ExecuteNonQuery();
    }

    private void Initialize()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists server_pawn_packages (
                package_id text primary key not null,
                idempotency_key text not null,
                owner_user_id text not null,
                owner_colony_id text null,
                source_snapshot_id text null,
                created_at_utc text not null,
                pawn_global_id text not null,
                pawn_name text null,
                thing_def text null,
                package_json text not null
            );

            create table if not exists server_pawn_package_idempotency (
                idempotency_key text primary key not null,
                package_id text not null
            );

            create index if not exists idx_server_pawn_packages_owner
                on server_pawn_packages(owner_user_id, owner_colony_id);
            create index if not exists idx_server_pawn_package_idempotency_package
                on server_pawn_package_idempotency(package_id);
            """;
        command.ExecuteNonQuery();
    }
}

internal sealed class SqliteThingPackageStore : SqliteStructuredRegistryStore, IThingPackagePersistenceStore
{
    private const string RegistryKey = "thing-packages";

    public SqliteThingPackageStore(string databasePath)
        : base(databasePath)
    {
        Initialize();
    }

    public bool IsInitialized()
    {
        return IsRegistryInitialized(RegistryKey);
    }

    public IReadOnlyList<StoredThingPackageRecord> ReadAll()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select
                package_id,
                idempotency_key,
                owner_user_id,
                owner_colony_id,
                source_snapshot_id,
                created_at_utc,
                global_key,
                def_name,
                label,
                fingerprint,
                package_json
            from server_thing_packages
            order by created_at_utc, package_id;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        List<StoredThingPackageRecord> records = new();
        while (reader.Read())
        {
            records.Add(new StoredThingPackageRecord(
                reader.GetString(reader.GetOrdinal("package_id")),
                reader.GetString(reader.GetOrdinal("idempotency_key")),
                reader.GetString(reader.GetOrdinal("owner_user_id")),
                NullableString(reader, "owner_colony_id"),
                NullableString(reader, "source_snapshot_id"),
                DateValue(reader, "created_at_utc"),
                reader.GetString(reader.GetOrdinal("global_key")),
                NullableString(reader, "def_name"),
                NullableString(reader, "label"),
                reader.GetString(reader.GetOrdinal("fingerprint")),
                reader.GetString(reader.GetOrdinal("package_json"))));
        }

        return records;
    }

    public IReadOnlyDictionary<string, string> ReadIdempotencyMap()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select idempotency_key, package_id
            from server_thing_package_idempotency;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    public void Upsert(StoredThingPackageRecord record)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        MarkRegistryInitialized(connection, transaction, RegistryKey);
        UpsertPackage(connection, transaction, record);
        MapIdempotencyKey(connection, transaction, record.IdempotencyKey, record.PackageId);
        transaction.Commit();
    }

    public void MapIdempotencyKey(string idempotencyKey, string packageId)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        MarkRegistryInitialized(connection, transaction, RegistryKey);
        MapIdempotencyKey(connection, transaction, idempotencyKey, packageId);
        transaction.Commit();
    }

    private static void UpsertPackage(SqliteConnection connection, SqliteTransaction transaction, StoredThingPackageRecord record)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into server_thing_packages (
                package_id,
                idempotency_key,
                owner_user_id,
                owner_colony_id,
                source_snapshot_id,
                created_at_utc,
                global_key,
                def_name,
                label,
                fingerprint,
                package_json
            ) values (
                $package_id,
                $idempotency_key,
                $owner_user_id,
                $owner_colony_id,
                $source_snapshot_id,
                $created_at_utc,
                $global_key,
                $def_name,
                $label,
                $fingerprint,
                $package_json
            )
            on conflict(package_id) do update set
                idempotency_key = excluded.idempotency_key,
                owner_user_id = excluded.owner_user_id,
                owner_colony_id = excluded.owner_colony_id,
                source_snapshot_id = excluded.source_snapshot_id,
                created_at_utc = excluded.created_at_utc,
                global_key = excluded.global_key,
                def_name = excluded.def_name,
                label = excluded.label,
                fingerprint = excluded.fingerprint,
                package_json = excluded.package_json;
            """;
        command.Parameters.AddWithValue("$package_id", record.PackageId);
        command.Parameters.AddWithValue("$idempotency_key", record.IdempotencyKey);
        command.Parameters.AddWithValue("$owner_user_id", record.OwnerUserId);
        command.Parameters.AddWithValue("$owner_colony_id", DbValue(record.OwnerColonyId));
        command.Parameters.AddWithValue("$source_snapshot_id", DbValue(record.SourceSnapshotId));
        command.Parameters.AddWithValue("$created_at_utc", DateString(record.CreatedAtUtc));
        command.Parameters.AddWithValue("$global_key", record.GlobalKey);
        command.Parameters.AddWithValue("$def_name", DbValue(record.DefName));
        command.Parameters.AddWithValue("$label", DbValue(record.Label));
        command.Parameters.AddWithValue("$fingerprint", record.Fingerprint);
        command.Parameters.AddWithValue("$package_json", record.PackageJson);
        command.ExecuteNonQuery();
    }

    private static void MapIdempotencyKey(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        string packageId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into server_thing_package_idempotency (
                idempotency_key,
                package_id
            ) values (
                $idempotency_key,
                $package_id
            )
            on conflict(idempotency_key) do update set
                package_id = excluded.package_id;
            """;
        command.Parameters.AddWithValue("$idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("$package_id", packageId);
        command.ExecuteNonQuery();
    }

    private void Initialize()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists server_thing_packages (
                package_id text primary key not null,
                idempotency_key text not null,
                owner_user_id text not null,
                owner_colony_id text null,
                source_snapshot_id text null,
                created_at_utc text not null,
                global_key text not null,
                def_name text null,
                label text null,
                fingerprint text not null,
                package_json text not null
            );

            create table if not exists server_thing_package_idempotency (
                idempotency_key text primary key not null,
                package_id text not null
            );

            create index if not exists idx_server_thing_packages_fingerprint
                on server_thing_packages(fingerprint);
            create index if not exists idx_server_thing_packages_owner
                on server_thing_packages(owner_user_id, owner_colony_id);
            create index if not exists idx_server_thing_package_idempotency_package
                on server_thing_package_idempotency(package_id);
            """;
        command.ExecuteNonQuery();
    }
}
