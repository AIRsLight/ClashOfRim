using static SnapshotTestAssertions;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class RaidCleanupCommitTests
{
    public static void Run(SaveSnapshotPackage original)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (string change in new[] { "none", "newer", "token", "removed", "source", "owner", "new-raid", "support-closed", "delivery" })
            RaidCleanupFixture.With(original, false, f => VerifyUnlockedPreparation(f, change));
        RaidCleanupFixture.With(original, false, f =>
        {
            Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(f.InvokeCleanup))).GetAwaiter().GetResult();
            Check(f.Store.WriteAttempts == 1 && f.Store.PreparedWriteAttempts == 1 && f.State.Ledger.ListAll().Count == 4,
                "Concurrent download cleanup duplicated publication or losses.");
        });
        foreach (bool timeout in new[] { false, true })
        foreach (bool reopen in new[] { false, true })
        foreach (string fault in new[] { "before", "after", "player", "loss", "support", "status", "signal", "advanced", "newer", "removed", "token", "source", "support-source" })
            RaidCleanupFixture.With(original, timeout, f => Recovery(f, fault, reopen));
        foreach (bool timeout in new[] { false, true })
        {
            RaidCleanupFixture.With(original, timeout, f => Guard(f, "owner"));
            RaidCleanupFixture.With(original, timeout, f => Guard(f, "raid"));
            foreach (bool committed in new[] { false, true })
                RaidCleanupFixture.With(original, timeout, f => LegacyKeyRecovery(f, committed));
        }
    }

    private static void VerifyUnlockedPreparation(RaidCleanupFixture f, string change)
    {
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            f.InvokeCleanup, () =>
            {
                Check(f.Store.WriteAttempts == 0 && f.State.Ledger.ListAll().Count == 2
                    && !Directory.EnumerateFiles(f.Journal, "*.json").Any(), "Cleanup published before preparation finished.");
                if (change is "newer" or "token")
                {
                    var envelope = change == "newer" ? f.Original.Envelope with { Identity = f.Original.Envelope.Identity with { SnapshotId = "newer" } }
                        : f.Original.Envelope with { NextLineageToken = "replacement-token" };
                    f.Files.StoreLatest(f.Original with { Envelope = envelope }, f.Original.Index, f.Now);
                }
                if (change == "removed") f.Files.RemoveLatest("attacker", "colony-a");
                if (change == "source") f.State.Ledger.ChangeStatus(f.Raid.EventId, ServerEventStatus.Failed);
                if (change == "owner") f.Sql("update events set actor_user_id = 'other' where event_id = 'raid-timeout-001';");
                if (change == "new-raid") f.State.Ledger.Append(f.Raid with
                    { EventId = "new-raid", IdempotencyKey = "new-raid", Status = ServerEventStatus.Recorded,
                        Payload = ((RaidEventPayload)f.Raid.Payload) with { FinishedAtUtc = null, AttackForce = new RaidAttackForceRecord("new-attack", [], []) } });
                if (change == "support-closed") f.State.Ledger.ChangeStatus(f.Support.EventId, ServerEventStatus.Cancelled);
                if (change == "delivery") f.State.Ledger.MarkDelivered(f.Support.EventId, "delivered-during-preparation", f.Now);
            });
        bool accepted = change is "none" or "support-closed" or "delivery";
        Check(f.Store.Preparations == 1 && f.Store.WriteAttempts == (accepted ? 1 : 0)
            && f.Store.PreparedWriteAttempts == f.Store.WriteAttempts && !Directory.EnumerateFiles(f.Journal, "*.json").Any(),
            "Cleanup lost prepared bytes or committed stale preparation: " + change);
        if (accepted)
        {
            Check(!f.Files.GetLatestPackage("attacker", "colony-a")!.Index.WorldObjects.Any(item => item.Def == "ClashOfRim_RemoteRaidBattleMapParent")
                && f.State.Ledger.ListAll().Count == (change == "support-closed" ? 3 : 4)
                && f.State.Ledger.Find(f.Support.EventId)!.Status == (change == "support-closed" ? ServerEventStatus.Cancelled : ServerEventStatus.Failed),
                "Cleanup did not rebuild its losses from current support state.");
            if (change == "delivery") Check(f.State.Ledger.Find(f.Support.EventId)!.DeliveredToSnapshotId == "delivered-during-preparation",
                "Cleanup lost a concurrent support delivery.");
        }
        else Check(f.State.Ledger.ListAll().Count == (change == "new-raid" ? 3 : 2)
            && f.Files.GetLatest("attacker", "colony-a")?.Identity.SnapshotId == (change == "removed" ? null : change == "newer" ? "newer" : f.Original.Envelope.Identity.SnapshotId)
            && (change != "token" || f.Files.GetLatest("attacker", "colony-a")!.Envelope.NextLineageToken == "replacement-token"),
            "Rejected cleanup altered the competing snapshot or ledger.");
    }

    private static void LegacyKeyRecovery(RaidCleanupFixture f, bool committed)
    {
        var generated = f.Mutation.Batch.Events.Single(item => item.Type == ServerEventType.SupportPawn);
        string key = $"support-loss:{f.Support.EventId}:RaidSettlementLost";
        var legacy = generated with { IdempotencyKey = key, EventId = AuthoritativeEventFactory.BuildLegacyEventId(generated.Type, key) };
        var mutation = f.Mutation with { Batch = f.Mutation.Batch with
            { Events = f.Mutation.Batch.Events.Select(item => item.EventId == generated.EventId ? legacy : item).ToArray() } };
        if (!committed) f.Store.FailPublication = true;
        Fails(() => f.State.SnapshotCommits.CommitRaidCleanup(f.Store, f.State.BankLoans, f.State.Ledger, f.State.Players,
            f.Cleaned, mutation, f.Now, _ => throw new IOException("before wake")), "Legacy journal interruption not reached.");
        if (committed)
        {
            f.State.Ledger.MarkDelivered(legacy.EventId, "base", f.Now);
            legacy = f.State.Ledger.MarkApplied(legacy.EventId, "consumed", f.Now);
        }
        f.Open();
        f.State.SnapshotMutations.Recover();
        f.InvokeCleanup();
        var actual = f.State.Ledger.Find(legacy.EventId);
        Check(actual is not null && JsonSerializer.Serialize(actual) == JsonSerializer.Serialize(legacy)
            && f.State.Ledger.Find(generated.EventId) is null && f.State.Ledger.ListAll().Count == 4
            && !Directory.EnumerateFiles(f.Journal, "*.json").Any(), "Legacy cleanup recovery renamed or duplicated support loss.");
    }

    private static void Recovery(RaidCleanupFixture f, string fault, bool reopen)
    {
        bool conflict = fault is "newer" or "removed" or "token" or "source" or "support-source";
        f.Store.FailPublication = fault == "before" || conflict;
        f.Store.FailAfterPublication = fault == "after";
        if (fault == "player") f.Sql("create trigger fail_write before insert on server_players begin select raise(abort, 'player'); end;");
        if (fault is "loss" or "support") f.Sql("create trigger fail_write before insert on events when NEW.idempotency_key like '"
            + (fault == "loss" ? "raid-attacker-loss:%" : "supportlossv2:%") + "' begin select raise(abort, 'event'); end;");
        if (fault == "status") f.Sql("create trigger fail_write before update on events begin select raise(abort, 'status'); end;");
        if (fault is "signal" or "advanced")
            Fails(() => f.State.SnapshotCommits.CommitRaidCleanup(f.Store, f.State.BankLoans, f.State.Ledger, f.State.Players,
                f.Cleaned, f.Mutation, f.Now, _ => throw new IOException("signal")), "Expected signal failure.");
        else Fails(() => f.InvokeCleanup(), "Cleanup swallowed a commit failure and returned a successful stale download.");
        string path = Directory.EnumerateFiles(f.Journal, "*.json").Single();
        var intent = JsonSerializer.Deserialize<SnapshotCommitIntent>(File.ReadAllBytes(path))!;
        Check(intent.Version == 9 && intent.RaidCleanup?.Batch.Events.Count == 2, "Cleanup did not persist the full loss batch.");
        if (fault is "player" or "loss" or "support" or "status")
        {
            Check(f.State.Ledger.ListAll().Count == 2 && f.State.Ledger.Find("support-in-raid")!.Status == f.Support.Status,
                "Failed cleanup batch exposed partial notifications or support failure.");
            f.Sql("drop trigger fail_write;");
        }
        if (fault is "newer" or "token")
        {
            var envelope = f.Original.Envelope;
            envelope = fault == "newer" ? envelope with { Identity = envelope.Identity with { SnapshotId = "newer" } }
                : envelope with { NextLineageToken = "new-token" };
            f.Files.StoreLatest(f.Original with { Envelope = envelope }, f.Original.Index, f.Now);
        }
        if (fault == "removed") f.Files.RemoveLatest("attacker", "colony-a");
        if (fault == "source") f.State.Ledger.ChangeStatus(f.Raid.EventId, ServerEventStatus.Cancelled);
        if (fault == "support-source") f.State.Ledger.ChangeStatus(f.Support.EventId, ServerEventStatus.Cancelled);
        AuthoritativeEvent? advanced = null;
        if (fault == "advanced")
        {
            string id = intent.RaidCleanup!.Batch.Events.First(item => item.Type == ServerEventType.SupportPawn).EventId;
            f.State.Ledger.MarkDelivered(id, "later", f.Now.AddHours(1));
            advanced = f.State.Ledger.MarkApplied(id, "later", f.Now.AddHours(2));
        }
        f.Store.FailPublication = false; f.Store.FailAfterPublication = false;
        if (reopen) f.Open();
        var latest = f.Files.GetLatest("attacker", "colony-a")?.Envelope;
        if (conflict)
        {
            Fails(() => f.State.SnapshotMutations.Recover(), "Conflicting cleanup was recovered automatically.");
            Check(File.Exists(path) && f.Files.GetLatest("attacker", "colony-a")?.Envelope == latest, "Conflict lost its journal or overwritten snapshot.");
            return;
        }
        f.State.SnapshotMutations.Recover();
        f.InvokeCleanup();
        Check(!Directory.EnumerateFiles(f.Journal, "*.json").Any() && f.State.Ledger.ListAll().Count == 4
            && f.State.Ledger.Find(f.Support.EventId)!.Status == ServerEventStatus.Failed
            && f.Files.GetLatestPackage("attacker", "colony-a")!.Payload.SequenceEqual(intent.Package.Payload)
            && f.Files.GetLatest("attacker", "colony-a")!.Envelope == intent.Package.Envelope
            && f.State.Players.FindByUserId("attacker")!.CurrentSnapshotId == intent.Package.Envelope.Identity.SnapshotId,
            "Cleanup recovery lost or duplicated snapshot/reference/notifications.");
        if (advanced is not null) Check(JsonSerializer.Serialize(f.State.Ledger.Find(advanced.EventId)) == JsonSerializer.Serialize(advanced),
            "Cleanup recovery reset later support notification application.");
        f.Open();
        Check(f.State.Ledger.ListAll().Count == 4 && f.State.Players.FindByUserId("attacker")!.CurrentSnapshotId == intent.Package.Envelope.Identity.SnapshotId,
            "Recovered cleanup did not persist.");
    }

    private static void Guard(RaidCleanupFixture f, string change)
    {
        if (change == "owner") f.Sql("update events set actor_user_id = 'other' where event_id = 'raid-timeout-001';");
        else
        {
            var package = f.Original with { Index = f.Original.Index with { WorldObjects = f.Original.Index.WorldObjects
                .Select(item => item.Def == "ClashOfRim_RemoteRaidBattleMapParent" ? item with { ClashOfRimRelatedEventId = "new-raid" } : item).ToArray() } };
            f.Store.ReadOverride = package;
        }
        f.RefreshRaid();
        f.InvokeCleanup();
        Check(f.Store.WriteAttempts == 0 && f.State.Ledger.ListAll().Count == 2 && !Directory.EnumerateFiles(f.Journal, "*.json").Any(),
            "Cleanup targeted a different owner or raid battle.");
    }


    private static void Fails(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or SqliteException) { return; }
        throw new Exception(message);
    }
}
