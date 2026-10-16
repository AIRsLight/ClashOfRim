using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Data.Sqlite;

internal static class EventDeliveryBatchTests
{
    public static void Run()
    {
        foreach (string backend in new[] { "memory", "file", "sqlite" })
        {
            string root = Path.Combine(Path.GetTempPath(), "cor-event-delivery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, backend == "sqlite" ? "events.sqlite" : "events.json");
            IAuthoritativeEventLedger Open() => backend switch
            {
                "file" => new FileAuthoritativeEventLedger(path),
                "sqlite" => new SqliteAuthoritativeEventLedger(path),
                _ => new InMemoryAuthoritativeEventLedger()
            };
            try
            {
                var ledger = Open();
                var at = DateTimeOffset.UnixEpoch;
                var gift = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("a"), new("b"), "gift", false,
                    new ItemDeliveryEventPayload([], null), at);
                var notice = AuthoritativeEventFactory.Create(ServerEventType.ServerNotification, new("server"), new("b"), "notice", false,
                    new ServerNotificationEventPayload("notice", "title", "text", ServerNotificationSeverity.Info, false), at);
                ledger.Append(gift); ledger.Append(notice);
                string Before() => JsonSerializer.Serialize(ledger.ListAll());
                string original = Before();
                var delivery = new LedgerDeliveryChange(gift, "baseline", at);
                var acceptance = new LedgerAcceptanceChange(notice, at, "read");
                var batch = new LedgerBatch([], [], DeliveryChanges: [delivery], AcceptanceChanges: [acceptance]);
                try
                {
                    ledger.ApplyBatch(batch with { AcceptanceChanges = [acceptance with { Expected = notice with { Status = ServerEventStatus.Failed } }] });
                    throw new Exception("Stale batch was accepted");
                }
                catch (InvalidOperationException) { }
                Require(Before() == original, "invalid batch changed an earlier event");
                bool failed = false;
                if (backend == "sqlite")
                {
                    Sql("create trigger fail_notice before update on events when new.type = 'ServerNotification' begin select raise(abort, 'delivery fault'); end;");
                    try { ledger.ApplyBatch(batch); } catch (SqliteException) { failed = true; }
                    Sql("drop trigger fail_notice;");
                }
                else if (backend == "file" && OperatingSystem.IsWindows())
                {
                    using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    try { ledger.ApplyBatch(batch); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
                }
                if (backend == "sqlite" || backend == "file" && OperatingSystem.IsWindows())
                {
                    Require(failed && Before() == original && JsonSerializer.Serialize(Open().ListAll()) == original,
                        "storage failure partially committed delivery or notification consumption");
                }
                ledger.ApplyBatch(batch);
                if (backend != "memory") ledger = Open();
                Require(ledger.Find(gift.EventId)!.DeliveredToSnapshotId == "baseline"
                    && ledger.Find(notice.EventId)!.Status == ServerEventStatus.AppliedToSnapshot, "delivery batch did not persist");
                void Sql(string sql)
                {
                    using var connection = new SqliteConnection("Data Source=" + path); connection.Open();
                    using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
                }
            }
            finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        }
        Console.WriteLine("通过：内存/文件/SQLite 批量投递校验、持久化与故障回滚");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
