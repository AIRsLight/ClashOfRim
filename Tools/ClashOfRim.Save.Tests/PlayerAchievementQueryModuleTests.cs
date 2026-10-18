using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Achievements;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class PlayerAchievementQueryModuleTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        int count = 0; var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (bool achievements in new[] { false, true }) { QueryIdentity(achievements); Inputs(achievements); count += 2; }
                CachedColony(factory); WealthFailure(factory); WealthCache(factory); Rankings(); DelimiterIdentity(factory); count += 5;
                foreach (string mode in new[] { "exact", "fallback", "missing", "missing-id", "unregistered", "deleted", "rebuilt" })
                { Visibility(factory, mode); count++; }
                foreach (bool achievements in new[] { false, true }) { QueuedDeletion(achievements); count++; }
            }
        }
        finally { CultureInfo.CurrentCulture = original; }
        foreach (bool achievements in new[] { false, true }) { HttpIsolation(factory, achievements); count++; }
        Console.WriteLine($"PASS: player/achievement query modules ({count} scenarios)");
    }

    private static void WealthFailure(Func<string, SaveSnapshotPackage> factory)
    {
        var slot = new RecoveryTestSupport.Slot();
        var state = new Fixture(new PlayerRegistry(slot));
        state.Players.RecordLatestSnapshotWealth("owner", "colony", "old", 1, DateTimeOffset.UnixEpoch);
        var package = factory("fresh");
        package = package with { Envelope = package.Envelope with { Identity = new("owner", "colony", "fresh") } };
        state.S.Snapshots.StoreLatest(new(package.Envelope.Identity, package.Envelope, package.Index, DateTimeOffset.UnixEpoch));
        var before = state.Players.FindByUserId("owner"); slot.Fail = true;
        bool failed = false; try { Players(state, new("viewer", "view", null)); } catch (IOException) { failed = true; }
        Require(failed && state.Players.FindByUserId("owner") == before, "Failed wealth cache save changed the published player record.");
        Require(new PlayerRegistry(slot).FindByUserId("owner") == before, "Failed cache save changed the reopened record.");
        slot.Fail = false;
        var result = Players(state, new("viewer", "view", null)).Players.Single();
        Require(result.CurrentSnapshotId == "fresh" && result.LastSeenAtUtc == DateTimeOffset.UnixEpoch
            && new PlayerRegistry(slot).FindByUserId("owner")!.LatestSnapshotWealthSnapshotId == "fresh", "Wealth retry lost cache/source activity.");
    }

    private static void QueryIdentity(bool achievements)
    {
        var state = new Fixture();
        state.Players.Record("victim", "current", "actual", DateTimeOffset.UnixEpoch, "Original");
        if (achievements) Achievements(state, new("victim", "forged", "invented"));
        else Players(state, new("victim", "forged", "invented"));
        var player = state.Players.FindByUserId("victim")!;
        Require(player.ColonyId == "current" && player.CurrentSnapshotId == "actual" && player.LastSeenAtUtc == DateTimeOffset.UnixEpoch,
            $"Public query achievements={achievements} rewrote the caller-supplied account identity/activity.");
        if (achievements) Achievements(state, new("unknown", "invented", "fake")); else Players(state, new("unknown", "invented", "fake"));
        Require(!state.Players.ContainsUser("unknown"), "Public query registered an unknown account.");
    }

    private static void CachedColony(Func<string, SaveSnapshotPackage> factory)
    {
        var state = new Fixture();
        state.Players.RecordLatestSnapshotWealth("owner", "old", "same", 1, DateTimeOffset.UnixEpoch);
        var package = factory("same");
        package = package with { Envelope = package.Envelope with { Identity = new("owner", "new", "same") } };
        state.S.Snapshots.StoreLatest(new(package.Envelope.Identity, package.Envelope, package.Index, DateTimeOffset.UnixEpoch));
        var player = Players(state, new("viewer", "view", null)).Players.Single(p => p.UserId == "owner");
        Require(player.ColonyId == "new", "Wealth cache reused a snapshot ID belonging to a different colony.");
    }

    private static void Inputs(bool achievements)
    {
        var f = new Fixture(); f.S.PlayerSlot.Fail = true;
        foreach (var identity in new[] { ("", "colony"), ("user", " ") })
        {
            if (achievements)
            {
                var response = Achievements(f, new(identity.Item1, identity.Item2, "arbitrary"));
                Require(response.Result.ErrorCode == ProtocolErrorCode.ValidationFailed && response.Leaderboards.Count == 0 && response.OwnAchievements.Count == 0,
                    "Invalid achievement query returned data.");
            }
            else
            {
                var response = Players(f, new(identity.Item1, identity.Item2, "arbitrary"));
                Require(response.Result.ErrorCode == ProtocolErrorCode.ValidationFailed && response.Players.Count == 0, "Invalid directory query returned data.");
            }
        }
    }

    private static void WealthCache(Func<string, SaveSnapshotPackage> factory)
    {
        var f = new Fixture();
        f.Players.RecordLatestSnapshotWealth("owner", "current", "cached", 17, DateTimeOffset.UnixEpoch);
        Snapshot(f, factory, "owner", "current", "cached", 99, DateTimeOffset.UnixEpoch);
        f.S.PlayerSlot.Fail = true;
        Require(Players(f, new("viewer", "view", null)).Players.Single().LatestSnapshotWealth == 17, "Matching wealth cache was recomputed or rewritten.");
        f.S.PlayerSlot.Fail = false;
        Snapshot(f, factory, "owner", "current", "fresh", 99, DateTimeOffset.UnixEpoch);
        Require(Players(f, new("viewer", "view", null)).Players.Single().LatestSnapshotWealth == 99, "Changed snapshot retained obsolete wealth.");
        f.S.Snapshots.RemoveLatest("owner", "current"); f.S.PlayerSlot.Fail = true;
        var cached = Players(f, new("viewer", "view", null)).Players.Single();
        Require(cached.CurrentSnapshotId == "fresh" && cached.LatestSnapshotWealth == 99, "Unavailable snapshot lost the trusted cached directory record.");
        f.S.PlayerSlot.Fail = false;
        var package = factory("unknown-wealth"); var identity = new SnapshotIdentity("owner", "current", "unknown-wealth");
        f.S.Snapshots.StoreLatest(new(identity, package.Envelope with { Identity = identity }, package.Index with { Maps = [] }, DateTimeOffset.UnixEpoch));
        Require(Players(f, new("viewer", "view", null)).Players.Single().LatestSnapshotWealth is null, "Missing wealth invented a numeric total.");
        f.S.PlayerSlot.Fail = true;
        Require(Players(f, new("viewer", "view", null)).Players.Single().LatestSnapshotWealth is null, "Known-null wealth was repeatedly persisted.");
    }

    private static void Visibility(Func<string, SaveSnapshotPackage> factory, string mode)
    {
        var f = new Fixture();
        f.Players.Record("owner", "current", mode == "missing" ? null : "reported", DateTimeOffset.UnixEpoch, "名称 İ العربية");
        if (mode == "unregistered") f.Players.Remove("owner");
        if (mode is "exact" or "fallback" or "deleted" or "rebuilt" or "unregistered")
            Snapshot(f, factory, "owner", "old", "latest-old", 12, DateTimeOffset.UnixEpoch.AddDays(2));
        if (mode == "exact") Snapshot(f, factory, "owner", "current", "exact", 23, DateTimeOffset.UnixEpoch);
        if (mode == "missing-id")
        {
            var package = factory("missing-id"); var identity = new SnapshotIdentity("owner", "current", null);
            f.S.Snapshots.StoreLatest(new(identity, package.Envelope with { Identity = identity }, package.Index, DateTimeOffset.UnixEpoch));
        }
        if (mode == "deleted") f.Players.MarkDeleted("owner", "current", "reported", DateTimeOffset.UnixEpoch);
        if (mode == "rebuilt")
        {
            f.Players.MarkDeleted("owner", "old", "latest-old", DateTimeOffset.UnixEpoch);
            f.Players.Record("owner", "current", null, DateTimeOffset.UnixEpoch);
        }
        foreach (string colony in new[] { "old", "current" }) Award(f, "owner", colony, "badge", 10);
        string? expected = mode is "missing" or "missing-id" or "deleted" or "rebuilt" or "unregistered" ? null : mode == "fallback" ? "old" : "current";
        var ranking = Achievements(f, new("viewer", "view", null));
        var directory = Players(f, new("viewer", "view", null));
        if (expected is null)
            Require(directory.Players.Count == 0 && ranking.Leaderboards.Count == 0, "Invisible colony leaked through a list: " + mode);
        else
        {
            var row = directory.Players.Single(); var entry = ranking.Leaderboards.Single().Entries.Single();
            Require(row.ColonyId == expected && entry.ColonyId == expected && row.DisplayName == "名称 İ العربية" && entry.DisplayName == row.DisplayName,
                "Directory and leaderboard chose different colony metadata: " + mode);
            if (mode is "exact" or "fallback")
                Require(row.LatestSnapshotWealth == (mode == "exact" ? 23 : 12), "Wrong resolved snapshot wealth: " + mode);
            Require(row.LastSeenAtUtc == DateTimeOffset.UnixEpoch && f.Players.FindByUserId("owner")!.LastSeenAtUtc == DateTimeOffset.UnixEpoch,
                "Reading wealth marked an inactive account as recently seen.");
        }
    }

    private static void Rankings()
    {
        var f = new Fixture();
        string[] users = ["z", "a", "İ", "I", "ı", "i", "é", "e", "中", "b", "c", "d"];
        for (int i = 0; i < users.Length; i++)
        {
            f.Players.Record(users[i], "colony", "current", DateTimeOffset.UnixEpoch, "name-" + users[i]);
            Award(f, users[i], "colony", "badge", i / 3, "Snapshot", "Label");
        }
        Award(f, "a", "colony", "event-badge", 5, "Event", " ");
        Award(f, "a", "old", "badge", 999); Award(f, "missing", "colony", "badge", 1000);
        string before = JsonSerializer.Serialize(f.Awards.ListAggregates()); f.S.PlayerSlot.Fail = true;
        var response = Achievements(f, new("a", "colony", "invented", " a ", " colony "));
        var board = response.Leaderboards.Single(b => b.AchievementId == "badge");
        string[] expected = users.Select((user, i) => (user, value: i / 3)).OrderByDescending(p => p.value).ThenBy(p => p.user, StringComparer.Ordinal).Take(10).Select(p => p.user).ToArray();
        Require(board.Entries.Select(e => e.UserId).SequenceEqual(expected) && board.Entries.All(e => e.DisplayName == "name-" + e.UserId && e.SourceSnapshotId == "award-source"),
            "Top ten, tie order or source metadata changed across culture.");
        Require(response.Leaderboards.Select(b => b.Category).SequenceEqual(new[] { "Event", "Snapshot" })
            && response.OwnAchievements.Select(a => a.AchievementId).SequenceEqual(new[] { "event-badge", "badge" })
            && board.DescriptionKey == "Label.Description" && board.IconId == "test-icon" && board.Color == AchievementColors.Purple,
            "Leaderboard/category metadata or target sorting changed.");
        Require(response.OwnAchievements[0].DescriptionKey == "ClashOfRim.Achievement.event-badge.Description"
            && Achievements(f, new("a", "colony", null)).OwnAchievements.Count == 2
            && Achievements(f, new("a", "colony", null, "A", "colony")).OwnAchievements.Count == 0
            && Achievements(f, new("a", "colony", null, "a", "old")).OwnAchievements.Count == 0,
            "Default/trimmed/case-sensitive target or hidden-colony filtering changed.");
        Require(JsonSerializer.Serialize(f.Awards.ListAggregates()) == before, "Ranking query changed historical achievements.");
        Require(f.S.Presence.TryConnectExclusive("a", out var presence), "Cannot create online player fixture.");
        using var lease = presence;
        f.Relations.SetRelationKind("viewer", "view", "a", "colony", DiplomacyRelationRegistry.RelationAlly, null, DateTimeOffset.UnixEpoch);
        f.S.PlayerSlot.Fail = false;
        var row = Players(f, new("viewer", "view", null)).Players.Single(p => p.UserId == "a");
        Require(row.Online && row.RelationKind == DiplomacyRelationRegistry.RelationAlly, "Directory lost online/diplomacy projection.");
    }

    private static void DelimiterIdentity(Func<string, SaveSnapshotPackage> factory)
    {
        var f = new Fixture();
        var snapshots = new List<LatestSnapshotRecord>();
        foreach (var pair in new[] { ("u\u001fx", "c"), ("u", "x\u001fc") })
        {
            f.Players.Record(pair.Item1, pair.Item2, "same", DateTimeOffset.UnixEpoch);
            var package = factory("same"); var identity = new SnapshotIdentity(pair.Item1, pair.Item2, "same");
            snapshots.Add(new(identity, package.Envelope with { Identity = identity }, package.Index, DateTimeOffset.UnixEpoch));
            Award(f, pair.Item1, pair.Item2, "badge", pair.Item1.Length);
        }
        var lookup = LatestSnapshotLookup.Build(snapshots);
        Require(snapshots.All(snapshot => lookup.Resolve(snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!) == snapshot), "Snapshot lookup conflated delimiter identities.");
        var rows = Players(f, new("viewer", "view", null)).Players;
        Require(rows.Count == 2, "Directory conflated distinct registered players.");
        f.Players.Remove("u");
        var board = Achievements(f, new("viewer", "view", null)).Leaderboards.Single();
        Require(board.Entries.Single().UserId == "u\u001fx", "Leaderboard visibility conflated delimiter identities.");
    }

    private static void QueuedDeletion(bool achievements)
    {
        var f = new Fixture(); f.Players.Record("owner", "colony", "reported", DateTimeOffset.UnixEpoch); Award(f, "owner", "colony", "badge", 1);
        using var started = new ManualResetEventSlim(); Task<int> request;
        lock (f.S.Gate)
        {
            request = Task.Run(() => { started.Set(); return achievements ? Achievements(f, new("viewer", "view", null)).Leaderboards.Count : Players(f, new("viewer", "view", null)).Players.Count; });
            Require(started.Wait(TimeSpan.FromSeconds(2)), "Query did not start.");
            f.Players.MarkDeleted("owner", "colony", "reported", DateTimeOffset.UnixEpoch);
        }
        Require(request.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult() == 0, "Queued query published a retired colony.");
    }

    private static void Snapshot(Fixture f, Func<string, SaveSnapshotPackage> factory, string user, string colony, string id, int wealth, DateTimeOffset at)
    {
        var package = factory(id); var identity = new SnapshotIdentity(user, colony, id);
        var index = package.Index with { Maps = [new MapSummary("0", "0", "parent", "(250, 1, 250)", false, false, false, false, 0, 0, WealthTotal: wealth)] };
        f.S.Snapshots.StoreLatest(new(identity, package.Envelope with { Identity = identity }, index, at));
    }

    private static void Award(Fixture f, string user, string colony, string id, long value, string category = "Snapshot", string label = "Label")
        => f.Awards.Record(user, colony, "award-source", new(id, "event", value, category: category, labelKey: label, iconId: "test-icon", aggregationKind: AchievementRegistry.AggregationMax, color: AchievementColors.Purple), DateTimeOffset.UnixEpoch);

    private sealed class Fixture
    {
        internal readonly SessionModuleTestContext S = new();
        internal readonly AchievementRegistry Awards = new();
        internal readonly DiplomacyRelationRegistry Relations = new();
        internal readonly PlayerRegistry Players;
        internal readonly PlayerDirectory Directory;
        internal readonly AchievementQueries Queries;
        internal Fixture(PlayerRegistry? players = null)
        {
            Players = players ?? S.Players; var catalog = new PlayerColonyCatalog(Players, S.Snapshots);
            Directory = new(catalog, new(Players, S.Snapshots), Relations, S.Presence, S.Mutations); Queries = new(Awards, catalog, S.Mutations);
            S.Recovery.OnPrerequisites = S.Recovery.OnCapture = S.Recovery.OnPrepare = S.Recovery.OnReplay = () => throw new Exception("Public query started snapshot recovery.");
        }
    }

    private static void HttpIsolation(Func<string, SaveSnapshotPackage> factory, bool achievements)
    {
        using var f = new HttpSnapshotRecoveryTests.Fixture(factory); f.Start(); f.Seed(); string before = f.Evidence();
        using var response = achievements
            ? f.Post(ProtocolMessageKind.ListAchievements, new ListAchievementsRequest("viewer", "view", null))
            : f.Post(ProtocolMessageKind.ListPlayers, new ListPlayersRequest("viewer", "view", null));
        response.EnsureSuccessStatusCode();
        bool accepted = achievements
            ? response.Content.ReadFromJsonAsync<ListAchievementsResponse>().GetAwaiter().GetResult()!.Result.Accepted
            : response.Content.ReadFromJsonAsync<ListPlayersResponse>().GetAwaiter().GetResult()!.Result.Accepted;
        Require(accepted && !f.State.Players.ContainsUser("viewer"), "Public query changed its accepted contract or registered a viewer.");
        f.AssertPending(before, "Public query achievements=" + achievements);
    }

    private static ListPlayersResponse Players(Fixture state, ListPlayersRequest request) => state.Directory.List(request);
    private static ListAchievementsResponse Achievements(Fixture state, ListAchievementsRequest request) => state.Queries.List(request);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
