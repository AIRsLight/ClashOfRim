using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

internal static class ClientSnapshotTransactionNotificationTests
{
    internal static void Run()
    {
        try
        {
            AdmissionNotifications();
            ReplacedTransaction();
            foreach (string boundary in new[] { "log", "translate", "enqueue", "queued" })
            foreach (string change in new[] { "user", "server", "colony", "auth", "login", "generation", "game", "reset", "snapshot", "password" })
                Ownership(boundary, change);
            foreach (bool playing in new[] { false, true })
            {
                var f = new Fixture { Playing = playing };
                using var lease = f.Start();
                f.Pump();
                Check(f.Messages.Count == (playing ? 1 : 0), "Entry state showed an in-game message.");
            }
            var empty = new Fixture { Text = " " };
            using var ignored = empty.Start();
            Check(empty.Pending.Count == 0 && empty.Notices.Count == 1, "Empty message was queued or its diagnostic was lost.");
            Console.WriteLine("Snapshot notifications checked admission, completion, replacement and 40 ownership boundaries.");
        }
        finally { ClientSessionWork.Reset(); }
    }

    private static void AdmissionNotifications()
    {
        foreach (bool promote in new[] { false, true })
        {
            var f = new Fixture();
            ClientSynchronizationCoordinator.Lease? manual = null;
            if (promote)
            {
                Check(f.Sync.TryBeginManual(out manual), "Manual setup failed.");
                Check(!f.Sync.TryAcquireSnapshot(out _) && f.Pending.Count == 0,
                    "Rejected snapshot queued an uploading message.");
            }
            using var lease = f.Start(manual);
            manual?.Dispose();
            Check(f.Sync.TryAcquireSnapshot(out var continued, lease) && ReferenceEquals(continued, lease), "Continuation failed.");
            Check(!f.Sync.TryAcquireSnapshot(out _) && f.Pending.Count == 1, "Rejected/continued snapshot added another message.");
            f.Ports.Snapshot = "accepted";
            f.Text = "later language";
            f.Pump();
            Check(f.Messages.SequenceEqual(new[] { "uploading" }), "Snapshot progression revoked the active upload or queued text was reread.");
            lease.Dispose();
            Check(f.Notices.Count(n => n.Phase == SnapshotTransactionPhase.Started) == 1
                && f.Notices.Count(n => n.Phase == SnapshotTransactionPhase.Ended) == 1,
                "Start/end diagnostics were duplicated or lost.");
        }
        var completed = new Fixture();
        completed.Start().Dispose();
        completed.Pump();
        Check(completed.Messages.Count == 0 && completed.Notices.Last().Phase == SnapshotTransactionPhase.Ended,
            "Finished upload still announced uploading.");
    }

    private static void ReplacedTransaction()
    {
        var f = new Fixture();
        using var first = f.Start();
        var original = f.Notices.Single();
        Check(f.Sync.IsCurrent(original), "Started transaction is not current.");
        f.Sync.Reset();
        using var second = f.Start();
        first.Dispose();
        Check(!f.Sync.IsCurrent(original) && f.Sync.IsCurrent(f.Notices.Last()) && second.IsCurrent,
            "Old transaction matched or released its successor.");
        f.Pump();
        Check(f.Messages.Count == 1, "Replaced upload notified the successor game twice.");
        second.Dispose();
        Check(!f.Sync.IsCurrent(f.Notices.Last()), "Ended notice was treated as a running upload.");
    }

    private static void Ownership(string boundary, string change)
    {
        var f = new Fixture();
        void Change()
        {
            switch (change)
            {
                case "user": f.Ports.User = "bob"; break;
                case "server": f.Ports.Server = "http://other"; break;
                case "colony": f.Ports.Colony = "other"; break;
                case "auth": f.Ports.Auth = "other"; break;
                case "login": f.Session.PublishLogin(new() { SessionId = "same-credentials", AuthToken = "auth" }); break;
                case "generation": ClientSessionWork.Reset(); break;
                case "game": f.Ports.GameOwner = new(); break;
                case "reset": f.Sync.Reset(); break;
                case "snapshot": f.Ports.Snapshot = "accepted"; break;
                case "password": f.Ports.Password = "changed"; break;
            }
        }
        f.Hook = at => { if (at == boundary) { f.Hook = null; Change(); } };
        using var lease = f.Start();
        if (boundary == "queued") Change();
        bool expected = change is "snapshot" or "password";
        if (!expected && boundary is "log" or "translate")
            Check(f.Pending.Count == 0, "Stale notification remained queued after an early ownership change.");
        f.Pump();
        Check(f.Messages.Count == (expected ? 1 : 0), "Notification ownership mismatch: " + boundary + "/" + change);
        Check(f.Notices.Any(n => n.Phase == SnapshotTransactionPhase.Started), "Ownership changes erased transaction diagnostics.");
    }

    private sealed class Fixture : IClientSnapshotTransactionPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly Queue<Action> Pending = new();
        internal readonly List<SnapshotTransactionNotice> Notices = new();
        internal readonly List<string> Messages = new();
        internal Action<string>? Hook;
        internal string Text = "uploading";
        public bool Playing { get; set; } = true;
        internal Fixture()
        {
            ClientSessionWork.Reset();
            Session = new(Ports, Ports, new ClientSessionConnection(Ports));
            var module = new ClientSnapshotTransactionNotifications(Session, Sync, Ports, this);
            Sync.SnapshotChanged += module.Observe;
        }
        internal ClientSynchronizationCoordinator.Lease Start(ClientSynchronizationCoordinator.Lease? previous = null)
        {
            Check(Sync.TryAcquireSnapshot(out var lease, previous), "Fixture upload admission failed.");
            return lease!;
        }
        internal void Pump() { while (Pending.TryDequeue(out var action)) action(); }
        public void Log(SnapshotTransactionNotice notice) { Notices.Add(notice); Hook?.Invoke("log"); }
        public string Uploading { get { Hook?.Invoke("translate"); return Text; } }
        // Leave this queue unguarded so the module, rather than a fake dispatcher, proves ownership.
        public void Enqueue(Action action, Action discarded) { Hook?.Invoke("enqueue"); Pending.Enqueue(action); }
        public void Message(string message) => Messages.Add(message);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
