using static RecoveryTestSupport;
using AIRsLight.ClashOfRim.Network;
using Microsoft.Data.Sqlite;
using System.Text.Json;

internal static class BankRegistryPersistenceTests
{
    private static readonly string[] Operations = { "loan-create", "debt-create", "loan-pending", "debt-pending",
        "loan-repaid", "debt-repaid", "confirm-activation", "confirm-repayment", "cancel-activation", "cancel-repayment", "remove" };

    public static void Run()
    {
        foreach (bool reopen in new[] { false, true })
        foreach (string operation in Operations)
        {
            string root = Path.Combine(Path.GetTempPath(), "cor-bank-registry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string database = Path.Combine(root, "server.sqlite");
                ServerDatabaseMigrator.Migrate(database);
                BankLoanRegistry Open() => new(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.BankLoans), null);
                var bank = Open();
                Seed(bank, operation);
                string before = Inspect(bank);
                var debtCache = bank.GetOpenDebts("user", "colony");
                string table = operation.StartsWith("loan-", StringComparison.Ordinal) ? "server_bank_loans" : "server_bank_debts";
                // Bulk operations first touch the loan table; fail in the later debt write to test rollback of both.
                string sqlOperation = operation is "cancel-activation" or "remove" ? "delete" : "insert";
                Execute(database, $"create trigger fail_bank before {sqlOperation} on {table} begin select raise(abort, 'injected failure'); end;");
                bool failed = false;
                try { Mutate(bank, operation); } catch (SqliteException) { failed = true; }
                Require(failed && Inspect(bank) == before && Inspect(Open()) == before, "Failed bank write leaked state: " + operation);
                Require(ReferenceEquals(debtCache, bank.GetOpenDebts("user", "colony")), "Failed bank write invalidated the committed debt cache.");
                Execute(database, "drop trigger fail_bank;");
                if (reopen) bank = Open();
                Mutate(bank, operation);
                VerifyAfter(bank, operation);
                Require(Inspect(bank) == Inspect(Open()), "Retry cache and database differ: " + operation);
            }
            finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
        }
        foreach (string operation in Operations)
        {
            var slot = new Slot();
            var bank = new BankLoanRegistry(slot);
            Seed(bank, operation);
            string before = Inspect(bank);
            slot.Fail = true;
            bool failed = false;
            try { Mutate(bank, operation); } catch (IOException) { failed = true; }
            Require(failed && Inspect(bank) == before && Inspect(new BankLoanRegistry(slot)) == before, "JSON bank failure leaked state: " + operation);
            slot.Fail = false;
            Mutate(bank, operation);
            VerifyAfter(bank, operation);
            Require(Inspect(bank) == Inspect(new BankLoanRegistry(slot)), "JSON retry lost bank records: " + operation);
        }
    }

    private static void Seed(BankLoanRegistry bank, string operation)
    {
        bank.Create("loan", "user", "colony", "before", 100, 10, 7, 1000, DateTimeOffset.UnixEpoch, "loan");
        bank.CreateDebt("debt", "user", "colony", "before", 50, BankLoanRegistry.DebtSourceFine, "fine", "contract", 1000, DateTimeOffset.UnixEpoch, true);
        if (operation is "confirm-activation" or "cancel-activation") return;
        bank.ConfirmPendingForSnapshot("user", "colony", "active", 1000, DateTimeOffset.UnixEpoch);
        if (operation is "confirm-repayment" or "cancel-repayment" or "remove")
        {
            bank.MarkRepaymentPending("loan-payment", "user", "colony", 1000, DateTimeOffset.UnixEpoch);
            bank.MarkDebtRepaymentPending("debt-payment", "user", "colony", bank.FindDebtByIdempotencyKey("debt")!.DebtId, 1000, DateTimeOffset.UnixEpoch);
        }
    }

    private static void Mutate(BankLoanRegistry bank, string operation)
    {
        switch (operation)
        {
            case "loan-create": bank.Create("new-loan", "other", "other-colony", "before", 100, 10, 7, 1000, DateTimeOffset.UnixEpoch, "new-loan"); break;
            case "debt-create": bank.CreateDebt("new-debt", "user", "colony", "before", 50, BankLoanRegistry.DebtSourceFine, "fine", "contract", 1000, DateTimeOffset.UnixEpoch); break;
            case "loan-pending": Require(bank.MarkRepaymentPending("loan-payment", "user", "colony", 1000, DateTimeOffset.UnixEpoch) is not null, "Pending loan retry failed."); break;
            case "debt-pending": Require(bank.MarkDebtRepaymentPending("debt-payment", "user", "colony", bank.FindDebtByIdempotencyKey("debt")!.DebtId, 1000, DateTimeOffset.UnixEpoch) is not null, "Pending debt retry failed."); break;
            case "loan-repaid": Require(bank.MarkRepaidWithSnapshot("loan-payment", "user", "colony", "loan", "paid", 110, 1000, DateTimeOffset.UnixEpoch) is not null, "Loan repayment retry failed."); break;
            case "debt-repaid": Require(bank.MarkDebtRepaidWithSnapshot("debt-payment", "user", "colony", bank.FindDebtByIdempotencyKey("debt")!.DebtId, "paid", 50, 1000, DateTimeOffset.UnixEpoch) is not null, "Debt repayment retry failed."); break;
            case "confirm-activation": case "confirm-repayment": bank.ConfirmPendingForSnapshot("user", "colony", "confirmed", 1000, DateTimeOffset.UnixEpoch); break;
            case "cancel-activation": case "cancel-repayment": bank.ReconcilePendingConfirmations("user", "colony", DateTimeOffset.UnixEpoch, TimeSpan.Zero, true); break;
            case "remove": bank.RemoveForColony("user", "colony"); break;
        }
    }

    private static void VerifyAfter(BankLoanRegistry bank, string operation)
    {
        var loan = bank.FindByIdempotencyKey("loan");
        var debt = bank.FindDebtByIdempotencyKey("debt");
        string? loanStatus = operation switch
        {
            "cancel-activation" or "remove" => null,
            "loan-pending" => BankLoanRegistry.StatusPendingRepayment,
            "loan-repaid" or "confirm-repayment" => BankLoanRegistry.StatusRepaid,
            _ => BankLoanRegistry.StatusActive
        };
        string? debtStatus = operation switch
        {
            "cancel-activation" or "remove" => null,
            "debt-pending" => BankLoanRegistry.StatusPendingRepayment,
            "debt-repaid" or "confirm-repayment" => BankLoanRegistry.StatusRepaid,
            _ => BankLoanRegistry.StatusActive
        };
        Require(loan?.Status == loanStatus && debt?.Status == debtStatus, "Retry did not apply bank status: " + operation);
        Require((bank.GetActive("user", "colony") is null) == (loanStatus is null or BankLoanRegistry.StatusRepaid), "Loan active index drifted.");
        int expectedDebts = debtStatus is null or BankLoanRegistry.StatusRepaid ? 0 : operation == "debt-create" ? 2 : 1;
        Require(bank.GetOpenDebts("user", "colony").Count == expectedDebts, "Open debt cache drifted.");
        if (operation is "cancel-repayment" or "remove")
            Require(bank.FindByIdempotencyKey("loan-payment") is null && bank.FindDebtByIdempotencyKey("debt-payment") is null, "Cancelled payment keys remained indexed.");
        if (operation == "loan-create") Require(bank.GetActive("other", "other-colony")?.IdempotencyKey == "new-loan", "New loan was lost.");
        string before = Inspect(bank);
        Mutate(bank, operation);
        Require(Inspect(bank) == before, "Repeated bank operation changed durable state again: " + operation);
    }

    private static string Inspect(BankLoanRegistry bank) => JsonSerializer.Serialize(new
    {
        Loans = new[] { "loan", "loan-payment", "new-loan" }.Select(bank.FindByIdempotencyKey).ToArray(),
        Debts = new[] { "debt", "debt-payment", "new-debt" }.Select(bank.FindDebtByIdempotencyKey).ToArray(),
        Active = bank.GetActive("user", "colony"), OtherActive = bank.GetActive("other", "other-colony"),
        OpenDebts = bank.GetOpenDebts("user", "colony").OrderBy(debt => debt.DebtId, StringComparer.Ordinal).ToArray()
    });
    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
