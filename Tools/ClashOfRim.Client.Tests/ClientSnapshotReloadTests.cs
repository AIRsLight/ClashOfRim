using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.RemoteMaps;

using Fixture = ClientSnapshotReloadTestPorts;

internal static class ClientSnapshotReloadTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            foreach (bool login in new[] { false, true })
            foreach (var purpose in Enum.GetValues<ClientSnapshotReloadPurpose>())
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                using var f = new Fixture(login); var world = new ModWorldConfigurationDto { StorytellerDefName = "original" };
                var task = f.Module.StartAsync(new("base", purpose, world)); world.StorytellerDefName = "changed";
                await f.Ports.PumpUntil(task);
                Check(f.Completed == 1 && f.Loads == 1 && f.Downloads == 1 && f.Logins == (login ? 1 : 0)
                    && f.Logouts.Count == 0 && !f.Sync.IsBusy && !f.Mutations.IsPending, "Reload did not finish exactly once.");
                Check(f.Ports.Published == 1 && f.Ports.GamePublished == 1 && f.Ports.Snapshot == f.SnapshotId
                    && f.Receipt!.Request.WorldConfiguration!.StorytellerDefName == "original", "Publication or frozen world configuration changed.");
                Check(f.Stages.Contains("scene") && f.Stages.Contains("loaded") && f.Stages.Last() == "completed", "Load completion ordering changed.");
            }
            foreach (string stage in new[] { "login", "download", "load-queued", "scene", "loaded" })
            foreach (string change in new[] { "server", "user", "colony", "auth", "snapshot", "game", "login", "generation", "mutation" })
            {
                using var f = new Fixture(true);
                f.Hook = name => { if (name == stage) f.Change(change); };
                await f.Ports.PumpUntil(f.Start());
                Check(f.Completed == 0 && f.Ports.Published == 0 && f.Ports.GamePublished == 0 && f.Failures == 0,
                    "Obsolete reload published: " + stage + "/" + change);
                Check(f.Logouts.Count == (stage == "login" ? 1 : 0), "Late credentials were lost or an adopted login was released.");
                if (change == "mutation") Check(f.Mutations.Current?.Kind == LocalMutationKind.Bank, "Old finally cleared another mutation.");
            }
            await Admission();
            await FailuresAndRetries();
            await LateLogout();
            await CompletionReentry();
            await OrphanedLoad();
            await LoginCallbackReplacement();
            await LoadedPresentationReplacement();
            await CompletionOwnership();
        }
        finally { CultureInfo.CurrentCulture = old; ClientSessionWork.Reset(); }
    }
    private static async Task Admission()
    {
        foreach (string change in new[] { "server", "user", "colony", "auth", "snapshot", "game", "login", "mutation" })
        {
            using var f = new Fixture(); f.Sync.TryBeginManual(out var foreign);
            var task = f.Start(); f.Change(change); f.Module.Advance(); await f.Ports.PumpUntil(task);
            Check(f.Downloads == 0 && foreign!.IsCurrent, "Cancelled admission wait released another operation: " + change);
            if (change == "mutation") Check(f.Mutations.Current?.Kind == LocalMutationKind.Bank, "Cancelled wait cleared a newer mutation.");
            foreign!.Dispose();
        }
        using (var f = new Fixture())
        {
            Check(f.Sync.TryBeginManual(out var other), "Could not acquire foreign operation.");
            var task = f.Start();
            Check(other!.IsCurrent && f.Downloads == 0 && f.Mutations.Current?.Kind == LocalMutationKind.RaidRestore, "Recovery reset someone else's lease.");
            Check(ReferenceEquals(task, f.Start()), "Duplicate recovery started another request.");
            other.Dispose(); await f.Ports.PumpUntil(task);
            Check(f.Completed == 1 && !f.Sync.IsBusy, "Waiting restore failed to resume.");
        }
        using (var f = new Fixture())
        {
            f.Sync.TryBeginManual(out var incoming);
            var task = f.Module.StartAsync(new("base", ClientSnapshotReloadPurpose.Entry), incoming); incoming!.Dispose();
            Check(f.Sync.IsBusy, "Entry caller released its transferred reload admission.");
            await f.Ports.PumpUntil(task); Check(f.Completed == 1, "Entry continuation did not load.");
        }
        using (var f = new Fixture())
        {
            f.Mutations.Begin(LocalMutationKind.Bank, "bank"); await f.Ports.PumpUntil(f.Start());
            Check(f.Downloads == 0 && f.Mutations.Current?.Kind == LocalMutationKind.Bank, "Reload replaced unrelated pending work.");
        }
        using (var f = new Fixture())
        {
            await f.Ports.PumpUntil(f.Module.StartAsync(new(null, ClientSnapshotReloadPurpose.Raid)));
            Check(f.Loads == 0 && !f.Sync.IsBusy && f.Module.BlocksGameLifecycle && f.View.Retry is null && f.View.Exit is not null,
                "Missing raid snapshot allowed the changed game to resume.");
        }
        using (var f = new Fixture())
        {
            await f.Ports.PumpUntil(f.Module.StartAsync(new(null, ClientSnapshotReloadPurpose.Entry)));
            Check(f.Loads == 0 && !f.Sync.IsBusy && !f.Mutations.IsPending, "Missing snapshot acquired admission or modified the game.");
        }
    }
    private static async Task LoginCallbackReplacement()
    {
        using var f = new Fixture(true); bool changed = false;
        f.Session.IdentityChanged += _ =>
        {
            if (changed) return; changed = true;
            f.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "replacement-token", SessionId = "replacement-session" });
        };
        await f.Ports.PumpUntil(f.Start());
        Check(f.Downloads == 0 && f.Loads == 0 && f.Completed == 0 && f.Ports.Auth == "replacement-token" && !f.Sync.IsBusy,
            "Snapshot reload adopted the identity published by a reentrant login callback.");
    }
    private static async Task FailuresAndRetries()
    {
        foreach (string mode in new[] { "login-rejected", "login-incomplete", "login-other", "download-rejected", "download-network", "load-before", "load-after", "load-discarded", "publish" })
        {
            using var f = new Fixture(mode.StartsWith("login", StringComparison.Ordinal)) { Mode = mode };
            if (mode == "publish") f.Ports.OnPublish = () => throw new IOException("publish");
            await f.Ports.PumpUntil(f.Start(), ex => Check(ex is IOException, "Unexpected game effect failure: " + ex));
            Check(f.Completed == 0 && f.Failures == 1 && !f.Sync.IsBusy && f.Mutations.IsPending && f.Module.BlocksGameLifecycle, "Failed reload lost its blocked state: " + mode);
            bool retry = mode is "download-network" or "load-before" or "load-discarded";
            Check((f.View.Retry is not null) == retry, "Unsafe/missing retry: " + mode);
            if (retry)
            {
                int downloads = f.Downloads; var clicked = f.View.Retry!; f.Mode = ""; clicked(); clicked();
                await f.Ports.PumpUntil(f.Done.Task);
                Check(f.Completed == 1 && f.Downloads == downloads + (mode == "download-network" ? 1 : 0)
                    && !f.Mutations.IsPending && !f.Sync.IsBusy, "Retry changed prepared bytes, repeated the decision, or retained admission.");
            }
            if (mode == "login-other") Check(f.Logouts.Single().User == "other" && f.Ports.User == "alice", "Wrong-account login was adopted or cleaned with another identity.");
        }
        using (var f = new Fixture { Mode = "download-network" })
        {
            await f.Ports.PumpUntil(f.Start()); f.Ports.Snapshot = "new-base"; f.View.Retry!();
            await f.Ports.PumpUntil(Task.CompletedTask);
            Check(f.Downloads == 1 && f.View.Retry is null && f.View.Message!.Contains("BaseSnapshotChanged"), "Retry rebound to a newer base.");
        }
        using (var f = new Fixture { Mode = "download-network" })
        {
            await f.Ports.PumpUntil(f.Module.StartAsync(new("base", ClientSnapshotReloadPurpose.Entry)));
            Check(!f.Mutations.IsPending && f.View.Shown == 0 && f.Failures == 1, "Failed main-menu download left a local mutation.");
        }
        using (var f = new Fixture())
        {
            f.Ports.OnPublish = () => { f.Ports.Snapshot = "third"; throw new IOException("third"); };
            await f.Ports.PumpUntil(f.Start(), ex => Check(ex is IOException, "Unexpected partial publication exception.")); Check(f.Failures == 0 && f.Ports.Snapshot == "third", "Partial publication overwrote a later snapshot failure state.");
        }
    }
    private static async Task LateLogout()
    {
        using var f = new Fixture(true) { HoldLogout = true };
        f.Hook = stage => { if (stage == "login") f.Change("login"); };
        var old = f.Start(); await f.Ports.PumpUntil(f.LogoutEntered.Task);
        Check(!old.IsCompleted && !f.Sync.IsBusy, "Late logout retained business admission.");
        f.Hook = null; var next = f.Start(); f.ReleaseLogout.TrySetResult();
        await f.Ports.PumpUntil(Task.WhenAll(old, next));
        Check(f.Completed == 1 && f.Logouts.Count == 1 && f.Logouts[0].Auth == "received-token", "Late logout changed the new session or failed to clean its receipt.");
    }
    private static async Task OrphanedLoad()
    {
        using var f = new Fixture { Mode = "load-orphan" };
        var old = f.Start(); await f.Ports.PumpUntil(f.LoadQueued.Task);
        Check(!old.IsCompleted && f.Sync.IsBusy && f.Module.BlocksGameLifecycle, "Orphan fixture did not retain pending load.");
        f.Change("game"); f.Module.Advance(); f.Mode = "";
        var next = f.Start(); await f.Ports.PumpUntil(Task.WhenAll(old, next));
        Check(f.Completed == 1 && !f.Sync.IsBusy && !f.Module.BlocksGameLifecycle, "Replaced game orphaned the old lease or blocked a new reload.");
    }
    private static async Task CompletionReentry()
    {
        using var f = new Fixture(); ClientSynchronizationCoordinator.Lease? next = null;
        f.Module.Completed += _ => { Check(f.Sync.TryBeginManual(out next), "Completed callback retained the old lease."); f.Mutations.Begin(LocalMutationKind.Bank, "next"); };
        await f.Ports.PumpUntil(f.Start());
        Check(next!.IsCurrent && f.Mutations.Current?.Kind == LocalMutationKind.Bank, "Old finalizer released work started from completion."); next.Dispose();
    }

    private static async Task LoadedPresentationReplacement()
    {
        foreach (string change in new[] { "server", "user", "colony", "auth", "snapshot", "game", "login", "generation", "mutation" })
        {
            using var f = new Fixture();
            f.Hook = stage => { if (stage == "progress-loaded") f.Change(change); };
            await f.Ports.PumpUntil(f.Start());
            Check(f.Completed == 0, "Loaded presentation handed an obsolete reload to world followups: " + change);
        }
    }

    private static async Task CompletionOwnership()
    {
        foreach (string change in new[] { "none", "server", "user", "colony", "auth", "snapshot", "game", "login", "same-login", "generation", "reload" })
        {
            using var f = new Fixture();
            await f.Ports.PumpUntil(f.Start());
            var receipt = f.Receipt!;
            Check(receipt.Step(static () => { }), "Completed receipt was revoked by load/admission disposal.");
            Task? next = null;
            if (change == "reload") next = f.Start(); else f.Change(change);
            int calls = 0;
            Check(receipt.Step(() => calls++) == (change == "none") && calls == (change == "none" ? 1 : 0), "Completion rebound: " + change);
            if (next is not null) { await f.Ports.PumpUntil(next); Check(!receipt.Step(() => calls++), "Old receipt revived after the next reload finished."); }
        }
        using (var f = new Fixture())
        {
            int later = 0;
            f.Module.Completed += _ => f.Change("same-login");
            f.Module.Completed += _ => later++;
            await f.Ports.PumpUntil(f.Start());
            Check(later == 0, "A completion subscriber rebound later subscribers to its new login.");
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
