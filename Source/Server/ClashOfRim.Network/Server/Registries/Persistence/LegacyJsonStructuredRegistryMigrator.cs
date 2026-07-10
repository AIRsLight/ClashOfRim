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
            new SqliteKeyedJsonRecordStore(databasePath, "diplomacy-relations"),
            new SqliteJsonPersistenceSlot(databasePath, "diplomacy-relations"));
        _ = new PawnPackageRegistry(
            new SqlitePawnPackageStore(databasePath),
            new SqliteJsonPersistenceSlot(databasePath, "pawn-packages"));
        _ = new ThingPackageRegistry(
            new SqliteThingPackageStore(databasePath),
            new SqliteJsonPersistenceSlot(databasePath, "thing-packages"));
        _ = new RaidProtectionActivationRegistry(
            new SqliteKeyedJsonRecordStore(databasePath, "raid-protection-activations"),
            new SqliteJsonPersistenceSlot(databasePath, "raid-protection-activations"));
        _ = new BankLoanRegistry(
            new SqliteKeyedJsonRecordStore(databasePath, "bank-loans"),
            new SqliteJsonPersistenceSlot(databasePath, "bank-loans"));
        _ = new MercenaryContractRegistry(
            new SqliteKeyedJsonRecordStore(databasePath, "mercenary-contracts"),
            new SqliteJsonPersistenceSlot(databasePath, "mercenary-contracts"));
        _ = new MercenaryGuardContractRegistry(
            new SqliteKeyedJsonRecordStore(databasePath, "mercenary-guards"),
            new SqliteJsonPersistenceSlot(databasePath, "mercenary-guards"));
        _ = new ChatMessageRegistry(
            new SqliteKeyedJsonRecordStore(databasePath, "chat-messages"),
            new SqliteJsonPersistenceSlot(databasePath, "chat-messages"));
        _ = new ServerShopRegistry(
            new SqliteKeyedJsonRecordStore(databasePath, "server-shop"),
            new SqliteJsonPersistenceSlot(databasePath, "server-shop"));
        _ = new AchievementRegistry(
            new SqliteKeyedJsonRecordStore(databasePath, "achievements"),
            new SqliteJsonPersistenceSlot(databasePath, "achievements"));
        _ = new AdminControlRegistry(
            new SqliteKeyedJsonRecordStore(databasePath, "admin-control"),
            new SqliteJsonPersistenceSlot(databasePath, "admin-control"));
        _ = new OfflineAccountRegistry(
            new SqliteKeyedJsonRecordStore(databasePath, "offline-accounts"),
            new SqliteJsonPersistenceSlot(databasePath, "offline-accounts"));
    }
}
