using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AIRsLight.ClashOfRim.Events;

public sealed class SqliteAuthoritativeEventLedger : IAuthoritativeEventLedger
{
    private readonly string connectionString;

    public SqliteAuthoritativeEventLedger(string databasePath)
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
        Initialize();
    }

    public LedgerAppendResult Append(AuthoritativeEvent ledgerEvent)
    {
        ArgumentNullException.ThrowIfNull(ledgerEvent);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        AuthoritativeEvent? existing = FindByIdempotencyKey(connection, transaction, ledgerEvent.IdempotencyKey);
        if (existing is not null)
        {
            transaction.Commit();
            return new LedgerAppendResult(existing, Created: false);
        }

        if (Find(connection, transaction, ledgerEvent.EventId) is not null)
        {
            throw new InvalidOperationException($"Event id already exists: {ledgerEvent.EventId}");
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into events (
                event_id,
                type,
                actor_user_id,
                actor_colony_id,
                actor_faction_id,
                target_user_id,
                target_colony_id,
                target_faction_id,
                created_at_utc,
                status,
                delivery_mode,
                idempotency_key,
                payload_json,
                target_world_object_id,
                target_map_unique_id,
                target_tile,
                target_landing_mode,
                rejection_policy,
                target_decision,
                decision_at_utc,
                decision_reason,
                last_application_result,
                last_failure_reason,
                next_retry_at_utc,
                delivered_to_snapshot_id,
                delivered_at_utc,
                applied_snapshot_id,
                applied_at_utc
            ) values (
                $event_id,
                $type,
                $actor_user_id,
                $actor_colony_id,
                $actor_faction_id,
                $target_user_id,
                $target_colony_id,
                $target_faction_id,
                $created_at_utc,
                $status,
                $delivery_mode,
                $idempotency_key,
                $payload_json,
                $target_world_object_id,
                $target_map_unique_id,
                $target_tile,
                $target_landing_mode,
                $rejection_policy,
                $target_decision,
                $decision_at_utc,
                $decision_reason,
                $last_application_result,
                $last_failure_reason,
                $next_retry_at_utc,
                $delivered_to_snapshot_id,
                $delivered_at_utc,
                $applied_snapshot_id,
                $applied_at_utc
            );
            """;
        AddEventParameters(command, ledgerEvent);
        command.ExecuteNonQuery();
        transaction.Commit();

        return new LedgerAppendResult(ledgerEvent, Created: true);
    }

    public AuthoritativeEvent? Find(string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        using SqliteConnection connection = OpenConnection();
        return Find(connection, transaction: null, eventId);
    }

    public AuthoritativeEvent? FindByIdempotencyKey(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        using SqliteConnection connection = OpenConnection();
        return FindByIdempotencyKey(connection, transaction: null, idempotencyKey);
    }

    public IReadOnlyList<AuthoritativeEvent> ListAll()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select *
            from events
            order by created_at_utc, event_id;
            """;
        return ReadEvents(command);
    }

    public IReadOnlyList<AuthoritativeEvent> ListByType(ServerEventType type)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select *
            from events
            where type = $type
            order by created_at_utc, event_id;
            """;
        command.Parameters.AddWithValue("$type", type.ToString());
        return ReadEvents(command);
    }

    public IReadOnlyList<AuthoritativeEvent> ListByTypeForActor(ServerEventType type, string actorUserId, string? actorColonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = actorColonyId is null
            ? """
                select *
                from events
                where type = $type
                  and actor_user_id = $actor_user_id
                  and actor_colony_id is null
                order by created_at_utc, event_id;
                """
            : """
                select *
                from events
                where type = $type
                  and actor_user_id = $actor_user_id
                  and actor_colony_id = $actor_colony_id
                order by created_at_utc, event_id;
                """;
        command.Parameters.AddWithValue("$type", type.ToString());
        command.Parameters.AddWithValue("$actor_user_id", actorUserId);
        if (actorColonyId is not null)
        {
            command.Parameters.AddWithValue("$actor_colony_id", actorColonyId);
        }
        return ReadEvents(command);
    }

    public IReadOnlyList<AuthoritativeEvent> ListByTypeForTarget(ServerEventType type, string targetUserId, string? targetColonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = targetColonyId is null
            ? """
                select *
                from events
                where type = $type
                  and target_user_id = $target_user_id
                  and target_colony_id is null
                order by created_at_utc, event_id;
                """
            : """
                select *
                from events
                where type = $type
                  and target_user_id = $target_user_id
                  and target_colony_id = $target_colony_id
                order by created_at_utc, event_id;
                """;
        command.Parameters.AddWithValue("$type", type.ToString());
        command.Parameters.AddWithValue("$target_user_id", targetUserId);
        if (targetColonyId is not null)
        {
            command.Parameters.AddWithValue("$target_colony_id", targetColonyId);
        }
        return ReadEvents(command);
    }

    public IReadOnlyList<AuthoritativeEvent> ListForUser(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select *
            from (
                select *
                from events
                where actor_user_id = $user_id

                union

                select *
                from events
                where target_user_id = $user_id
            )
            order by created_at_utc, event_id;
            """;
        command.Parameters.AddWithValue("$user_id", userId);
        return ReadEvents(command);
    }

    public IReadOnlyList<AuthoritativeEvent> ListQueueForTarget(string targetUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select *
            from events
            where target_user_id = $target_user_id
              and status in (
                  $pending_status,
                  $ready_status,
                  $delivered_status,
                  $conflict_status,
                  $failed_status,
                  $rejected_status
              )
            order by created_at_utc, event_id;
            """;
        command.Parameters.AddWithValue("$target_user_id", targetUserId);
        command.Parameters.AddWithValue("$pending_status", ServerEventStatus.PendingOfflineDelivery.ToString());
        command.Parameters.AddWithValue("$ready_status", ServerEventStatus.ReadyForImmediateDelivery.ToString());
        command.Parameters.AddWithValue("$delivered_status", ServerEventStatus.DeliveredToClient.ToString());
        command.Parameters.AddWithValue("$conflict_status", ServerEventStatus.Conflict.ToString());
        command.Parameters.AddWithValue("$failed_status", ServerEventStatus.Failed.ToString());
        command.Parameters.AddWithValue("$rejected_status", ServerEventStatus.RejectedByTarget.ToString());
        return ReadEvents(command);
    }

    public int DeleteForColony(string userId, string colonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            delete from events
            where (actor_user_id = $user_id and actor_colony_id = $colony_id)
               or (target_user_id = $user_id and target_colony_id = $colony_id);
            """;
        command.Parameters.AddWithValue("$user_id", userId);
        command.Parameters.AddWithValue("$colony_id", colonyId);
        return command.ExecuteNonQuery();
    }

    public IReadOnlyList<AuthoritativeEvent> ListPendingForTarget(string targetUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select *
            from events
            where target_user_id = $target_user_id
              and status = $status
            order by created_at_utc, event_id;
            """;
        command.Parameters.AddWithValue("$target_user_id", targetUserId);
        command.Parameters.AddWithValue("$status", ServerEventStatus.PendingOfflineDelivery.ToString());
        return ReadEvents(command);
    }

    public IReadOnlyList<AuthoritativeEvent> ListDeliverableForTarget(string targetUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select *
            from events
            where target_user_id = $target_user_id
              and status in ($pending_status, $delivered_status)
            order by created_at_utc, event_id;
            """;
        command.Parameters.AddWithValue("$target_user_id", targetUserId);
        command.Parameters.AddWithValue("$pending_status", ServerEventStatus.PendingOfflineDelivery.ToString());
        command.Parameters.AddWithValue("$delivered_status", ServerEventStatus.DeliveredToClient.ToString());
        return ReadEvents(command);
    }

    public AuthoritativeEvent ChangeStatus(string eventId, ServerEventStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        AuthoritativeEvent ledgerEvent = Find(connection, transaction, eventId)
            ?? throw new KeyNotFoundException($"Event id not found: {eventId}");
        AuthoritativeEvent changed = ledgerEvent with { Status = status };

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            update events
            set status = $status
            where event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$event_id", eventId);
        command.ExecuteNonQuery();
        transaction.Commit();

        return changed;
    }

    public AuthoritativeEvent MarkDelivered(string eventId, string deliveredToSnapshotId, DateTimeOffset deliveredAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveredToSnapshotId);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        AuthoritativeEvent ledgerEvent = Find(connection, transaction, eventId)
            ?? throw new KeyNotFoundException($"Event id not found: {eventId}");
        AuthoritativeEvent changed = ledgerEvent with
        {
            Status = ServerEventStatus.DeliveredToClient,
            DeliveredToSnapshotId = deliveredToSnapshotId,
            DeliveredAtUtc = deliveredAtUtc
        };

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            update events
            set status = $status,
                delivered_to_snapshot_id = $delivered_to_snapshot_id,
                delivered_at_utc = $delivered_at_utc
            where event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$status", changed.Status.ToString());
        command.Parameters.AddWithValue("$delivered_to_snapshot_id", deliveredToSnapshotId);
        command.Parameters.AddWithValue("$delivered_at_utc", deliveredAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$event_id", eventId);
        command.ExecuteNonQuery();
        transaction.Commit();

        return changed;
    }

    public AuthoritativeEvent MarkApplied(string eventId, string appliedSnapshotId, DateTimeOffset appliedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appliedSnapshotId);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        AuthoritativeEvent ledgerEvent = Find(connection, transaction, eventId)
            ?? throw new KeyNotFoundException($"Event id not found: {eventId}");
        if (string.IsNullOrWhiteSpace(ledgerEvent.DeliveredToSnapshotId))
        {
            throw new InvalidOperationException("Event must be delivered to a snapshot before it can be applied.");
        }

        AuthoritativeEvent changed = ledgerEvent with
        {
            Status = ServerEventStatus.AppliedToSnapshot,
            AppliedSnapshotId = appliedSnapshotId,
            AppliedAtUtc = appliedAtUtc
        };

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            update events
            set status = $status,
                applied_snapshot_id = $applied_snapshot_id,
                applied_at_utc = $applied_at_utc
            where event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$status", changed.Status.ToString());
        command.Parameters.AddWithValue("$applied_snapshot_id", appliedSnapshotId);
        command.Parameters.AddWithValue("$applied_at_utc", appliedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$event_id", eventId);
        command.ExecuteNonQuery();
        transaction.Commit();

        return changed;
    }

    public AuthoritativeEvent MarkAccepted(string eventId, DateTimeOffset acceptedAtUtc, string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        AuthoritativeEvent ledgerEvent = Find(connection, transaction, eventId)
            ?? throw new KeyNotFoundException($"Event id not found: {eventId}");
        AuthoritativeEvent changed = ledgerEvent with
        {
            Status = ServerEventStatus.AppliedToSnapshot,
            TargetDecision = TargetEventDecision.Accepted,
            DecisionAtUtc = acceptedAtUtc,
            DecisionReason = reason,
            LastApplicationResult = EventApplicationResultKind.Applied,
            AppliedAtUtc = acceptedAtUtc
        };

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            update events
            set status = $status,
                target_decision = $target_decision,
                decision_at_utc = $decision_at_utc,
                decision_reason = $decision_reason,
                last_application_result = $last_application_result,
                applied_at_utc = $applied_at_utc
            where event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$status", changed.Status.ToString());
        command.Parameters.AddWithValue("$target_decision", changed.TargetDecision.ToString());
        command.Parameters.AddWithValue("$decision_at_utc", acceptedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$decision_reason", ValueOrDbNull(reason));
        command.Parameters.AddWithValue("$last_application_result", changed.LastApplicationResult.ToString());
        command.Parameters.AddWithValue("$applied_at_utc", acceptedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$event_id", eventId);
        command.ExecuteNonQuery();
        transaction.Commit();

        return changed;
    }

    public AuthoritativeEvent MarkRejected(string eventId, DateTimeOffset rejectedAtUtc, string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        AuthoritativeEvent ledgerEvent = Find(connection, transaction, eventId)
            ?? throw new KeyNotFoundException($"Event id not found: {eventId}");
        if (ledgerEvent.RejectionPolicy != EventRejectionPolicy.RejectableByTarget)
        {
            throw new InvalidOperationException("Event is not rejectable by target.");
        }

        AuthoritativeEvent changed = ledgerEvent with
        {
            Status = ServerEventStatus.RejectedByTarget,
            TargetDecision = TargetEventDecision.Rejected,
            DecisionAtUtc = rejectedAtUtc,
            DecisionReason = reason,
            LastApplicationResult = EventApplicationResultKind.Rejected
        };

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            update events
            set status = $status,
                target_decision = $target_decision,
                decision_at_utc = $decision_at_utc,
                decision_reason = $decision_reason,
                last_application_result = $last_application_result
            where event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$status", changed.Status.ToString());
        command.Parameters.AddWithValue("$target_decision", changed.TargetDecision.ToString());
        command.Parameters.AddWithValue("$decision_at_utc", rejectedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$decision_reason", ValueOrDbNull(reason));
        command.Parameters.AddWithValue("$last_application_result", changed.LastApplicationResult.ToString());
        command.Parameters.AddWithValue("$event_id", eventId);
        command.ExecuteNonQuery();
        transaction.Commit();

        return changed;
    }

    public GiftReturnResult RejectGiftAndCreateReturn(
        string eventId,
        DateTimeOffset rejectedAtUtc,
        string? reason,
        bool originalActorOnline)
    {
        AuthoritativeEvent rejectedGift = MarkRejected(eventId, rejectedAtUtc, reason);
        AuthoritativeEvent returnEvent = GiftReturnEventFactory.CreateReturnEvent(
            rejectedGift,
            rejectedAtUtc,
            originalActorOnline);
        LedgerAppendResult appendResult = Append(returnEvent);
        return new GiftReturnResult(rejectedGift, appendResult.Event, appendResult.Created);
    }

    public AuthoritativeEvent ReportApplicationResult(
        string eventId,
        EventApplicationResultKind result,
        string? failureReason,
        DateTimeOffset? nextRetryAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        AuthoritativeEvent ledgerEvent = Find(connection, transaction, eventId)
            ?? throw new KeyNotFoundException($"Event id not found: {eventId}");
        AuthoritativeEvent changed = ledgerEvent with
        {
            Status = result is EventApplicationResultKind.SnapshotBaseMismatch
                or EventApplicationResultKind.TargetObjectMissing
                or EventApplicationResultKind.LossNotReflected
                or EventApplicationResultKind.NeedsManualReview
                ? ServerEventStatus.Conflict
                : ledgerEvent.Status,
            LastApplicationResult = result,
            LastFailureReason = failureReason,
            NextRetryAtUtc = nextRetryAtUtc
        };

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            update events
            set status = $status,
                last_application_result = $last_application_result,
                last_failure_reason = $last_failure_reason,
                next_retry_at_utc = $next_retry_at_utc
            where event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$status", changed.Status.ToString());
        command.Parameters.AddWithValue("$last_application_result", changed.LastApplicationResult.ToString());
        command.Parameters.AddWithValue("$last_failure_reason", ValueOrDbNull(failureReason));
        command.Parameters.AddWithValue("$next_retry_at_utc", ValueOrDbNull(FormatDateTimeOffset(nextRetryAtUtc)));
        command.Parameters.AddWithValue("$event_id", eventId);
        command.ExecuteNonQuery();
        transaction.Commit();

        return changed;
    }

    private void Initialize()
    {
        using SqliteConnection connection = OpenConnection();

        using SqliteCommand pragmas = connection.CreateCommand();
        pragmas.CommandText = """
            pragma journal_mode = wal;
            pragma foreign_keys = on;
            """;
        pragmas.ExecuteNonQuery();

        using SqliteCommand schema = connection.CreateCommand();
        schema.CommandText = """
            create table if not exists events (
                event_id text primary key,
                type text not null,
                actor_user_id text not null,
                actor_colony_id text null,
                actor_faction_id text null,
                target_user_id text not null,
                target_colony_id text null,
                target_faction_id text null,
                created_at_utc text not null,
                status text not null,
                delivery_mode text not null,
                idempotency_key text not null unique,
                payload_json text not null,
                target_world_object_id text null,
                target_map_unique_id text null,
                target_tile integer null,
                target_landing_mode text not null default 'Unspecified',
                rejection_policy text not null default 'NotRejectable',
                target_decision text not null default 'None',
                decision_at_utc text null,
                decision_reason text null,
                last_application_result text not null default 'None',
                last_failure_reason text null,
                next_retry_at_utc text null,
                delivered_to_snapshot_id text null,
                delivered_at_utc text null,
                applied_snapshot_id text null,
                applied_at_utc text null
            );

            create index if not exists idx_events_target_status_created
                on events(target_user_id, status, created_at_utc, event_id);

            create index if not exists idx_events_actor_created
                on events(actor_user_id, created_at_utc, event_id);

            create index if not exists idx_events_target_created
                on events(target_user_id, created_at_utc, event_id);

            create index if not exists idx_events_type_created
                on events(type, created_at_utc, event_id);

            create index if not exists idx_events_type_actor_colony_created
                on events(type, actor_user_id, actor_colony_id, created_at_utc, event_id);

            create index if not exists idx_events_type_target_colony_created
                on events(type, target_user_id, target_colony_id, created_at_utc, event_id);
            """;
        schema.ExecuteNonQuery();

        EnsureColumn(connection, "target_world_object_id", "text null");
        EnsureColumn(connection, "target_map_unique_id", "text null");
        EnsureColumn(connection, "target_tile", "integer null");
        EnsureColumn(connection, "target_landing_mode", "text not null default 'Unspecified'");
        EnsureColumn(connection, "rejection_policy", "text not null default 'NotRejectable'");
        EnsureColumn(connection, "target_decision", "text not null default 'None'");
        EnsureColumn(connection, "decision_at_utc", "text null");
        EnsureColumn(connection, "decision_reason", "text null");
        EnsureColumn(connection, "last_application_result", "text not null default 'None'");
        EnsureColumn(connection, "last_failure_reason", "text null");
        EnsureColumn(connection, "next_retry_at_utc", "text null");
        EnsureColumn(connection, "delivered_to_snapshot_id", "text null");
        EnsureColumn(connection, "delivered_at_utc", "text null");
        EnsureColumn(connection, "applied_snapshot_id", "text null");
        EnsureColumn(connection, "applied_at_utc", "text null");
    }

    private static void EnsureColumn(SqliteConnection connection, string name, string declaration)
    {
        using SqliteCommand tableInfo = connection.CreateCommand();
        tableInfo.CommandText = "pragma table_info(events);";
        using SqliteDataReader reader = tableInfo.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(reader.GetOrdinal("name")), name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using SqliteCommand alter = connection.CreateCommand();
        alter.CommandText = $"alter table events add column {name} {declaration};";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static AuthoritativeEvent? Find(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string eventId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            select *
            from events
            where event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        return ReadEvents(command).SingleOrDefault();
    }

    private static AuthoritativeEvent? FindByIdempotencyKey(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string idempotencyKey)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            select *
            from events
            where idempotency_key = $idempotency_key;
            """;
        command.Parameters.AddWithValue("$idempotency_key", idempotencyKey);
        return ReadEvents(command).SingleOrDefault();
    }

    private static IReadOnlyList<AuthoritativeEvent> ReadEvents(SqliteCommand command)
    {
        var events = new List<AuthoritativeEvent>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }

    private static AuthoritativeEvent ReadEvent(SqliteDataReader reader)
    {
        string payloadJson = reader.GetString(reader.GetOrdinal("payload_json"));
        LedgerEventPayload payload = JsonSerializer.Deserialize<LedgerEventPayload>(payloadJson, LedgerJson.Options)
            ?? throw new InvalidOperationException("Event payload cannot be deserialized.");

        return new AuthoritativeEvent(
            reader.GetString(reader.GetOrdinal("event_id")),
            Enum.Parse<ServerEventType>(reader.GetString(reader.GetOrdinal("type"))),
            new EventParty(
                reader.GetString(reader.GetOrdinal("actor_user_id")),
                GetNullableString(reader, "actor_colony_id"),
                GetNullableString(reader, "actor_faction_id")),
            new EventParty(
                reader.GetString(reader.GetOrdinal("target_user_id")),
                GetNullableString(reader, "target_colony_id"),
                GetNullableString(reader, "target_faction_id")),
            DateTimeOffset.Parse(
                reader.GetString(reader.GetOrdinal("created_at_utc")),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            Enum.Parse<ServerEventStatus>(reader.GetString(reader.GetOrdinal("status"))),
            Enum.Parse<ServerEventDeliveryMode>(reader.GetString(reader.GetOrdinal("delivery_mode"))),
            reader.GetString(reader.GetOrdinal("idempotency_key")),
            payload,
            ReadTargetContext(reader),
            Enum.Parse<EventRejectionPolicy>(reader.GetString(reader.GetOrdinal("rejection_policy"))),
            Enum.Parse<TargetEventDecision>(reader.GetString(reader.GetOrdinal("target_decision"))),
            GetNullableDateTimeOffset(reader, "decision_at_utc"),
            GetNullableString(reader, "decision_reason"),
            Enum.Parse<EventApplicationResultKind>(reader.GetString(reader.GetOrdinal("last_application_result"))),
            GetNullableString(reader, "last_failure_reason"),
            GetNullableDateTimeOffset(reader, "next_retry_at_utc"),
            GetNullableString(reader, "delivered_to_snapshot_id"),
            GetNullableDateTimeOffset(reader, "delivered_at_utc"),
            GetNullableString(reader, "applied_snapshot_id"),
            GetNullableDateTimeOffset(reader, "applied_at_utc"));
    }

    private static void AddEventParameters(SqliteCommand command, AuthoritativeEvent ledgerEvent)
    {
        command.Parameters.AddWithValue("$event_id", ledgerEvent.EventId);
        command.Parameters.AddWithValue("$type", ledgerEvent.Type.ToString());
        command.Parameters.AddWithValue("$actor_user_id", ledgerEvent.Actor.UserId);
        command.Parameters.AddWithValue("$actor_colony_id", ValueOrDbNull(ledgerEvent.Actor.ColonyId));
        command.Parameters.AddWithValue("$actor_faction_id", ValueOrDbNull(ledgerEvent.Actor.FactionId));
        command.Parameters.AddWithValue("$target_user_id", ledgerEvent.Target.UserId);
        command.Parameters.AddWithValue("$target_colony_id", ValueOrDbNull(ledgerEvent.Target.ColonyId));
        command.Parameters.AddWithValue("$target_faction_id", ValueOrDbNull(ledgerEvent.Target.FactionId));
        command.Parameters.AddWithValue("$created_at_utc", ledgerEvent.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$status", ledgerEvent.Status.ToString());
        command.Parameters.AddWithValue("$delivery_mode", ledgerEvent.DeliveryMode.ToString());
        command.Parameters.AddWithValue("$idempotency_key", ledgerEvent.IdempotencyKey);
        command.Parameters.AddWithValue(
            "$payload_json",
            JsonSerializer.Serialize<LedgerEventPayload>(ledgerEvent.Payload, LedgerJson.Options));
        command.Parameters.AddWithValue("$target_world_object_id", ValueOrDbNull(ledgerEvent.TargetContext?.WorldObjectId));
        command.Parameters.AddWithValue("$target_map_unique_id", ValueOrDbNull(ledgerEvent.TargetContext?.MapUniqueId));
        command.Parameters.AddWithValue("$target_tile", ledgerEvent.TargetContext?.Tile is int tile ? tile : DBNull.Value);
        command.Parameters.AddWithValue("$target_landing_mode", (ledgerEvent.TargetContext?.LandingMode ?? EventLandingMode.Unspecified).ToString());
        command.Parameters.AddWithValue("$rejection_policy", ledgerEvent.RejectionPolicy.ToString());
        command.Parameters.AddWithValue("$target_decision", ledgerEvent.TargetDecision.ToString());
        command.Parameters.AddWithValue("$decision_at_utc", ValueOrDbNull(FormatDateTimeOffset(ledgerEvent.DecisionAtUtc)));
        command.Parameters.AddWithValue("$decision_reason", ValueOrDbNull(ledgerEvent.DecisionReason));
        command.Parameters.AddWithValue("$last_application_result", ledgerEvent.LastApplicationResult.ToString());
        command.Parameters.AddWithValue("$last_failure_reason", ValueOrDbNull(ledgerEvent.LastFailureReason));
        command.Parameters.AddWithValue("$next_retry_at_utc", ValueOrDbNull(FormatDateTimeOffset(ledgerEvent.NextRetryAtUtc)));
        command.Parameters.AddWithValue("$delivered_to_snapshot_id", ValueOrDbNull(ledgerEvent.DeliveredToSnapshotId));
        command.Parameters.AddWithValue("$delivered_at_utc", ValueOrDbNull(FormatDateTimeOffset(ledgerEvent.DeliveredAtUtc)));
        command.Parameters.AddWithValue("$applied_snapshot_id", ValueOrDbNull(ledgerEvent.AppliedSnapshotId));
        command.Parameters.AddWithValue("$applied_at_utc", ValueOrDbNull(FormatDateTimeOffset(ledgerEvent.AppliedAtUtc)));
    }

    private static string? GetNullableString(SqliteDataReader reader, string name)
    {
        int ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static EventTargetContext? ReadTargetContext(SqliteDataReader reader)
    {
        string? worldObjectId = GetNullableString(reader, "target_world_object_id");
        string? mapUniqueId = GetNullableString(reader, "target_map_unique_id");
        int? tile = GetNullableInt(reader, "target_tile");
        var landingMode = Enum.Parse<EventLandingMode>(reader.GetString(reader.GetOrdinal("target_landing_mode")));

        if (string.IsNullOrWhiteSpace(worldObjectId)
            && string.IsNullOrWhiteSpace(mapUniqueId)
            && !tile.HasValue
            && landingMode == EventLandingMode.Unspecified)
        {
            return null;
        }

        return new EventTargetContext(worldObjectId, mapUniqueId, tile, landingMode);
    }

    private static int? GetNullableInt(SqliteDataReader reader, string name)
    {
        int ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static object ValueOrDbNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
    }

    private static DateTimeOffset? GetNullableDateTimeOffset(SqliteDataReader reader, string name)
    {
        string? value = GetNullableString(reader, name);
        return string.IsNullOrWhiteSpace(value)
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static string? FormatDateTimeOffset(DateTimeOffset? value)
    {
        return value?.ToString("O", CultureInfo.InvariantCulture);
    }
}
