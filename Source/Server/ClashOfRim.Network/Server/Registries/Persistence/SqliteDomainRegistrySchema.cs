using Microsoft.Data.Sqlite;

namespace AIRsLight.ClashOfRim.Network;

internal static class SqliteDomainRegistrySchema
{
    private const string LegacyInitializationMarkerKey = "\u0000initialized";
    internal const string OfflineAccounts = "offline-accounts";
    internal const string BankLoans = "bank-loans";
    internal const string MercenaryContracts = "mercenary-contracts";
    internal const string MercenaryGuards = "mercenary-guards";
    internal const string ServerShop = "server-shop";
    internal const string DiplomacyRelations = "diplomacy-relations";
    internal const string RaidProtectionActivations = "raid-protection-activations";
    internal const string ChatMessages = "chat-messages";
    internal const string Achievements = "achievements";

    private static readonly IReadOnlyDictionary<string, DomainRegistryDefinition> Definitions =
        new Dictionary<string, DomainRegistryDefinition>(StringComparer.Ordinal)
        {
            [OfflineAccounts] = new(OfflineAccounts, [new("account:", "server_offline_accounts")]),
            [BankLoans] = new(BankLoans,
            [
                new("loan:", "server_bank_loans"),
                new("debt:", "server_bank_debts")
            ]),
            [MercenaryContracts] = new(MercenaryContracts, [new("contract:", "server_mercenary_contracts")]),
            [MercenaryGuards] = new(MercenaryGuards, [new("contract:", "server_mercenary_guard_contracts")]),
            [ServerShop] = new(ServerShop,
            [
                new("listing:", "server_shop_listings"),
                new("buyer:", "server_shop_buyer_purchases"),
                new("completed:", "server_shop_completed_purchases")
            ]),
            [DiplomacyRelations] = new(DiplomacyRelations, [new(string.Empty, "server_diplomacy_relations")]),
            [RaidProtectionActivations] = new(RaidProtectionActivations, [new(string.Empty, "server_raid_protection_activations")]),
            [ChatMessages] = new(ChatMessages, [new("message:", "server_chat_messages")]),
            [Achievements] = new(Achievements,
            [
                new("event:", "server_achievement_events"),
                new("aggregate:", "server_achievement_aggregates"),
                new("metric-event:", "server_achievement_metric_events"),
                new("metric-aggregate:", "server_achievement_metric_aggregates")
            ])
        };

    internal static DomainRegistryDefinition GetDefinition(string registryKey)
    {
        if (!Definitions.TryGetValue(registryKey, out DomainRegistryDefinition? definition))
        {
            throw new ArgumentOutOfRangeException(nameof(registryKey), registryKey, "No dedicated registry table mapping is defined.");
        }

        return definition;
    }

