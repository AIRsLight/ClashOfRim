using System.Reflection;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class SnapshotAchievementRecoveryTests
{
    private static readonly DateTimeOffset Accepted = DateTimeOffset.UnixEpoch.AddDays(2);
    private static readonly SnapshotAchievementCandidateDto Candidate = new("snapshot_replay", "one", 7,
        labelKey: "snapshot replay", aggregationKind: AchievementRegistry.AggregationSum);

    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (bool reopen in new[] { false, true })
        foreach (string fault in new[] { "achievement", "aggregate", "notification-first", "notification-second" })
            VerifyRecovery(fixture, reopen, fault);
        VerifyLegacyCache();
    }

    private static void VerifyRecovery(Func<string, SaveSnapshotPackage> fixture, bool reopen, string fault)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-snapshot-achievement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string database = Path.Combine(root, "server.sqlite");
            ServerDatabaseMigrator.Migrate(database);
            ClashOfRimNetworkState Open() => new(ledger: new SqliteAuthoritativeEventLedger(database),
                achievements: new AchievementRegistry(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.Achievements), null));
            var state = Open();
            SaveSnapshotPackage package = fixture("achievement-snapshot");
            var snapshot = new LatestSnapshotRecord(package.Envelope.Identity, package.Envelope, package.Index with
            {
                HistoryWealthItems = 300_000, HistoryWealthBuildings = 0, HistoryWealthPawns = 0,
                HistoryPrisonerCount = 0, HistoryColonistMood = 0, StoryColonistsLaunched = 0
            }, Accepted);
            string table = fault == "achievement" ? "server_achievement_events" : fault == "aggregate" ? "server_achievement_aggregates" : "events";
            string condition = fault is "achievement" or "aggregate"
                ? "when json_extract(new.content_json, '$.AchievementId') = 'snapshot_replay'"
                : fault == "notification-second" ? "when (select count(*) from events) = 1" : "";
            Sql(database, $"create trigger fail_snapshot_achievement before insert on {table} {condition} begin select raise(abort, 'injected snapshot achievement failure'); end;");
            bool failed = false;
            try { Record(state, snapshot, [Candidate]); }
            catch (TargetInvocationException ex) when (ex.InnerException is SqliteException) { failed = true; }
            Require(failed, "fault was not injected");
            int expectedRecords = fault is "achievement" or "aggregate" ? 1 : 2;
            foreach (var current in new[] { state, Open() })
            {
                Require(current.Achievements.ListSnapshotEvents("userA", "colonyA", "achievement-snapshot").Count == expectedRecords,
                    "failed write leaked into snapshot index or reopening lost committed events");
                Require(current.Ledger.ListAll().Count == (fault == "notification-second" ? 1 : 0), "failure produced unexpected notifications");
            }
            Sql(database, "drop trigger fail_snapshot_achievement;");
            if (reopen) state = Open();
            // Previously persisted unlocks must be replayed even if the current metric
            // no longer reaches the threshold or a supplied candidate is now absent.
            var replay = fault is "achievement" or "aggregate" ? snapshot : snapshot with
            {
                Index = snapshot.Index with { HistoryWealthItems = 0 }
            };
            Record(state, replay, fault is "achievement" or "aggregate" ? [Candidate] : [], Accepted.AddMinutes(1));
            Verify(state);
            var before = state.Ledger.ListAll().ToDictionary(item => item.EventId);
            foreach (var item in before.Values)
            {
                state.Ledger.MarkDelivered(item.EventId, "delivered", Accepted);
                state.Ledger.MarkApplied(item.EventId, "applied", Accepted);
            }
            state = Open();
            Record(state, snapshot, [Candidate], Accepted.AddMinutes(2));
            Verify(state);
            Require(state.Ledger.ListAll().All(item => item.Status == ServerEventStatus.AppliedToSnapshot && item.AppliedSnapshotId == "applied"),
                "replay reset consumed notification state");
            Require(state.Ledger.ListAll().All(item => item.CreatedAtUtc == before[item.EventId].CreatedAtUtc), "replay changed notification identity/time");

            long notificationVersion = state.EventNotifications.GetVersion("userA");
            foreach (var identity in new[] { new SnapshotIdentity("userA", "colonyA", "next-snapshot"),
                new SnapshotIdentity("other", "colonyA", "achievement-snapshot"), new SnapshotIdentity("userA", "other", "achievement-snapshot") })
            {
                // Only the original colony/snapshot may recover these notifications.
                var empty = snapshot with { Identity = identity, Envelope = snapshot.Envelope with { Identity = identity },
                    Index = snapshot.Index with { HistoryWealthItems = 0 } };
                Record(state, empty, []);
                Require(state.Achievements.ListSnapshotEvents(identity.OwnerId!, identity.ColonyId!, identity.SnapshotId!).Count == 0,
                    "snapshot event index crossed identity boundaries");
            }
            Require(state.EventNotifications.GetVersion("userA") == notificationVersion && state.Ledger.ListAll().Count == 2,
                "unrelated snapshot replayed old notifications");
            var nextIdentity = snapshot.Identity with { SnapshotId = "next-snapshot" };
            var newer = snapshot with { Identity = nextIdentity, Envelope = snapshot.Envelope with { Identity = nextIdentity },
                Index = snapshot.Index with { HistoryWealthItems = 400_000 } };
            Record(state, newer, [Candidate]);
            Require(state.Achievements.ListAggregates().Single(item => item.AchievementId == "wealth_items_250000").Value == 400_000
                && state.Achievements.ListSnapshotEvents("userA", "colonyA", "next-snapshot").Count == 0
                && state.Ledger.ListAll().Count == 2, "aggregate improvement rewrote the original unlock source or duplicated notices");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static void Verify(ClashOfRimNetworkState state)
    {
        var records = state.Achievements.ListSnapshotEvents("userA", "colonyA", "achievement-snapshot");
        Require(records.Count == 2 && records.Single(item => item.AchievementId == "wealth_items_250000").RecordedAtUtc == Accepted,
            "recovery lost original unlock records");
        Require(state.Achievements.ListAggregates().Single(item => item.AchievementId == "snapshot_replay").Value == 7,
            "replay counted a candidate twice");
        Require(state.Ledger.ListAll().Count == 2 && state.Ledger.ListAll().All(item => item.Target == new EventParty("userA", "colonyA")),
            "unlock notification was lost, duplicated or sent to the wrong colony");
    }

    private static void VerifyLegacyCache()
    {
        var slot = new FailingSlot();
        var registry = new AchievementRegistry(slot);
        slot.Fail = true;
        bool failed = false;
        try { registry.TryRecord("owner", "colony", "snapshot", Candidate, Accepted, out _); }
        catch (IOException) { failed = true; }
        Require(failed && registry.ListSnapshotEvents("owner", "colony", "snapshot").Count == 0,
            "failed legacy persistence leaked a source index entry");
        slot.Fail = false;
        registry.TryRecord("owner", "colony", "snapshot", Candidate, Accepted, out _);
        var captured = registry.ListSnapshotEvents("owner", "colony", "snapshot");
        registry.TryRecord("owner", "colony", "snapshot", new SnapshotAchievementCandidateDto("other", "two", 1), Accepted, out _);
        Require(captured.Count == 1 && registry.ListSnapshotEvents("owner", "colony", "snapshot").Count == 2
            && new AchievementRegistry(slot).ListSnapshotEvents("owner", "colony", "snapshot").Count == 2,
            "source index is not stable or is missing after legacy reload");
    }

    private static void Record(ClashOfRimNetworkState state, LatestSnapshotRecord snapshot, IReadOnlyCollection<SnapshotAchievementCandidateDto> candidates,
        DateTimeOffset? occurredAt = null) =>
        typeof(ClashOfRimNetworkServer).GetMethod("RecordSnapshotMetricsAndAchievements", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { state, snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!, snapshot, occurredAt ?? Accepted, candidates });
    private static void Sql(string database, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception("Snapshot achievement: " + message); }
    private sealed class FailingSlot : IJsonPersistenceSlot
    {
        private string? value;
        public bool Fail;
        public string? Read() => value;
        public void Write(string json) { if (Fail) throw new IOException("injected failure"); value = json; }
    }
}
