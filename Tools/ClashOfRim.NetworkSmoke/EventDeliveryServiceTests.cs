using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Events;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class EventDeliveryServiceTests
{
    internal static async Task RunAsync(Func<string, string, string, SaveSnapshotPackage> package)
    {
        ReadAndDeliver(package);
        RecoveryRevalidation(package);
        foreach (string change in new[] { "session", "snapshot", "cancel", "signal" }) await WaitRevalidation(package, change);
        Console.WriteLine("PASS: independent event delivery (queue isolation, batch delivery, recovery revalidation and lock-free polling)");
    }

    private static void ReadAndDeliver(Func<string, string, string, SaveSnapshotPackage> package)
    {
        var f = new Fixture(package);
        var gift = f.Gift("gift");
        var historical = gift with { EventId = "account-wide", IdempotencyKey = "account-wide", Target = new("reader") };
        var hidden = gift with { EventId = "retired", IdempotencyKey = "retired", Target = new("reader", "retired") };
        f.Ledger.Append(gift); f.Ledger.Append(historical); f.Ledger.Append(hidden);
        var queue = f.Delivery.PullPendingEvents(new("reader", "home", "current", f.Token));
        Require(queue.Result.Accepted && QueueIds(queue.EventQueue).ToHashSet().SetEquals([gift.EventId, historical.EventId]),
            "Queue lost account-wide events or leaked another colony.");
        using (f.Mutations.EnterUnrecovered())
            Require(QueueIds(f.Delivery.ReadQueue("reader", "home")).SequenceEqual(QueueIds(queue.EventQueue)), "Login and polling queue projections differ.");
        foreach (string invalidId in new[] { hidden.EventId, "missing" })
        {
            var denied = f.Delivery.PullEventDetails(new("reader", "home", "current", [gift.EventId, invalidId], f.Token));
            Require(!denied.Result.Accepted && denied.Events.Count == 0 && f.Ledger.Find(gift.EventId) == gift,
                "Invalid batch delivered an earlier event.");
        }
        var response = f.Delivery.PullEventDetails(new("reader", "home", "current", [gift.EventId, gift.EventId], f.Token));
        var delivered = f.Ledger.Find(gift.EventId)!;
        Require(response.Result.Accepted && response.Events.Count == 1 && delivered.DeliveredToSnapshotId == "current"
            && delivered.Status == ServerEventStatus.DeliveredToClient, "Delivery did not bind exactly once to the current baseline.");
        f.Delivery.PullEventDetails(new("reader", "home", "current", [gift.EventId], f.Token));
        Require(f.Ledger.Find(gift.EventId) == delivered, "Same-baseline repeat rewrote delivery.");

        var expired = f.Gift("expired") with
        {
            Status = ServerEventStatus.DeliveredToClient, DeliveredToSnapshotId = "current",
            DeliveredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3)
        };
        f.Ledger.Append(expired);
        int replays = f.Recovery.Replays;
        var invalid = f.Delivery.PullPendingEvents(new("reader", "home", "stale", f.Token));
        Require(!invalid.Result.Accepted && f.Recovery.Replays == replays && f.Ledger.Find(expired.EventId) == expired,
            "Denied read started recovery or lease maintenance.");
        f.Delivery.PullPendingEvents(new("reader", "home", "current", f.Token));
        Require(f.Ledger.Find(expired.EventId)!.Status == ServerEventStatus.PendingOfflineDelivery, "Expired offline delivery was not requeued.");
    }

    private static void RecoveryRevalidation(Func<string, string, string, SaveSnapshotPackage> package)
    {
        var f = new Fixture(package);
        var gift = f.Gift("recovery");
        f.Ledger.Append(gift);
        f.Recovery.AfterReplay = () => f.Sessions.EndUser("reader");
        var result = f.Delivery.PullEventDetails(new("reader", "home", "current", [gift.EventId], f.Token));
        Require(!result.Result.Accepted && result.Events.Count == 0 && f.Ledger.Find(gift.EventId) == gift,
            "Recovery revoked session but event delivery still published.");
    }

    private static async Task WaitRevalidation(Func<string, string, string, SaveSnapshotPackage> package, string change)
    {
        var f = new Fixture(package);
        var gift = f.Gift("wait");
        f.Ledger.Append(gift);
        using var cancellation = new CancellationTokenSource();
        Task<WaitForEventsResponse> waiting = f.Delivery.WaitForEvents(new("reader", "home", "current", 0, 30, f.Token), cancellation.Token);
        Require(!waiting.IsCompleted, "Long poll did not wait.");
        Require(await Task.Run(() =>
        {
            if (!Monitor.TryEnter(f.Gate, TimeSpan.FromSeconds(3))) return false;
            try
            {
                if (change == "session") f.Sessions.EndUser("reader");
                if (change == "snapshot") f.PublishSnapshot("newer");
                return true;
            }
            finally { Monitor.Exit(f.Gate); }
        }), "Long poll held the mutation gate while awaiting notification.");

        if (change == "cancel")
        {
            cancellation.Cancel();
            bool canceled = false;
            try { await waiting.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { canceled = true; }
            Require(canceled && f.Recovery.Replays == 0, "Cancelled wait recovered or swallowed cancellation.");
        }
        else
        {
            f.Notifications.SignalUser("reader");
            var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Require(result.Result.Accepted == (change == "signal"), "Wait did not revalidate the changed session/baseline: " + change);
            Require(change == "signal" ? result.Changed && QueueIds(result.EventQueue).Single() == gift.EventId : !QueueIds(result.EventQueue).Any(),
                "Wait returned incorrect visibility after wakeup: " + change);
        }
        Require(f.Ledger.Find(gift.EventId) == gift, "Queue polling delivered an event implicitly.");
    }

    private sealed class Fixture
    {
        internal readonly object Gate = new();
        internal readonly LoginSessionRegistry Sessions = new();
        internal readonly InMemoryAuthoritativeEventLedger Ledger = new();
        internal readonly EventNotificationHub Notifications = new();
        internal readonly RecoveryProbe Recovery = new();
        internal readonly SnapshotMutationCoordinator Mutations;
        internal readonly EventDeliveryService Delivery;
        internal readonly string Token;
        private readonly InMemoryColonySnapshotIndexStore snapshots = new();
        private readonly Func<string, string, string, SaveSnapshotPackage> package;

        internal Fixture(Func<string, string, string, SaveSnapshotPackage> package)
        {
            this.package = package;
            var tokens = new AuthTokenRegistry();
            var authorization = new ColonyAccessAuthorization(tokens, Sessions, new DiplomacyRelationRegistry(), new RaidPreparationRegistry(), Ledger, () => true);
            Mutations = new(Gate, Recovery);
            Delivery = new(authorization, Mutations, snapshots, Ledger, Notifications, new OnlinePresenceRegistry(), new PlayerRegistry());
            PublishSnapshot("current");
            Sessions.TryCreate("reader", "home", DateTimeOffset.UtcNow, out string session);
            Token = tokens.Issue("reader", "reader", "home", session, DateTimeOffset.UtcNow);
        }

        internal void PublishSnapshot(string id)
        {
            var snapshot = package("reader", "home", id);
            snapshots.StoreLatest(new(snapshot.Envelope.Identity, snapshot.Envelope, snapshot.Index, DateTimeOffset.UtcNow));
        }

        internal AuthoritativeEvent Gift(string key) => AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery,
            new("sender", "away"), new("reader", "home"), key, false, new ItemDeliveryEventPayload([new("wood", "WoodLog", 1)], null), DateTimeOffset.UtcNow);
    }

    // No pending snapshot work, with an optional replay-side session change.
    private sealed class RecoveryProbe : ISnapshotRecovery, ISnapshotRecoveryPreparation
    {
        internal Action? AfterReplay;
        internal int Replays;
        public void RecoverPrerequisites() { }
        public ISnapshotRecoveryPreparation CapturePreparation() => this;
        public void Replay() { Replays++; AfterReplay?.Invoke(); }
        public bool NeedsPreparation => false;
        public bool IsCurrent => throw new InvalidOperationException("No work to prepare.");
        public void Prepare() => throw new InvalidOperationException("No work to prepare.");
        public bool TryInstall() => throw new InvalidOperationException("No work to prepare.");
    }

    private static IEnumerable<string> QueueIds(EventQueueSummaryDto queue) => queue.DirectlyProcessable.Concat(queue.WaitingForUserChoice)
        .Concat(queue.DeliveredUnconfirmed).Concat(queue.Conflicts).Concat(queue.Rejected).Select(item => item.EventId);
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