    internal static void EnsureTables(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            create table if not exists server_registry_quarantine (
                collection_key text not null,
                item_key text not null,
                content_json text not null,
                error_text text not null,
                quarantined_at_utc text not null,
                primary key (collection_key, item_key)
            );

            create table if not exists server_offline_accounts (
                item_key text primary key not null,
                content_json text not null check (json_valid(content_json)),
                updated_at_utc text not null,
                user_id text generated always as (coalesce(json_extract(content_json, '$.UserId'), json_extract(content_json, '$.userId'))) stored,
                display_name text generated always as (coalesce(json_extract(content_json, '$.DisplayName'), json_extract(content_json, '$.displayName'))) stored,
                password_salt text generated always as (coalesce(json_extract(content_json, '$.PasswordSalt'), json_extract(content_json, '$.passwordSalt'))) stored,
                password_hash text generated always as (coalesce(json_extract(content_json, '$.PasswordHash'), json_extract(content_json, '$.passwordHash'))) stored,
                password_iterations integer generated always as (coalesce(json_extract(content_json, '$.Iterations'), json_extract(content_json, '$.iterations'))) stored,
                created_at_utc text generated always as (coalesce(json_extract(content_json, '$.CreatedAtUtc'), json_extract(content_json, '$.createdAtUtc'))) stored,
                account_updated_at_utc text generated always as (coalesce(json_extract(content_json, '$.UpdatedAtUtc'), json_extract(content_json, '$.updatedAtUtc'))) stored
            );
            create unique index if not exists ux_server_offline_accounts_user on server_offline_accounts(user_id);

            create table if not exists server_bank_loans (
                item_key text primary key not null,
                content_json text not null check (json_valid(content_json)),
                updated_at_utc text not null,
                loan_id text generated always as (coalesce(json_extract(content_json, '$.LoanId'), json_extract(content_json, '$.loanId'))) stored,
                idempotency_key text generated always as (coalesce(json_extract(content_json, '$.IdempotencyKey'), json_extract(content_json, '$.idempotencyKey'))) stored,
                user_id text generated always as (coalesce(json_extract(content_json, '$.UserId'), json_extract(content_json, '$.userId'))) stored,
                colony_id text generated always as (coalesce(json_extract(content_json, '$.ColonyId'), json_extract(content_json, '$.colonyId'))) stored,
                snapshot_id text generated always as (coalesce(json_extract(content_json, '$.SnapshotId'), json_extract(content_json, '$.snapshotId'))) stored,
                principal_silver integer generated always as (coalesce(json_extract(content_json, '$.PrincipalSilver'), json_extract(content_json, '$.principalSilver'))) stored,
                total_due_silver integer generated always as (coalesce(json_extract(content_json, '$.TotalDueSilver'), json_extract(content_json, '$.totalDueSilver'))) stored,
                due_at_game_ticks integer generated always as (coalesce(json_extract(content_json, '$.DueAtGameTicks'), json_extract(content_json, '$.dueAtGameTicks'))) stored,
                status text generated always as (coalesce(json_extract(content_json, '$.Status'), json_extract(content_json, '$.status'))) stored,
                created_at_utc text generated always as (coalesce(json_extract(content_json, '$.CreatedAtUtc'), json_extract(content_json, '$.createdAtUtc'))) stored,
                repayment_idempotency_key text generated always as (coalesce(json_extract(content_json, '$.RepaymentIdempotencyKey'), json_extract(content_json, '$.repaymentIdempotencyKey'))) stored
            );
            create unique index if not exists ux_server_bank_loans_id on server_bank_loans(loan_id);
            create unique index if not exists ux_server_bank_loans_idempotency on server_bank_loans(idempotency_key);
            create index if not exists ix_server_bank_loans_colony_status on server_bank_loans(user_id, colony_id, status);

            create table if not exists server_bank_debts (
                item_key text primary key not null,
                content_json text not null check (json_valid(content_json)),
                updated_at_utc text not null,
                debt_id text generated always as (coalesce(json_extract(content_json, '$.DebtId'), json_extract(content_json, '$.debtId'))) stored,
                idempotency_key text generated always as (coalesce(json_extract(content_json, '$.IdempotencyKey'), json_extract(content_json, '$.idempotencyKey'))) stored,
                user_id text generated always as (coalesce(json_extract(content_json, '$.UserId'), json_extract(content_json, '$.userId'))) stored,
                colony_id text generated always as (coalesce(json_extract(content_json, '$.ColonyId'), json_extract(content_json, '$.colonyId'))) stored,
                snapshot_id text generated always as (coalesce(json_extract(content_json, '$.SnapshotId'), json_extract(content_json, '$.snapshotId'))) stored,
                amount_silver integer generated always as (coalesce(json_extract(content_json, '$.AmountSilver'), json_extract(content_json, '$.amountSilver'))) stored,
                source_kind text generated always as (coalesce(json_extract(content_json, '$.SourceKind'), json_extract(content_json, '$.sourceKind'))) stored,
                source_id text generated always as (coalesce(json_extract(content_json, '$.SourceId'), json_extract(content_json, '$.sourceId'))) stored,
                status text generated always as (coalesce(json_extract(content_json, '$.Status'), json_extract(content_json, '$.status'))) stored,
                created_at_utc text generated always as (coalesce(json_extract(content_json, '$.CreatedAtUtc'), json_extract(content_json, '$.createdAtUtc'))) stored,
                repayment_idempotency_key text generated always as (coalesce(json_extract(content_json, '$.RepaymentIdempotencyKey'), json_extract(content_json, '$.repaymentIdempotencyKey'))) stored
            );
            create unique index if not exists ux_server_bank_debts_id on server_bank_debts(debt_id);
            create unique index if not exists ux_server_bank_debts_idempotency on server_bank_debts(idempotency_key);
            create index if not exists ix_server_bank_debts_colony_status on server_bank_debts(user_id, colony_id, status);

            create table if not exists server_mercenary_contracts (
                item_key text primary key not null,
                content_json text not null check (json_valid(content_json)),
                updated_at_utc text not null,
                contract_id text generated always as (coalesce(json_extract(content_json, '$.ContractId'), json_extract(content_json, '$.contractId'))) stored,
                idempotency_key text generated always as (coalesce(json_extract(content_json, '$.IdempotencyKey'), json_extract(content_json, '$.idempotencyKey'))) stored,
                user_id text generated always as (coalesce(json_extract(content_json, '$.UserId'), json_extract(content_json, '$.userId'))) stored,
                colony_id text generated always as (coalesce(json_extract(content_json, '$.ColonyId'), json_extract(content_json, '$.colonyId'))) stored,
                snapshot_id text generated always as (coalesce(json_extract(content_json, '$.SnapshotId'), json_extract(content_json, '$.snapshotId'))) stored,
                skill_def_name text generated always as (coalesce(json_extract(content_json, '$.SkillDefName'), json_extract(content_json, '$.skillDefName'))) stored,
                skill_level integer generated always as (coalesce(json_extract(content_json, '$.SkillLevel'), json_extract(content_json, '$.skillLevel'))) stored,
                price_silver integer generated always as (coalesce(json_extract(content_json, '$.PriceSilver'), json_extract(content_json, '$.priceSilver'))) stored,
                expires_at_game_ticks integer generated always as (coalesce(json_extract(content_json, '$.ExpiresAtGameTicks'), json_extract(content_json, '$.expiresAtGameTicks'))) stored,
                status text generated always as (coalesce(json_extract(content_json, '$.Status'), json_extract(content_json, '$.status'))) stored,
                created_at_utc text generated always as (coalesce(json_extract(content_json, '$.CreatedAtUtc'), json_extract(content_json, '$.createdAtUtc'))) stored
            );
            create unique index if not exists ux_server_mercenary_contracts_id on server_mercenary_contracts(contract_id);
            create unique index if not exists ux_server_mercenary_contracts_idempotency on server_mercenary_contracts(idempotency_key);
            create index if not exists ix_server_mercenary_contracts_colony_status on server_mercenary_contracts(user_id, colony_id, status);

            create table if not exists server_mercenary_guard_contracts (
                item_key text primary key not null,
                content_json text not null check (json_valid(content_json)),
                updated_at_utc text not null,
                contract_id text generated always as (coalesce(json_extract(content_json, '$.ContractId'), json_extract(content_json, '$.contractId'))) stored,
                idempotency_key text generated always as (coalesce(json_extract(content_json, '$.IdempotencyKey'), json_extract(content_json, '$.idempotencyKey'))) stored,
                user_id text generated always as (coalesce(json_extract(content_json, '$.UserId'), json_extract(content_json, '$.userId'))) stored,
                colony_id text generated always as (coalesce(json_extract(content_json, '$.ColonyId'), json_extract(content_json, '$.colonyId'))) stored,
                snapshot_id text generated always as (coalesce(json_extract(content_json, '$.SnapshotId'), json_extract(content_json, '$.snapshotId'))) stored,
                tier text generated always as (coalesce(json_extract(content_json, '$.Tier'), json_extract(content_json, '$.tier'))) stored,
                price_silver integer generated always as (coalesce(json_extract(content_json, '$.PriceSilver'), json_extract(content_json, '$.priceSilver'))) stored,
                point_ratio real generated always as (coalesce(json_extract(content_json, '$.PointRatio'), json_extract(content_json, '$.pointRatio'))) stored,
                status text generated always as (coalesce(json_extract(content_json, '$.Status'), json_extract(content_json, '$.status'))) stored,
                consumed_raid_event_id text generated always as (coalesce(json_extract(content_json, '$.ConsumedRaidEventId'), json_extract(content_json, '$.consumedRaidEventId'))) stored,
                created_at_utc text generated always as (coalesce(json_extract(content_json, '$.CreatedAtUtc'), json_extract(content_json, '$.createdAtUtc'))) stored
            );
            create unique index if not exists ux_server_mercenary_guards_id on server_mercenary_guard_contracts(contract_id);
            create unique index if not exists ux_server_mercenary_guards_idempotency on server_mercenary_guard_contracts(idempotency_key);
            create index if not exists ix_server_mercenary_guards_colony_status on server_mercenary_guard_contracts(user_id, colony_id, status);

            create table if not exists server_shop_listings (
                item_key text primary key not null,
                content_json text not null check (json_valid(content_json)),
                updated_at_utc text not null,
                listing_id text generated always as (coalesce(json_extract(content_json, '$.ListingId'), json_extract(content_json, '$.listingId'))) stored,
                listing_kind text generated always as (coalesce(json_extract(content_json, '$.ListingKind'), json_extract(content_json, '$.listingKind'))) stored,
                item_json text generated always as (coalesce(json_extract(content_json, '$.Item'), json_extract(content_json, '$.item'))) stored,
                price_silver integer generated always as (coalesce(json_extract(content_json, '$.PriceSilver'), json_extract(content_json, '$.priceSilver'))) stored,
                stock_count integer generated always as (coalesce(json_extract(content_json, '$.StockCount'), json_extract(content_json, '$.stockCount'))) stored,
                price_increase_ratio real generated always as (coalesce(json_extract(content_json, '$.PriceIncreaseRatio'), json_extract(content_json, '$.priceIncreaseRatio'))) stored,
                listing_updated_at_utc text generated always as (coalesce(json_extract(content_json, '$.UpdatedAtUtc'), json_extract(content_json, '$.updatedAtUtc'))) stored,
                updated_by_user_id text generated always as (coalesce(json_extract(content_json, '$.UpdatedByUserId'), json_extract(content_json, '$.updatedByUserId'))) stored
            );
            create unique index if not exists ux_server_shop_listings_id on server_shop_listings(listing_id);
            create index if not exists ix_server_shop_listings_kind_stock on server_shop_listings(listing_kind, stock_count);

            create table if not exists server_shop_buyer_purchases (
                item_key text primary key not null,
                content_json text not null check (json_valid(content_json)),
                updated_at_utc text not null,
                listing_id text generated always as (coalesce(json_extract(content_json, '$.ListingId'), json_extract(content_json, '$.listingId'))) stored,
                user_id text generated always as (coalesce(json_extract(content_json, '$.UserId'), json_extract(content_json, '$.userId'))) stored,
                colony_id text generated always as (coalesce(json_extract(content_json, '$.ColonyId'), json_extract(content_json, '$.colonyId'))) stored,
                purchase_count integer generated always as (coalesce(json_extract(content_json, '$.PurchaseCount'), json_extract(content_json, '$.purchaseCount'))) stored
            );
            create unique index if not exists ux_server_shop_buyer on server_shop_buyer_purchases(listing_id, user_id, colony_id);

            create table if not exists server_shop_completed_purchases (
                item_key text primary key not null,
                content_json text not null check (json_valid(content_json)),
                updated_at_utc text not null,
                idempotency_key text generated always as (json_extract(content_json, '$')) stored
            );
            create unique index if not exists ux_server_shop_completed_idempotency on server_shop_completed_purchases(idempotency_key);

            create table if not exists server_diplomacy_relations (
                item_key text primary key not null,
                content_json text not null check (json_valid(content_json)),
                updated_at_utc text not null,
                user_a text generated always as (coalesce(json_extract(content_json, '$.UserA'), json_extract(content_json, '$.userA'))) stored,
                colony_a text generated always as (coalesce(json_extract(content_json, '$.ColonyA'), json_extract(content_json, '$.colonyA'))) stored,
                user_b text generated always as (coalesce(json_extract(content_json, '$.UserB'), json_extract(content_json, '$.userB'))) stored,
                colony_b text generated always as (coalesce(json_extract(content_json, '$.ColonyB'), json_extract(content_json, '$.colonyB'))) stored,
                relation_kind text generated always as (coalesce(json_extract(content_json, '$.RelationKind'), json_extract(content_json, '$.relationKind'))) stored,
                source_event_id text generated always as (coalesce(json_extract(content_json, '$.SourceEventId'), json_extract(content_json, '$.sourceEventId'))) stored,
                relation_updated_at_utc text generated always as (coalesce(json_extract(content_json, '$.UpdatedAtUtc'), json_extract(content_json, '$.updatedAtUtc'))) stored
            );
            create unique index if not exists ux_server_diplomacy_endpoints on server_diplomacy_relations(user_a, colony_a, user_b, colony_b);
            create index if not exists ix_server_diplomacy_user_a on server_diplomacy_relations(user_a, colony_a);
            create index if not exists ix_server_diplomacy_user_b on server_diplomacy_relations(user_b, colony_b);

            create table if not exists server_raid_protection_activations (
                item_key text primary key not null,
                content_json text not null check (json_valid(content_json)),
                updated_at_utc text not null,
                raid_event_id text generated always as (coalesce(json_extract(content_json, '$.RaidEventId'), json_extract(content_json, '$.raidEventId'))) stored,
                defender_user_id text generated always as (coalesce(json_extract(content_json, '$.DefenderUserId'), json_extract(content_json, '$.defenderUserId'))) stored,
                defender_colony_id text generated always as (coalesce(json_extract(content_json, '$.DefenderColonyId'), json_extract(content_json, '$.defenderColonyId'))) stored,
                activated_at_utc text generated always as (coalesce(json_extract(content_json, '$.ActivatedAtUtc'), json_extract(content_json, '$.activatedAtUtc'))) stored
            );
            create unique index if not exists ux_server_raid_protection_event on server_raid_protection_activations(raid_event_id);
            create index if not exists ix_server_raid_protection_colony on server_raid_protection_activations(defender_user_id, defender_colony_id);

            create table if not exists server_chat_messages (
                item_key text primary key not null,
                content_json text not null check (json_valid(content_json)),
                updated_at_utc text not null,
                sequence integer generated always as (coalesce(json_extract(content_json, '$.Sequence'), json_extract(content_json, '$.sequence'))) stored,
                message_id text generated always as (coalesce(json_extract(content_json, '$.MessageId'), json_extract(content_json, '$.messageId'))) stored,
                from_user_id text generated always as (coalesce(json_extract(content_json, '$.FromUserId'), json_extract(content_json, '$.fromUserId'))) stored,
                from_colony_id text generated always as (coalesce(json_extract(content_json, '$.FromColonyId'), json_extract(content_json, '$.fromColonyId'))) stored,
                target_user_id text generated always as (coalesce(json_extract(content_json, '$.TargetUserId'), json_extract(content_json, '$.targetUserId'))) stored,
                message_text text generated always as (coalesce(json_extract(content_json, '$.Text'), json_extract(content_json, '$.text'))) stored,
                sent_at_utc text generated always as (coalesce(json_extract(content_json, '$.SentAtUtc'), json_extract(content_json, '$.sentAtUtc'))) stored
            );
            create unique index if not exists ux_server_chat_sequence on server_chat_messages(sequence);
            create unique index if not exists ux_server_chat_message_id on server_chat_messages(message_id);
            create index if not exists ix_server_chat_target_sequence on server_chat_messages(target_user_id, sequence);

            create table if not exists server_achievement_events (
                item_key text primary key not null,
                content_json text not null check (json_valid(content_json)),
                updated_at_utc text not null,
                user_id text generated always as (coalesce(json_extract(content_json, '$.UserId'), json_extract(content_json, '$.userId'))) stored,
                colony_id text generated always as (coalesce(json_extract(content_json, '$.ColonyId'), json_extract(content_json, '$.colonyId'))) stored,
                achievement_id text generated always as (coalesce(json_extract(content_json, '$.AchievementId'), json_extract(content_json, '$.achievementId'))) stored,
                event_key text generated always as (coalesce(json_extract(content_json, '$.EventKey'), json_extract(content_json, '$.eventKey'))) stored,
                value integer generated always as (coalesce(json_extract(content_json, '$.Value'), json_extract(content_json, '$.value'))) stored,
                category text generated always as (coalesce(json_extract(content_json, '$.Category'), json_extract(content_json, '$.category'))) stored,
                source_snapshot_id text generated always as (coalesce(json_extract(content_json, '$.SourceSnapshotId'), json_extract(content_json, '$.sourceSnapshotId'))) stored,
                recorded_at_utc text generated always as (coalesce(json_extract(content_json, '$.RecordedAtUtc'), json_extract(content_json, '$.recordedAtUtc'))) stored,
                metadata_json text generated always as (coalesce(json_extract(content_json, '$.MetadataJson'), json_extract(content_json, '$.metadataJson'))) stored,
                color text generated always as (coalesce(coalesce(json_extract(content_json, '$.Color'), json_extract(content_json, '$.color')), 'Green')) stored
            );
            create unique index if not exists ux_server_achievement_events_key on server_achievement_events(user_id, colony_id, achievement_id, event_key);
            create index if not exists ix_server_achievement_events_user on server_achievement_events(user_id, colony_id, recorded_at_utc);

            create table if not exists server_achievement_aggregates (
                item_key text primary key not null,
                content_json text not null check (json_valid(content_json)),
                updated_at_utc text not null,
                user_id text generated always as (coalesce(json_extract(content_json, '$.UserId'), json_extract(content_json, '$.userId'))) stored,
                colony_id text generated always as (coalesce(json_extract(content_json, '$.ColonyId'), json_extract(content_json, '$.colonyId'))) stored,
                achievement_id text generated always as (coalesce(json_extract(content_json, '$.AchievementId'), json_extract(content_json, '$.achievementId'))) stored,
                value integer generated always as (coalesce(json_extract(content_json, '$.Value'), json_extract(content_json, '$.value'))) stored,
                source_snapshot_id text generated always as (coalesce(json_extract(content_json, '$.SourceSnapshotId'), json_extract(content_json, '$.sourceSnapshotId'))) stored,
                aggregate_updated_at_utc text generated always as (coalesce(json_extract(content_json, '$.UpdatedAtUtc'), json_extract(content_json, '$.updatedAtUtc'))) stored,
                color text generated always as (coalesce(coalesce(json_extract(content_json, '$.Color'), json_extract(content_json, '$.color')), 'Green')) stored
            );
            create unique index if not exists ux_server_achievement_aggregates_key on server_achievement_aggregates(user_id, colony_id, achievement_id);

            create table if not exists server_achievement_metric_events (
                item_key text primary key not null,
                content_json text not null check (json_valid(content_json)),
                updated_at_utc text not null,
                user_id text generated always as (coalesce(json_extract(content_json, '$.UserId'), json_extract(content_json, '$.userId'))) stored,
                colony_id text generated always as (coalesce(json_extract(content_json, '$.ColonyId'), json_extract(content_json, '$.colonyId'))) stored,
                metric_id text generated always as (coalesce(json_extract(content_json, '$.MetricId'), json_extract(content_json, '$.metricId'))) stored,
                event_key text generated always as (coalesce(json_extract(content_json, '$.EventKey'), json_extract(content_json, '$.eventKey'))) stored,
                value integer generated always as (coalesce(json_extract(content_json, '$.Value'), json_extract(content_json, '$.value'))) stored,
                source_snapshot_id text generated always as (coalesce(json_extract(content_json, '$.SourceSnapshotId'), json_extract(content_json, '$.sourceSnapshotId'))) stored,
                recorded_at_utc text generated always as (coalesce(json_extract(content_json, '$.RecordedAtUtc'), json_extract(content_json, '$.recordedAtUtc'))) stored
            );
            create unique index if not exists ux_server_achievement_metric_events_key on server_achievement_metric_events(user_id, colony_id, metric_id, event_key);

            create table if not exists server_achievement_metric_aggregates (
                item_key text primary key not null,
                content_json text not null check (json_valid(content_json)),
                updated_at_utc text not null,
                user_id text generated always as (coalesce(json_extract(content_json, '$.UserId'), json_extract(content_json, '$.userId'))) stored,
                colony_id text generated always as (coalesce(json_extract(content_json, '$.ColonyId'), json_extract(content_json, '$.colonyId'))) stored,
                metric_id text generated always as (coalesce(json_extract(content_json, '$.MetricId'), json_extract(content_json, '$.metricId'))) stored,
                value integer generated always as (coalesce(json_extract(content_json, '$.Value'), json_extract(content_json, '$.value'))) stored,
                source_snapshot_id text generated always as (coalesce(json_extract(content_json, '$.SourceSnapshotId'), json_extract(content_json, '$.sourceSnapshotId'))) stored,
                aggregate_updated_at_utc text generated always as (coalesce(json_extract(content_json, '$.UpdatedAtUtc'), json_extract(content_json, '$.updatedAtUtc'))) stored
            );
            create unique index if not exists ux_server_achievement_metric_aggregates_key on server_achievement_metric_aggregates(user_id, colony_id, metric_id);
            """;
        command.ExecuteNonQuery();
    }

    internal static void MigrateFromKeyedJson(SqliteConnection connection, SqliteTransaction transaction)
    {
        EnsureTables(connection, transaction);
        EnsureMarkerTable(connection, transaction);
        if (TableExists(connection, transaction, "server_keyed_json_records"))
        {
            foreach (DomainRegistryDefinition definition in Definitions.Values)
            {
                foreach (DomainTableRoute route in definition.Routes)
                {
                    QuarantineInvalidRoute(connection, transaction, definition.RegistryKey, route);
                    CopyRoute(connection, transaction, definition.RegistryKey, route);
                    DeleteLegacyRoute(connection, transaction, definition.RegistryKey, route);
                }

                DeleteLegacyMarkers(connection, transaction, definition.RegistryKey);
            }
        }

        foreach (string registryKey in Definitions.Keys)
        {
            MarkInitialized(connection, transaction, registryKey);
        }
    }

    private static void CopyRoute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string registryKey,
        DomainTableRoute route)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            insert into {route.TableName} (item_key, content_json, updated_at_utc)
            select item_key, content_json, updated_at_utc
            from server_keyed_json_records
            where collection_key = $collection_key
                and item_key <> $marker_key
                and item_key <> '__initialized__'
                and item_key like $item_prefix escape '\'
                and json_valid(content_json)
            on conflict(item_key) do update set
                content_json = excluded.content_json,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$collection_key", registryKey);
        command.Parameters.AddWithValue("$item_prefix", EscapeLike(route.ItemKeyPrefix) + "%");
        command.Parameters.AddWithValue("$marker_key", LegacyInitializationMarkerKey);
        command.ExecuteNonQuery();
    }

    private static void QuarantineInvalidRoute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string registryKey,
        DomainTableRoute route)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into server_registry_quarantine (
                collection_key, item_key, content_json, error_text, quarantined_at_utc
            )
            select collection_key, item_key, content_json, 'Invalid JSON', $quarantined_at_utc
            from server_keyed_json_records
            where collection_key = $collection_key
                and item_key <> $marker_key
                and item_key <> '__initialized__'
                and item_key like $item_prefix escape '\'
                and not json_valid(content_json)
            on conflict(collection_key, item_key) do update set
                content_json = excluded.content_json,
                error_text = excluded.error_text,
                quarantined_at_utc = excluded.quarantined_at_utc;
            """;
        command.Parameters.AddWithValue("$collection_key", registryKey);
        command.Parameters.AddWithValue("$item_prefix", EscapeLike(route.ItemKeyPrefix) + "%");
        command.Parameters.AddWithValue("$marker_key", LegacyInitializationMarkerKey);
        command.Parameters.AddWithValue("$quarantined_at_utc", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void DeleteLegacyRoute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string registryKey,
        DomainTableRoute route)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            delete from server_keyed_json_records
            where collection_key = $collection_key
                and item_key <> $marker_key
                and item_key <> '__initialized__'
                and item_key like $item_prefix escape '\';
            """;
        command.Parameters.AddWithValue("$collection_key", registryKey);
        command.Parameters.AddWithValue("$marker_key", LegacyInitializationMarkerKey);
        command.Parameters.AddWithValue("$item_prefix", EscapeLike(route.ItemKeyPrefix) + "%");
        command.ExecuteNonQuery();
    }

    private static void DeleteLegacyMarkers(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string registryKey)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            delete from server_keyed_json_records
            where collection_key = $collection_key
                and item_key in ($marker_key, '__initialized__');
            """;
        command.Parameters.AddWithValue("$collection_key", registryKey);
        command.Parameters.AddWithValue("$marker_key", LegacyInitializationMarkerKey);
        command.ExecuteNonQuery();
    }

