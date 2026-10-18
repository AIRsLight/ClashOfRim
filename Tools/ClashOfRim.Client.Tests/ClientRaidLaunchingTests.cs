using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.RemoteMaps;

using Fixture = ClientRaidLaunchTestPorts;

internal static class ClientRaidLaunchingTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string language in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            foreach (string deadlines in new[] { "valid", "missing", "invalid" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                var f = new Fixture { DeadlineKind = deadlines }; Task work = f.Start();
                f.Target.OwnerUserId = "changed"; f.Target.MapId = "other"; f.Target.Tile++;
                await f.Open();
                Check(f.Sync.IsBusy && f.Mutations.IsPending && f.Ports.Published == 0, "Launch lost admission before receipt publication.");
                (await f.Ports.Next())(); await work;
                Check(f.Completed == 1 && f.Applied == 1 && f.Captured == 1 && f.Opened == 1 && f.Prepared == 1 && f.Downloaded == 1
                    && f.Ports.Captures == 1 && f.Ports.Published == 1 && f.Ports.GamePublished == 1 && !f.Sync.IsBusy && !f.Mutations.IsPending,
                    "Launch repeated an effect or omitted completion.");
                Check(f.Sent!.Target == new ClientRaidTarget("defender", "there", "world", "map", 7)
                    && f.Sent.DefenderSnapshotId == "downloaded" && f.Sent.PreparationId == "preparation" && f.Sent.GuardDeploymentId == "guard"
                    && f.Context!.UserId == "alice" && f.Context.CurrentSnapshotId == "base" && f.Context.AuthToken == "auth", "Launch drifted from captured authority/target.");
                Check((f.Deadlines is not null) == (deadlines == "valid"), "Partial/missing deadline replaced local fallback.");
                if (f.Deadlines is { } time) Check(time == new ClientRaidServerDeadlines(Utc(0), Utc(15), Utc(17)), "Server time parsed with local culture/time zone.");
            }
            foreach (bool build in new[] { false, true })
            {
                var f = new Fixture { Fault = build ? "" : "network" };
                if (build) f.Ports.CaptureFailure = new("capture", "failure", SnapshotUploadMessage.SaveFailed);
                Task work = f.Start(); await f.Open(); await work; (await f.Ports.Next())();
                Check(f.Failure.Retry is not null && f.Mutations.IsPending && !f.Sync.IsBusy, "Recoverable failure lost pending intent.");
                var command = f.Sent; var snapshot = f.Snapshot; var payload = f.Payload; var context = f.Context;
                f.Fault = ""; f.Ports.CaptureFailure = null; var retry = f.Failure.Retry!; retry();
                (await f.Ports.Next())(); await f.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Captured == 1 && f.Prepared == 1 && f.Downloaded == 1 && f.Opened == 1 && f.Ports.Captures == (build ? 2 : 1), "Retry repeated source capture, preparation, download or movement.");
                if (!build) Check(ReferenceEquals(command, f.Sent) && ReferenceEquals(snapshot, f.Snapshot)
                    && ReferenceEquals(payload, f.Payload) && ReferenceEquals(context, f.Context), "Retry changed intent, identity or snapshot bytes.");
                int sent = f.Submissions; retry(); Check(f.Submissions == sent, "Consumed window retried twice.");
            }
            foreach (string fault in new[] { "rejected", "permanent", "missing-result", "wrong-event", "missing-event", "wrong-snapshot", "missing-snapshot", "no-battle", "open-failed" })
            {
                var f = new Fixture { Fault = fault }; Task work = f.Start(); await f.Open();
                if (fault is "missing-result" or "wrong-event" or "missing-event" or "wrong-snapshot" or "missing-snapshot") (await f.Ports.Next())();
                await work; (await f.Ports.Next())();
                Check(f.Failure.Retry is null && f.Mutations.IsPending && !f.Sync.IsBusy && f.Ports.Published == 0 && f.Applied == 0 && f.Completed == 0, "Unsafe failure completed or allowed replay: " + fault);
                if (fault is "no-battle" or "open-failed") Check(f.Submissions == 0, "Missing battle submitted a start snapshot.");
            }
            foreach (string fault in new[] { "pvp", "target", "pending", "busy", "capture", "capture-throw", "prepare", "entry" })
            {
                var f = new Fixture { Fault = fault };
                if (fault == "target") f.Target.MapId = "";
                if (fault == "pending") f.Mutations.Begin(LocalMutationKind.Bank, "bank");
                if (fault == "busy") f.Sync.TryBeginManual(out _);
                Task work = f.Start();
                if (fault is "prepare" or "entry") (await f.Ports.Next())();
                if (fault == "entry") (await f.NextLong())();
                await work;
                Check(f.Opened == 0 && f.Ports.Captures == 0 && f.Submissions == 0 && f.Mutations.IsPending == (fault == "pending"), "Pre-mutation refusal touched game or pending state: " + fault);
                f.Sync.Reset();
            }
            foreach (string change in new[] { "server", "auth", "user", "colony", "snapshot", "game", "login", "generation" })
            foreach (string stage in new[] { "capture", "prepare", "game-queue", "long-queue", "open", "snapshot", "http", "publication" })
            {
                var f = new Fixture(); Action changeOwner = () => Change(f, change);
                if (stage == "capture") f.AfterCapture = changeOwner;
                if (stage == "prepare") f.PrepareHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (stage == "open") f.AfterOpen = changeOwner;
                if (stage == "snapshot") f.Ports.OnCapture = changeOwner;
                if (stage == "http") f.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Task work = f.Start(); ClientSynchronizationCoordinator.Lease? replacement = null;
                if (stage == "prepare")
                { await f.Preparing.Task.WaitAsync(TimeSpan.FromSeconds(5)); changeOwner(); f.PrepareHold!.SetResult(f.PrepareReply()); }
                else if (stage == "game-queue") { var callback = await f.Ports.Next(); changeOwner(); callback(); }
                else if (stage == "long-queue") { (await f.Ports.Next())(); var callback = await f.NextLong(); changeOwner(); callback(); }
                else if (stage is "open" or "snapshot" or "http" or "publication")
                {
                    await f.Open();
                    if (stage == "http")
                    {
                        await f.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5)); changeOwner();
                        f.Sync.Reset(); Check(f.Sync.TryBeginManual(out replacement), "Replacement admission unavailable."); f.Hold!.SetResult(f.Reply());
                    }
                    if (stage == "publication") { var callback = await f.Ports.Next(); changeOwner(); callback(); }
                }
                await work.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Ports.Published == 0 && f.Applied == 0 && f.Completed == 0 && f.Failure.Shown == 0, "Late launch crossed ownership: " + stage + "/" + change);
                if (replacement is not null) { Check(replacement.IsCurrent, "Old launch released replacement admission."); replacement.Dispose(); }
            }
            var changedBase = new Fixture { Fault = "network" }; var failed = changedBase.Start(); await changedBase.Open(); await failed; (await changedBase.Ports.Next())();
            changedBase.Ports.Snapshot = "other"; changedBase.Failure.Retry!(); (await changedBase.Ports.Next())();
            Check(changedBase.Submissions == 1 && changedBase.Failure.Retry is null, "Retry rebound to a newer base.");
            var partial = new Fixture(); partial.Ports.OnPublish = () => throw new InvalidOperationException("partial publication");
            Task publishing = partial.Start(); await partial.Open();
            try { (await partial.Ports.Next())(); } catch (InvalidOperationException) { }
            await publishing; (await partial.Ports.Next())();
            Check(partial.Ports.Published == 1 && partial.Applied == 0 && partial.Mutations.IsPending && partial.Failure.Retry is null, "Partial publication enabled resubmission.");
            var changedPublication = new Fixture(); changedPublication.Ports.OnPublish = () => changedPublication.Ports.Snapshot = "unrelated";
            Task changing = changedPublication.Start(); await changedPublication.Open(); (await changedPublication.Ports.Next())(); await changing;
            Check(changedPublication.Applied == 0 && changedPublication.Completed == 0 && changedPublication.Mutations.IsPending, "Publication cleared unrelated snapshot state.");
            var lostBattle = new Fixture(); Task losing = lostBattle.Start(); await lostBattle.Open(); var receipt = await lostBattle.Ports.Next(); lostBattle.Fault = "no-battle"; receipt(); await losing; (await lostBattle.Ports.Next())();
            Check(lostBattle.Ports.Published == 0 && lostBattle.Failure.Retry is null, "Receipt published after the local battle disappeared.");
            var discarded = new Fixture { DiscardOpen = true }; Task discarding = discarded.Start(); await discarded.Open(); await discarding;
            Check(discarded.Opened == 0 && !discarded.Sync.IsBusy && !discarded.Mutations.IsPending, "Discarded long event stranded admission.");
            foreach (bool beforeOpen in new[] { true, false })
            {
                var f = new Fixture { Fault = beforeOpen ? "enqueue-throw" : "open-throw" }; Task work = f.Start();
                if (beforeOpen)
                { try { (await f.Ports.Next())(); } catch (InvalidOperationException) { } }
                else { try { await f.Open(); } catch (InvalidOperationException) { } }
                (await f.Ports.Next())(); await work;
                if (!beforeOpen) (await f.Ports.Next())();
                Check(!f.Sync.IsBusy && f.Submissions == 0 && f.Mutations.IsPending == !beforeOpen
                    && f.Failure.Retry is null, "Open/queue exception released unconfirmed changes or stranded admission.");
            }
            var reentrant = new Fixture(); ClientSynchronizationCoordinator.Lease? nextLease = null;
            reentrant.OnCompleted = () => { Check(reentrant.Sync.TryBeginManual(out nextLease), "Completion could not start new work."); reentrant.Mutations.Begin(LocalMutationKind.Bank, "new"); };
            Task completing = reentrant.Start(); await reentrant.Open(); (await reentrant.Ports.Next())(); await completing;
            Check(nextLease!.IsCurrent && reentrant.Mutations.Current?.Kind == LocalMutationKind.Bank, "Old finally cleared reentrant work."); nextLease.Dispose();
        }
        finally { CultureInfo.CurrentCulture = previous; ClientSessionWork.Reset(); }
    }

    private static long Utc(int minute) => new DateTime(2026, 10, 18, 1, minute, 0, DateTimeKind.Utc).Ticks;
    private static void Change(Fixture f, string kind)
    {
        switch (kind)
        {
            case "server": f.Ports.Server = "http://new"; break;
            case "auth": f.Ports.Auth = "new"; break;
            case "user": f.Ports.User = "new"; break;
            case "colony": f.Ports.Colony = "new"; break;
            case "snapshot": f.Ports.Snapshot = "new"; break;
            case "game": f.Ports.GameOwner = new(); break;
            case "login": f.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "new-login" }); break;
            case "generation": ClientSessionWork.Reset(); break;
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
