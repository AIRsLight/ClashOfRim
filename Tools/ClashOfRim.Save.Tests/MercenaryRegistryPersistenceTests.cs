using AIRsLight.ClashOfRim.Network;
using Microsoft.Data.Sqlite;
using System.Text.Json;

internal static class MercenaryRegistryPersistenceTests
{
    private static readonly string[] Operations = { "create", "activate", "confirm", "cancel", "complete", "close", "guard-activate", "guard-consume" };

    public static void Run()
    {
        foreach (bool reopen in new[] { false, true })
        foreach (string operation in Operations)
        {
            string root = Path.Combine(Path.GetTempPath(), "cor-mercenary-registry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string database = Path.Combine(root, "server.sqlite");
                ServerDatabaseMigrator.Migrate(database);
                Registries Open() => new(
                    new MercenaryContractRegistry(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.MercenaryContracts), null),
                    new MercenaryGuardContractRegistry(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.MercenaryGuards), null));
                var state = Open();
                Seed(state, operation);
                string before = Inspect(state);
                string table = operation.StartsWith("guard-", StringComparison.Ordinal) ? "server_mercenary_guard_contracts" : "server_mercenary_contracts";
                // For bulk changes fail on the second pending row, after an earlier row was written.
                string condition = operation is "confirm" or "cancel" or "close" ? $" when {(operation == "cancel" ? "old" : "new")}.item_key = 'contract:pending2'" : "";
                Execute(database, $"create trigger fail_contract before {(operation == "cancel" ? "delete" : "insert")} on {table}{condition} begin select raise(abort, 'injected failure'); end;");
                bool failed = false;
                try { Mutate(state, operation); } catch (SqliteException) { failed = true; }
                Require(failed, "Contract persistence failure was not injected: " + operation);
                Require(Inspect(state) == before && Inspect(Open()) == before, "Failed contract write changed cache or durable state: " + operation);
                Execute(database, "drop trigger fail_contract;");
                if (reopen) state = Open();
                Mutate(state, operation);
                VerifyAfter(state, operation);
                Require(Inspect(state) == Inspect(Open()), "Contract retry cache differs from durable state: " + operation);
            }
            finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
        }
        foreach (string operation in Operations)
        {
            var contracts = new FailingSlot();
            var guards = new FailingSlot();
            Registries Open() => new(new MercenaryContractRegistry(contracts), new MercenaryGuardContractRegistry(guards));
            var state = Open();
            Seed(state, operation);
            string before = Inspect(state);
            contracts.Fail = guards.Fail = true;
            bool failed = false;
            try { Mutate(state, operation); } catch (IOException) { failed = true; }
            Require(failed && Inspect(state) == before && Inspect(Open()) == before, "JSON failure leaked contract state: " + operation);
            contracts.Fail = guards.Fail = false;
            Mutate(state, operation);
            VerifyAfter(state, operation);
            Require(Inspect(state) == Inspect(Open()), "JSON retry did not persist contracts.");
        }
    }

    private static void Seed(Registries state, string operation)
    {
        foreach (string id in new[] { "pending1", "pending2" }) Create(state.Contracts, id);
        Activate(state.Contracts, "active");
        if (operation != "guard-activate") ActivateGuard(state.Guards);
    }

    private static void Mutate(Registries state, string operation)
    {
        switch (operation)
        {
            case "create": Create(state.Contracts, "new"); break;
            case "activate": Activate(state.Contracts, "new"); break;
            case "confirm": state.Contracts.ConfirmPendingForSnapshot("user", "colony", "confirmed", DateTimeOffset.UnixEpoch); break;
            case "cancel": state.Contracts.ReconcilePendingConfirmations("user", "colony", DateTimeOffset.UnixEpoch, TimeSpan.Zero, true); break;
            case "complete": state.Contracts.Complete("active", "user", "colony"); break;
            case "close": state.Contracts.CloseForColony("user", "colony"); break;
            case "guard-activate": Require(ActivateGuard(state.Guards) is not null, "Guard retry rejected."); break;
            case "guard-consume": Require(state.Guards.ConsumeForRaid("user", "colony", "raid", "guard", DateTimeOffset.UnixEpoch) is not null, "Guard consume retry rejected."); break;
        }
    }

    private static void VerifyAfter(Registries state, string operation)
    {
        int open = operation switch { "create" or "activate" => 4, "cancel" => 1, "complete" => 2, "close" => 0, _ => 3 };
        int active = operation switch { "activate" => 2, "confirm" => 3, "complete" or "close" => 0, _ => 1 };
        Require(state.Contracts.CountOpenForColony("user", "colony") == open
            && state.Contracts.CountActiveForColony("user", "colony") == active, "Contract count indexes drifted: " + operation);
        if (operation == "cancel") Require(state.Contracts.FindByIdempotencyKey("pending1") is null && state.Contracts.Find("pending2") is null, "Cancelled pending contract remained indexed.");
        if (operation is "guard-activate" or "guard-consume")
            Require(state.Guards.HasActiveForColony("user", "colony") == (operation == "guard-activate")
                && state.Guards.FindByIdempotencyKey("guard")!.ConsumedRaidEventId == (operation == "guard-consume" ? "raid" : null), "Guard active index or consumption receipt is incorrect.");
        string before = Inspect(state);
        if (operation is not ("guard-consume" or "confirm")) Mutate(state, operation);
        Require(Inspect(state) == before, "Retry duplicated contract state: " + operation);
    }

    private static string Inspect(Registries state) => JsonSerializer.Serialize(new
    {
        Contracts = new[] { "pending1", "pending2", "active", "new" }.Select(state.Contracts.FindByIdempotencyKey).ToArray(),
        ById = new[] { "pending1", "pending2", "active", "new" }.Select(state.Contracts.Find).ToArray(),
        Open = state.Contracts.CountOpenForColony("user", "colony"),
        Active = state.Contracts.CountActiveForColony("user", "colony"),
        Guard = state.Guards.FindByIdempotencyKey("guard"),
        ActiveGuard = state.Guards.FindActiveForColony("user", "colony")
    });
    private static void Create(MercenaryContractRegistry registry, string id) => registry.Create(id, "user", "colony", "snapshot", "Shooting", 10, 5, 100, 200, 300, 0, DateTimeOffset.UnixEpoch, id);
    private static void Activate(MercenaryContractRegistry registry, string id) => registry.ActivateWithSnapshot(id, "user", "colony", id, "snapshot", "Shooting", 10, 5, 100, 200, 300, 0, DateTimeOffset.UnixEpoch);
    private static MercenaryGuardContractRecord? ActivateGuard(MercenaryGuardContractRegistry registry) => registry.ActivateWithSnapshot("guard", "user", "colony", "guard", "snapshot", "Basic", 100, 1, 0, DateTimeOffset.UnixEpoch);
    private sealed record Registries(MercenaryContractRegistry Contracts, MercenaryGuardContractRegistry Guards);
    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class FailingSlot : IJsonPersistenceSlot
    {
        private string? value;
        public bool Fail;
        public string? Read() => value;
        public void Write(string json)
        {
            if (Fail) throw new IOException("Injected JSON write failure.");
            value = json;
        }
    }
}
