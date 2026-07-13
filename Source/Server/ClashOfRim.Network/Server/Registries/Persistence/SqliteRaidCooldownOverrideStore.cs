using Microsoft.Data.Sqlite;

namespace AIRsLight.ClashOfRim.Network;

internal interface IRaidCooldownOverridePersistenceStore
{
    IReadOnlyList<RaidCooldownOverrideRecord> ReadOverrides();

    IReadOnlyList<RaidCooldownSuppressionRecord> ReadSuppressions();

    void SetCurrent(
        RaidCooldownOverrideRecord record,
        IReadOnlyCollection<RaidCooldownSuppressionRecord> suppressions);
}

internal sealed class SqliteRaidCooldownOverrideStore : SqliteStructuredRegistryStore, IRaidCooldownOverridePersistenceStore
{
    internal SqliteRaidCooldownOverrideStore(string databasePath)
        : base(databasePath)
    {
        using SqliteConnection connection = OpenConnection();
        EnsureTables(connection);
    }

    public IReadOnlyList<RaidCooldownOverrideRecord> ReadOverrides()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select defender_user_id, defender_colony_id, updated_at_utc, cooldown_until_utc
            from server_raid_cooldown_overrides
            order by defender_user_id, defender_colony_id;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var records = new List<RaidCooldownOverrideRecord>();
        while (reader.Read())
        {
            records.Add(new RaidCooldownOverrideRecord(
                reader.GetString(0),
                reader.GetString(1),
                DateValue(reader, "updated_at_utc"),
                DateValue(reader, "cooldown_until_utc")));
        }

        return records;
    }

    public IReadOnlyList<RaidCooldownSuppressionRecord> ReadSuppressions()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select defender_user_id, defender_colony_id, raid_event_id, suppressed_at_utc
            from server_raid_cooldown_suppressions
            order by defender_user_id, defender_colony_id, raid_event_id;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var records = new List<RaidCooldownSuppressionRecord>();
        while (reader.Read())
        {
            records.Add(new RaidCooldownSuppressionRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateValue(reader, "suppressed_at_utc")));
        }

        return records;
    }

    public void SetCurrent(
        RaidCooldownOverrideRecord record,
        IReadOnlyCollection<RaidCooldownSuppressionRecord> suppressions)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(suppressions);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                insert into server_raid_cooldown_overrides (
                    defender_user_id, defender_colony_id, updated_at_utc, cooldown_until_utc
                ) values (
                    $user_id, $colony_id, $updated_at_utc, $cooldown_until_utc
                )
                on conflict(defender_user_id, defender_colony_id) do update set
                    updated_at_utc = excluded.updated_at_utc,
                    cooldown_until_utc = excluded.cooldown_until_utc;
                """;
            command.Parameters.AddWithValue("$user_id", record.DefenderUserId);
            command.Parameters.AddWithValue("$colony_id", record.DefenderColonyId);
            command.Parameters.AddWithValue("$updated_at_utc", DateString(record.UpdatedAtUtc));
            command.Parameters.AddWithValue("$cooldown_until_utc", DateString(record.CooldownUntilUtc));
            command.ExecuteNonQuery();
        }

        foreach (RaidCooldownSuppressionRecord suppression in suppressions)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                insert into server_raid_cooldown_suppressions (
                    defender_user_id, defender_colony_id, raid_event_id, suppressed_at_utc
                ) values (
                    $user_id, $colony_id, $raid_event_id, $suppressed_at_utc
                )
                on conflict(defender_user_id, defender_colony_id, raid_event_id) do update set
                    suppressed_at_utc = excluded.suppressed_at_utc;
                """;
            command.Parameters.AddWithValue("$user_id", suppression.DefenderUserId);
            command.Parameters.AddWithValue("$colony_id", suppression.DefenderColonyId);
            command.Parameters.AddWithValue("$raid_event_id", suppression.RaidEventId);
            command.Parameters.AddWithValue("$suppressed_at_utc", DateString(suppression.SuppressedAtUtc));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    internal static void EnsureTables(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            create table if not exists server_raid_cooldown_overrides (
                defender_user_id text not null,
                defender_colony_id text not null,
                updated_at_utc text not null,
                cooldown_until_utc text not null,
                primary key (defender_user_id, defender_colony_id)
            );

            create table if not exists server_raid_cooldown_suppressions (
                defender_user_id text not null,
                defender_colony_id text not null,
                raid_event_id text not null,
                suppressed_at_utc text not null,
                primary key (defender_user_id, defender_colony_id, raid_event_id)
            );
            create index if not exists ix_server_raid_cooldown_suppressions_event
                on server_raid_cooldown_suppressions(raid_event_id);
            """;
        command.ExecuteNonQuery();
    }
}
