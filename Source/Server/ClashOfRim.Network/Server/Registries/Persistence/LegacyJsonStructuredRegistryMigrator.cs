using AIRsLight.ClashOfRim.Network.Plugins;

namespace AIRsLight.ClashOfRim.Network;

/// <summary>
/// Imports the former document-per-registry layout into the current structured
/// stores. This is deliberately reachable only from the explicit schema
/// migration path, never from normal registry construction.
/// </summary>
internal static class LegacyJsonStructuredRegistryMigrator
{
    public static void Import(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        using IDisposable scope = LegacyStructuredImportScope.Begin();

        var worldJson = new SqliteJsonPersistenceSlot(databasePath, "world-configuration");
        _ = new WorldConfigurationRegistry(
            new SqliteKeyedJsonRecordStore(databasePath, "world-configuration"),
            worldJson,
            worldJson,
            WorldConfigurationExtensionService.Empty);
        _ = new PlayerRegistry(
            new SqlitePlayerRegistryStore(databasePath),
            new SqliteJsonPersistenceSlot(databasePath, "players"));
        _ = new DiplomacyRelationRegistry(
            new SqliteDomainKeyedJsonRecordStore(databasePath, SqliteDomainRegistrySchema.DiplomacyRelations),
            new SqliteJsonPersistenceSlot(databasePath, "diplomacy-relations"));
        _ = new PawnPackageRegistry(
            new SqlitePawnPackageStore(databasePath),
            new SqliteJsonPersistenceSlot(databasePath, "pawn-packages"));
        _ = new ThingPackageRegistry(
            new SqliteThingPackageStore(databasePath),
            new SqliteJsonPersistenceSlot(databasePath, "thing-packages"));
        _ = new RaidProtectionActivationRegistry(
            new SqliteDomainKeyedJsonRecordStore(databasePath, SqliteDomainRegistrySchema.RaidProtectionActivations),
            new SqliteJsonPersistenceSlot(databasePath, "raid-protection-activations"));
        _ = new BankLoanRegistry(
            new SqliteDomainKeyedJsonRecordStore(databasePath, SqliteDomainRegistrySchema.BankLoans),
            new SqliteJsonPersistenceSlot(databasePath, "bank-loans"));
        _ = new MercenaryContractRegistry(
            new SqliteDomainKeyedJsonRecordStore(databasePath, SqliteDomainRegistrySchema.MercenaryContracts),
            new SqliteJsonPersistenceSlot(databasePath, "mercenary-contracts"));
        _ = new MercenaryGuardContractRegistry(
            new SqliteDomainKeyedJsonRecordStore(databasePath, SqliteDomainRegistrySchema.MercenaryGuards),
            new SqliteJsonPersistenceSlot(databasePath, "mercenary-guards"));
        _ = new ChatMessageRegistry(
            new SqliteDomainKeyedJsonRecordStore(databasePath, SqliteDomainRegistrySchema.ChatMessages),
            new SqliteJsonPersistenceSlot(databasePath, "chat-messages"));
        _ = new ServerShopRegistry(
            new SqliteDomainKeyedJsonRecordStore(databasePath, SqliteDomainRegistrySchema.ServerShop),
            new SqliteJsonPersistenceSlot(databasePath, "server-shop"));
        _ = new AchievementRegistry(
            new SqliteDomainKeyedJsonRecordStore(databasePath, SqliteDomainRegistrySchema.Achievements),
            new SqliteJsonPersistenceSlot(databasePath, "achievements"));
        _ = new AdminControlRegistry(
            new SqliteKeyedJsonRecordStore(databasePath, "admin-control"),
            new SqliteJsonPersistenceSlot(databasePath, "admin-control"));
        _ = new OfflineAccountRegistry(
            new SqliteDomainKeyedJsonRecordStore(databasePath, SqliteDomainRegistrySchema.OfflineAccounts),
            new SqliteJsonPersistenceSlot(databasePath, "offline-accounts"));
    }
}
