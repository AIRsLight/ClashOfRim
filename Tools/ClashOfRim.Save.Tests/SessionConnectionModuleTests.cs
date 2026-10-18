using System.Globalization;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class SessionConnectionModuleTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var saved = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (bool connected in new[] { false, true }) Lifecycle(connected);
                foreach (bool replace in new[] { false, true }) FailedOpen(replace);
                foreach (string entry in new[] { "open", "logout", "close" }) FailedRecovery(entry);
                SnapshotSelection(factory);
            }
            DetachedTimeoutPreparation();
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    private static void Lifecycle(bool connected)
    {
        var f = new SessionModuleTestContext();
        Check(f.Presence.TryConnectExclusive("observer", "observer-session", out var observerLease), "Observer could not connect.");
        using var observer = observerLease;
        var owner = f.Issue();
        f.Loans.Create("old", "user", "colony", "base", 100, 10, 7, 1000, DateTimeOffset.UtcNow.AddHours(-1));
        f.Loans.Create("other", "user", "other-colony", "base", 100, 10, 7, 1000, DateTimeOffset.UtcNow.AddHours(-1));
        foreach (var request in new[] { new LogoutRequest("", "colony", owner.Session), new LogoutRequest("user", "", owner.Session),
            new LogoutRequest("user", "colony", null), new LogoutRequest("user", "other-colony", owner.Session),
            new LogoutRequest("other", "colony", owner.Session), new LogoutRequest("user", "colony", "forged") })
            Check(!f.Connections.Logout(request).Result.Accepted, "Invalid owner was accepted.");
        Check(f.Recovery.Replays == 0 && f.PlayerSlot.Writes == 0 && f.Valid(owner), "Unauthorized logout ran recovery or changed state.");
        OnlinePresenceLease? lease = null;
        if (connected)
        {
            Check(f.Connections.Open("user", "colony", owner.Session, "reported", out lease) is null && lease!.IsActive(), "Valid stream did not open.");
            Check(f.Players.FindByUserId("user")!.CurrentSnapshotId == "reported" && f.Notifications.GetVersion("observer") == 1,
                "Opening a stream omitted activity or online notification.");
            int replays = f.Recovery.Replays;
            Check(f.Connections.Open("user", "colony", owner.Session, null, out _) is not null
                && f.Recovery.Replays == replays && lease!.IsActive(), "Duplicate open recovered or displaced its owner.");
        }
        var result = f.Connections.Logout(new("user", "colony", owner.Session));
        Check(result.Result.Accepted && result.Disconnected == connected && f.Retired(owner)
            && !f.Presence.IsUserOnline("user") && f.Loans.FindByIdempotencyKey("old") is null
            && f.Loans.FindByIdempotencyKey("other") is not null, "Logout crossed colony boundaries or left its own resources.");
        var next = f.Issue();
        f.Loans.Create("new", "user", "colony", "base", 100, 10, 7, 1000, DateTimeOffset.UtcNow);
        Check(f.Connections.Open("user", "colony", next.Session, "new-base", out var nextLease) is null, "Replacement stream failed.");
        int before = f.Recovery.Replays;
        f.Connections.Close("user", "colony", owner.Session, lease);
        Check(f.Valid(next) && nextLease!.IsActive() && f.Recovery.Replays == before
            && f.Loans.FindByIdempotencyKey("new") is not null, "Old cleanup touched the replacement or started recovery.");
        f.Connections.Close("user", "colony", next.Session, nextLease);
        Check(f.Retired(next) && f.Loans.FindByIdempotencyKey("new") is not null, "Disconnect retained resources or force-cancelled a fresh confirmation.");
    }

    private static void FailedOpen(bool replace)
    {
        var f = new SessionModuleTestContext();
        var owner = f.Issue();
        Check(f.Presence.TryConnectExclusive("observer", "observer-session", out var observerLease), "Observer could not connect.");
        using var observer = observerLease;
        (string Session, string Token) next = default;
        OnlinePresenceLease? nextLease = null;
        if (replace) f.PlayerSlot.Before = _ =>
        {
            f.PlayerSlot.Before = null;
            f.Sessions.End("user", owner.Session); f.Presence.DisconnectSession("user", owner.Session);
            next = f.Issue();
            Check(f.Sessions.TryBeginStream("user", "colony", next.Session, DateTimeOffset.UtcNow)
                && f.Presence.TryConnectExclusive("user", next.Session, out nextLease), "Could not replace the session during the failed write.");
        };
        f.PlayerSlot.Fail = true;
        Throws(() => f.Connections.Open("user", "colony", owner.Session, "base", out _));
        Check(!f.Tokens.IsValid(owner.Token, "user", "colony", DateTimeOffset.UtcNow)
            && f.Notifications.GetVersion("observer") == 0, "Failed stream leaked its token or notified success.");
        if (replace) Check(f.Valid(next) && nextLease!.IsActive(), "Failed old opening removed a replacement session.");
        else Check(f.Retired(owner) && !f.Presence.IsUserOnline("user"), "Failed opening retained its session or presence.");
        f.PlayerSlot.Fail = false;
        nextLease?.Dispose();
    }

    private static void FailedRecovery(string entry)
    {
        var f = new SessionModuleTestContext(); var owner = f.Issue(); OnlinePresenceLease? lease = null;
        if (entry != "open") Check(f.Connections.Open("user", "colony", owner.Session, null, out lease) is null, "Fixture stream failed.");
        f.Recovery.OnPrerequisites = () => throw new IOException("Recovery failed.");
        Throws(() =>
        {
            if (entry == "open") f.Connections.Open("user", "colony", owner.Session, null, out _);
            else if (entry == "logout") f.Connections.Logout(new("user", "colony", owner.Session));
            else f.Connections.Close("user", "colony", owner.Session, lease);
        });
        Check((entry == "close" ? f.Retired(owner) : f.Valid(owner)) && (lease?.IsActive() ?? false) == (entry == "logout"),
            "Recovery failure violated explicit retry or broken-transport cleanup ownership.");
        f.Recovery.OnPrerequisites = null;
        if (entry != "close") Check(f.Connections.Logout(new("user", "colony", owner.Session)).Result.Accepted, "Owner could not retry after recovery failure.");
    }

    private static void DetachedTimeoutPreparation()
    {
        var f = new SessionModuleTestContext(); var owner = f.Issue();
        Check(f.Connections.Open("user", "colony", owner.Session, null, out var lease) is null, "Fixture stream failed.");
        int captures = 0, prepared = 0;
        f.Recovery.OnCapture = () => { if (++captures == 2) f.Recovery.NeedsPreparation = true; };
        f.Recovery.OnPrepare = () =>
        {
            Check(!Monitor.IsEntered(f.Gate) && f.Retired(owner) && !lease!.IsActive(), "Timeout preparation retained the logout gate or began before disconnection.");
            prepared++;
        };
        f.Connections.Close("user", "colony", owner.Session, lease);
        Check(prepared == 1, "Timeout completion did not use independent preparation.");
    }

    private static void SnapshotSelection(Func<string, SaveSnapshotPackage> factory)
    {
        var f = new SessionModuleTestContext(); var package = factory("activity"); var now = DateTimeOffset.UtcNow;
        void Add(string user, string colony, string id, int seconds)
        {
            var envelope = package.Envelope with { Identity = new(user, colony, id) };
            f.Snapshots.StoreLatest(new(envelope.Identity, envelope, package.Index, now.AddSeconds(seconds)));
        }
        Add("user", "exact", "exact", -20); Add("user", "first", "tie-first", -10); Add("user", "second", "tie-second", -10);
        Add("foreign", "first", "foreign-newer", 0);
        var lookup = LatestSnapshotLookup.Build(f.Snapshots.ListLatest());
        Check(lookup.Resolve("user", "exact")!.Identity.SnapshotId == "exact"
            && lookup.Resolve("user", "missing")!.Identity.SnapshotId == "tie-first", "Snapshot selection changed exact, owner or tie precedence.");
        foreach (var pair in new[] { ("exact", "exact"), ("missing", "tie-first") })
        {
            f.Activity.Record("user", pair.Item1, "reported", now, " display ");
            var record = f.Players.FindByUserId("user")!;
            Check(record.ColonyId == pair.Item1 && record.CurrentSnapshotId == pair.Item2 && record.DisplayName == "display", "Activity diverged from lookup or lost identity/display data.");
        }
        foreach (string? hint in new[] { null, " ", "reported" })
        {
            f.Activity.Record("new-user", "new-colony", hint, now);
            Check(f.Players.FindByUserId("new-user")!.CurrentSnapshotId == (hint == "reported" ? hint : null), "No-snapshot fallback changed.");
        }
        f.Players.MarkDeleted("user", "deleted", null, now);
        f.Activity.Record("user", "deleted", "reported", now);
        Check(f.Players.FindByUserId("user")!.ColonyId == "missing", "Activity revived a deleted colony.");
    }

    private static void Throws(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Expected persistence/recovery failure."); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
