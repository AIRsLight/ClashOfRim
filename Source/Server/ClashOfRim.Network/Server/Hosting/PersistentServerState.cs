using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Hosting;

internal static class PersistentServerState
{
    internal static (ClashOfRimNetworkState State, ServerPersistenceMigrationResult Migration) Create(
        string dataDirectory,
        ClashOfRimServerConfiguration serverConfiguration,
        ServerPluginRegistry plugins,
        ServerPersistenceMigrationService persistenceMigrations)
    {
        string databasePath = Path.Combine(dataDirectory, "server.sqlite");
        ServerPersistenceMigrationResult migration = persistenceMigrations.ValidateForStartup();
        ServerConfigurationRegistry serverConfigurationOverrides =
            new(new SqliteJsonPersistenceSlot(databasePath, "server-configuration"));
        return (new ClashOfRimNetworkState(
            // Keep the original directory name so pending version 1 bank intents survive upgrades.
            snapshotCommits: new SnapshotCommitService(Path.Combine(dataDirectory, "bank-snapshot-commits")),
            colonyDeletions: new ColonyDeletionService(Path.Combine(dataDirectory, "colony-deletions")),
            ledger: new SqliteAuthoritativeEventLedger(databasePath),
            snapshotStore: new FileColonySnapshotIndexStore(Path.Combine(dataDirectory, "snapshots")),
            serverConfiguration: serverConfiguration,
            serverConfigurationOverrides: serverConfigurationOverrides,
            worldConfigurationRegistry: new WorldConfigurationRegistry(
                new SqliteKeyedJsonRecordStore(databasePath, "world-configuration"),
                legacyPersistence: null,
                binaryPersistence: new SqliteJsonPersistenceSlot(databasePath, "world-configuration"),
                worldExtensions: WorldConfigurationExtensionService.Empty),
            compatibilityBaselineRegistry: new CompatibilityBaselineRegistry(new SqliteJsonPersistenceSlot(databasePath, "compatibility-baseline")),
            adminBaselineRegistry: new AdminBaselineRegistry(new SqliteJsonPersistenceSlot(databasePath, "admin-baseline")),
            playerRegistry: new PlayerRegistry(
                new SqlitePlayerRegistryStore(databasePath),
                legacyPersistence: null),
            diplomacyRelations: new DiplomacyRelationRegistry(
                new SqliteDomainKeyedJsonRecordStore(databasePath, SqliteDomainRegistrySchema.DiplomacyRelations),
                legacyPersistence: null),
            pawnPackages: new PawnPackageRegistry(
                new SqlitePawnPackageStore(databasePath),
                legacyPersistence: null),
            thingPackages: new ThingPackageRegistry(
                new SqliteThingPackageStore(databasePath),
                legacyPersistence: null),
            raidProtectionActivations: new RaidProtectionActivationRegistry(
                new SqliteDomainKeyedJsonRecordStore(databasePath, SqliteDomainRegistrySchema.RaidProtectionActivations),
                legacyPersistence: null),
            raidCooldownOverrides: new RaidCooldownOverrideRegistry(
                new SqliteRaidCooldownOverrideStore(databasePath)),
            bankLoans: new BankLoanRegistry(
                new SqliteDomainKeyedJsonRecordStore(databasePath, SqliteDomainRegistrySchema.BankLoans),
                legacyPersistence: null),
            mercenaryContracts: new MercenaryContractRegistry(
                new SqliteDomainKeyedJsonRecordStore(databasePath, SqliteDomainRegistrySchema.MercenaryContracts),
                legacyPersistence: null),
            mercenaryGuards: new MercenaryGuardContractRegistry(
                new SqliteDomainKeyedJsonRecordStore(databasePath, SqliteDomainRegistrySchema.MercenaryGuards),
                legacyPersistence: null),
            chatMessages: new ChatMessageRegistry(
                new SqliteDomainKeyedJsonRecordStore(databasePath, SqliteDomainRegistrySchema.ChatMessages),
                legacyPersistence: null),
            serverShop: new ServerShopRegistry(
                new SqliteDomainKeyedJsonRecordStore(databasePath, SqliteDomainRegistrySchema.ServerShop),
                legacyPersistence: null),
            achievements: new AchievementRegistry(
                new SqliteDomainKeyedJsonRecordStore(databasePath, SqliteDomainRegistrySchema.Achievements),
                legacyPersistence: null),
            snapshotPostUploadJobs: new SnapshotPostUploadJobRegistry(
                new SqliteSnapshotPostUploadJobStore(databasePath)),
            snapshotPostUploadArtifacts: new FileSnapshotPostUploadArtifactStore(
                Path.Combine(dataDirectory, "snapshots", "operations")),
            adminControl: new AdminControlRegistry(
                new SqliteKeyedJsonRecordStore(databasePath, "admin-control"),
                legacyPersistence: null),
            offlineAccounts: new OfflineAccountRegistry(
                new SqliteDomainKeyedJsonRecordStore(databasePath, SqliteDomainRegistrySchema.OfflineAccounts),
                legacyPersistence: null),
            steamAuthTickets: BuildSteamAuthTicketValidator(serverConfiguration),
            plugins: plugins), migration);
    }

    private static ISteamAuthTicketValidator BuildSteamAuthTicketValidator(ClashOfRimServerConfiguration configuration)
    {
        if (configuration.AuthenticationDebugMode)
        {
            return new DevelopmentSteamAuthTicketValidator();
        }

        return string.IsNullOrWhiteSpace(configuration.SteamWebApiKey)
            ? new DevelopmentSteamAuthTicketValidator()
            : new SteamWebApiAuthTicketValidator(configuration.SteamWebApiKey!, configuration.SteamAppId);
    }
}