    private static void EnsureMarkerTable(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            create table if not exists server_structured_registry_markers (
                registry_key text primary key not null,
                initialized_at_utc text not null
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void MarkInitialized(SqliteConnection connection, SqliteTransaction transaction, string registryKey)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into server_structured_registry_markers (registry_key, initialized_at_utc)
            values ($registry_key, $initialized_at_utc)
            on conflict(registry_key) do update set initialized_at_utc = excluded.initialized_at_utc;
            """;
        command.Parameters.AddWithValue("$registry_key", registryKey);
        command.Parameters.AddWithValue("$initialized_at_utc", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select exists(select 1 from sqlite_master where type = 'table' and name = $table_name);";
        command.Parameters.AddWithValue("$table_name", tableName);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    private static string EscapeLike(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}

internal sealed record DomainRegistryDefinition(
    string RegistryKey,
    IReadOnlyList<DomainTableRoute> Routes)
{
    internal DomainTableRoute Resolve(string itemKey)
    {
        DomainTableRoute? route = Routes
            .OrderByDescending(candidate => candidate.ItemKeyPrefix.Length)
            .FirstOrDefault(candidate => itemKey.StartsWith(candidate.ItemKeyPrefix, StringComparison.Ordinal));
        return route ?? throw new InvalidDataException(
            $"Record key '{itemKey}' is not valid for registry '{RegistryKey}'.");
    }
}

internal sealed record DomainTableRoute(string ItemKeyPrefix, string TableName);

internal sealed class SqliteDomainKeyedJsonRecordStore : SqliteStructuredRegistryStore, IKeyedJsonRecordStore
{
    private readonly DomainRegistryDefinition definition;

    internal SqliteDomainKeyedJsonRecordStore(string databasePath, string registryKey)
        : base(databasePath)
    {
        definition = SqliteDomainRegistrySchema.GetDefinition(registryKey);
        using SqliteConnection connection = OpenConnection();
        if (!definition.Routes.All(route => TableExists(connection, route.TableName)))
        {
            SqliteDomainRegistrySchema.EnsureTables(connection);
        }
    }

    public bool IsInitialized()
    {
        return IsRegistryInitialized(definition.RegistryKey);
    }

    public IReadOnlyDictionary<string, string> ReadAll()
    {
        return ReadAllFromDatabase();
    }

    public void ApplyBatch(
        IReadOnlyDictionary<string, string> upserts,
        IReadOnlyCollection<string> deletes)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(deletes);

        ApplyBatchToDatabase(upserts, deletes);
    }

    public void ReplaceAllForImport(IReadOnlyDictionary<string, string> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        MarkRegistryInitialized(connection, transaction, definition.RegistryKey);
        foreach (DomainTableRoute route in definition.Routes)
        {
            using SqliteCommand delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"delete from {route.TableName};";
            delete.ExecuteNonQuery();
        }

        string updatedAtUtc = DateString(DateTimeOffset.UtcNow);
        foreach (KeyValuePair<string, string> pair in records)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            Upsert(connection, transaction, definition.Resolve(pair.Key), pair.Key, pair.Value, updatedAtUtc);
        }

        transaction.Commit();
    }

    [Obsolete("Runtime code must use ApplyBatch. Full replacement is reserved for explicit imports.")]
    public void ReplaceAll(IReadOnlyDictionary<string, string> records)
    {
        ReplaceAllForImport(records);
    }

    private Dictionary<string, string> ReadAllFromDatabase()
    {
        var records = new Dictionary<string, string>(StringComparer.Ordinal);
        using SqliteConnection connection = OpenConnection();
        foreach (DomainTableRoute route in definition.Routes)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"select item_key, content_json from {route.TableName} order by item_key;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                records[reader.GetString(0)] = reader.GetString(1);
            }
        }

        return records;
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "select exists(select 1 from sqlite_master where type = 'table' and name = $table_name);";
        command.Parameters.AddWithValue("$table_name", tableName);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    private void ApplyBatchToDatabase(
        IReadOnlyDictionary<string, string> upserts,
        IReadOnlyCollection<string> deletes)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        MarkRegistryInitialized(connection, transaction, definition.RegistryKey);
        string updatedAtUtc = DateString(DateTimeOffset.UtcNow);
        foreach (KeyValuePair<string, string> pair in upserts)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                Upsert(connection, transaction, definition.Resolve(pair.Key), pair.Key, pair.Value, updatedAtUtc);
            }
        }

        foreach (string itemKey in deletes.Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal))
        {
            Delete(connection, transaction, definition.Resolve(itemKey), itemKey);
        }

        transaction.Commit();
    }

    private static void Upsert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DomainTableRoute route,
        string itemKey,
        string contentJson,
        string updatedAtUtc)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            insert into {route.TableName} (item_key, content_json, updated_at_utc)
            values ($item_key, $content_json, $updated_at_utc)
            on conflict(item_key) do update set
                content_json = excluded.content_json,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$item_key", itemKey);
        command.Parameters.AddWithValue("$content_json", contentJson);
        command.Parameters.AddWithValue("$updated_at_utc", updatedAtUtc);
        command.ExecuteNonQuery();
    }

    private static void Delete(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DomainTableRoute route,
        string itemKey)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"delete from {route.TableName} where item_key = $item_key;";
        command.Parameters.AddWithValue("$item_key", itemKey);
        command.ExecuteNonQuery();
    }
}
