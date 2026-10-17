using Ports = EventModuleTestPorts;
using FailureView = EventModuleFailureView;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class EventConfirmationModuleTests
{
    internal static void RunWorkflow() => WorkflowAsync().GetAwaiter().GetResult();
    private static async Task WorkflowAsync()
    {
        try
        {
            foreach (string kind in new[] { "gift", "support", "trade", "batch" })
            {
                var f = new Fixture(); int completed = 0;
                int captureThread = System.Environment.CurrentManagedThreadId;
                f.Ports.OnCapture = () => Require(System.Environment.CurrentManagedThreadId == captureThread, "Save capture left the caller thread.");
                f.Workflow.Completed += () => { Require(!f.Mutations.IsPending && !f.Sync.IsBusy, "Completion ran before reset/publication."); completed++; };
                f.Inbox.TryTrackPendingConfirmation("event");
                Task work = StartWorkflow(f, kind);
                Require(f.Ports.Captures == 1 && f.Mutations.IsPending && f.Sync.IsBusy, "Workflow did not prepare synchronously.");
                (await f.Ports.Next())(); await work;
                Require(completed == 1 && f.Ports.Snapshot == "accepted" && !f.Mutations.IsPending && !f.Sync.IsBusy,
                    "Workflow completion lost authoritative publication: " + kind);
                Require(f.Effects.Handled.SequenceEqual(kind is "gift" or "support" ? new[] { "event" } : Array.Empty<string>()), "Wrong handled effects: " + kind);
                Require(f.Inbox.HasPendingConfirmation("event") == (kind != "gift"), "Workflow cleared unrelated pending confirmations.");
                var last = f.Presentation.Notices.Last();
                Require(last.Stage == ClientEventConfirmationStage.Accepted && last.SnapshotId == "accepted", "Missing typed success notice.");
                if (kind == "batch") Require(f.Protocol.Batch!.IdempotencyKey.StartsWith("auto-event-confirm:alice:colony:event:"), "Batch prefix changed.");
                else Require(f.Protocol.Single!.IdempotencyKey.StartsWith((kind == "support" ? "support" : kind) + "-confirm:alice:colony:event:")
                    && f.Protocol.Single.ApplicationResult == (kind == "support" ? "SupportPawnArrived" : kind == "trade" ? "SelfDeliveryExchangeApplied" : "delivered")
                    && f.Protocol.Single.SourceEventId == (kind == "trade" ? "trade-source" : null)
                    && f.Protocol.Single.BaseSnapshotId == (kind == "trade" ? "trade-base" : "base"), "Intent changed in shared workflow.");
            }
            foreach (string kind in new[] { "gift", "support", "trade", "batch" })
            foreach (string error in new[] { "transport", "null", "rejected", "exception" })
            {
                var f = new Fixture(); f.Protocol.Failure = error;
                await StartWorkflow(f, kind); (await f.Ports.Next())();
                var failure = f.Presentation.Notices.Last();
                Require(failure.Stage == ClientEventConfirmationStage.Failed && f.View.Visible is not null
                    && f.Mutations.IsPending && !f.Sync.IsBusy && f.Effects.Handled.Count == 0,
                    "Failure lost pending mutation, admission or recovery: " + kind + "/" + error);
                Require(f.View.Visible!.CanRetry == (error is "transport" or "exception"), "Retry classification changed.");
            }
            foreach (string admission in new[] { "identity", "snapshot", "busy", "blocked", "capture", "obsolete" })
            {
                var f = new Fixture(); ClientSynchronizationCoordinator.Lease? held = null;
                if (admission == "identity") f.Ports.Colony = "";
                if (admission == "snapshot") f.Ports.Snapshot = "";
                if (admission == "busy") f.Sync.TryBeginManual(out held);
                if (admission == "blocked") f.Mutations.Begin(LocalMutationKind.Bank, "bank");
                if (admission == "capture") f.Ports.CaptureFailure = new("capture", "save failed", SnapshotUploadMessage.SaveFailed);
                if (admission == "obsolete") f.Ports.OnCapture = () => f.Session.ClearIdentity();
                await f.Workflow.GiftAsync("event", "delivered");
                Require(f.Protocol.Calls == 0 && f.Effects.Handled.Count == 0, "Failed preparation reached protocol.");
                if (admission == "obsolete") Require(f.Presentation.Notices.Count == 0, "Obsolete preparation reported into a replacement session.");
                else Require(f.Presentation.Notices.Single().Stage == ClientEventConfirmationStage.PreparationFailed, "Missing preparation notice.");
                if (admission is "identity" or "snapshot" or "capture") { (await f.Ports.Next())(); Require(f.View.Visible is not null, "Preparation failure lost recovery."); }
                else Require(f.Ports.Queued == 0, "Busy/blocked/obsolete preparation queued recovery.");
                held?.Dispose();
            }
            var empty = new Fixture();
            await empty.Workflow.GiftAsync("", "delivered"); await empty.Workflow.SupportArrivalAsync(""); await empty.Workflow.TradeAsync("", "", "");
            await empty.Workflow.AutomaticAsync(Array.Empty<ModConfirmEventApplicationEntryDto>());
            Require(empty.Ports.Captures == 0 && empty.Protocol.Calls == 0 && empty.Presentation.Notices.Count == 3, "Empty intents entered confirmation.");
            foreach (string kind in new[] { "gift", "batch" })
            foreach (string replacement in new[] { "login", "generation", "game", "snapshot" })
            foreach (bool throws in new[] { false, true })
            {
                var f = new Fixture(); f.Protocol.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var work = StartWorkflow(f, kind);
                await f.Protocol.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Replace(f, replacement);
                f.Mutations.Begin(LocalMutationKind.Bank, "new owner");
                if (throws) f.Protocol.Hold.SetException(new HttpRequestException("late")); else f.Protocol.Hold.SetResult();
                await work;
                Require(f.Presentation.Notices.All(n => n.Stage == ClientEventConfirmationStage.Uploading)
                    && f.Mutations.Current?.Kind == LocalMutationKind.Bank && f.Effects.Handled.Count == 0 && f.Ports.Queued == 0,
                    "Late workflow result changed replacement state: " + kind + "/" + replacement);
            }
            var interruptedPublication = new Fixture();
            interruptedPublication.Ports.OnPublish = () => throw new System.IO.IOException("publication interrupted");
            var publication = interruptedPublication.Workflow.GiftAsync("event", "delivered");
            try { (await interruptedPublication.Ports.Next())(); throw new Exception("Publication fault was not injected."); }
            catch (System.IO.IOException) { }
            await publication; (await interruptedPublication.Ports.Next())();
            Require(interruptedPublication.Ports.Snapshot == "accepted" && interruptedPublication.View.Visible is not null
                && interruptedPublication.Mutations.IsPending && !interruptedPublication.Sync.IsBusy,
                "Our own interrupted publication lost recovery after advancing snapshot metadata.");
            await WorkflowBatchRetry();
            await WorkflowPreparedRaid();
        }
        finally { ClientSessionWork.Reset(); }
    }

    private static Task StartWorkflow(Fixture f, string kind) => kind switch
    {
        "gift" => f.Workflow.GiftAsync("event", "delivered"),
        "support" => f.Workflow.SupportArrivalAsync("event"),
        "trade" => f.Workflow.TradeAsync("event", "trade-source", "trade-base"),
        _ => f.Workflow.AutomaticAsync(new[] { Entry("event") })
    };

    private static async Task WorkflowBatchRetry()
    {
        var f = new Fixture(); f.Protocol.Failure = "transport";
        foreach (string id in new[] { "é", "A", "a", "rejected" }) f.Inbox.TryTrackPendingConfirmation(id);
        var entries = new[] { Entry("é"), Entry("A"), Entry("a") };
        var work = f.Workflow.AutomaticAsync(entries);
        entries[0].EventId = "caller-mutated";
        await work; (await f.Ports.Next())();
        // The protocol receives its own copy too; mutation there must not change retry intent.
        f.Protocol.Batch!.Applications.First().EventId = "transport-mutated";
        f.Protocol.Failure = null;
        f.Protocol.Applications = new()
        {
            new() { EventId = "é", Result = new() { Accepted = true }, AppliedSnapshotId = "first" },
            new() { EventId = "rejected", Result = new() { Accepted = false }, AppliedSnapshotId = "wrong" },
            new() { EventId = "A", Result = new() { Accepted = true }, AppliedSnapshotId = "last" },
            new() { EventId = "", Result = new() { Accepted = true }, AppliedSnapshotId = "" },
            new() { EventId = "unknown", Result = null }
        };
        int completed = 0; f.Workflow.Completed += () => completed++;
        int captureThread = System.Environment.CurrentManagedThreadId;
        f.Ports.OnCapture = () => Require(System.Environment.CurrentManagedThreadId == captureThread, "Retry save left the game thread.");
        // Retry is a game-thread action. Replaying the same button cannot submit twice.
        var retry = f.View.Retry!; retry(); retry();
        (await f.Ports.Next())();
        Require(f.Protocol.Calls == 2 && f.Ports.Captures == 2 && completed == 1
            && f.Protocol.Batch!.Applications.Select(e => e.EventId).SequenceEqual(new[] { "é", "A", "a" })
            && f.Ports.Snapshot == "last" && f.Effects.Handled.SequenceEqual(new[] { "é", "A" }), "Retry changed intent or batch result selection.");
        var accepted = f.Presentation.Notices.Last();
        Require(accepted.Count == 3 && accepted.Rejected == 2, "Batch accepted/rejected counts changed.");
        Require(!f.Inbox.HasPendingConfirmation("é") && !f.Inbox.HasPendingConfirmation("A")
            && f.Inbox.HasPendingConfirmation("a") && f.Inbox.HasPendingConfirmation("rejected"), "Batch cleared unconfirmed gift intent.");
        (await f.Ports.Next())();
        Require(f.View.Visible is null, "Successful retry retained the old failure window.");
    }

    private static async Task WorkflowPreparedRaid()
    {
        var f = new Fixture(); var continuation = f.Capture().Admission;
        int completed = 0; f.Workflow.Completed += () => completed++;
        var command = f.Confirmation.Single(f.Confirmation.Capture(continuation), "raid", "event", null, "raid-base", Package(), new byte[] { 1 }, "settled")
            with { IdempotencyKey = "prepared-raid-key", UserId = "defender", ColonyId = "defender-colony", HttpTimeout = TimeSpan.FromMinutes(3) };
        Task work = f.Workflow.PreparedRaidAsync(command, continuation, static () => { }, (_, _) => throw new Exception("Unexpected raid failure"), _ =>
        {
            Require(completed == 1 && !f.Mutations.IsPending && !f.Sync.IsBusy, "Raid completion did not precede domain continuation.");
            f.Mutations.Begin(LocalMutationKind.RaidAttacker, "next");
            Require(f.Sync.TryAcquireSnapshot(out f.Next), "Raid callback could not continue synchronization.");
            f.Recovery.Show(LocalMutationKind.RaidAttacker, "next failed", static () => { });
        });
        (await f.Ports.Next())(); await work;
        (await f.Ports.Next())();
        Require(f.Ports.Captures == 0 && ReferenceEquals(f.Protocol.Single, command) && f.Ports.Snapshot == "base"
            && f.Next!.IsCurrent && f.View.Message == "next failed", "Prepared workflow rebuilt data, published another identity or closed next failure.");
        f.Next!.Dispose();
        foreach (string replacement in new[] { "none", "identity", "game", "mutation", "generation" })
        {
            var stale = new Fixture(); stale.Protocol.Failure = "transport"; int failed = 0;
            var task = stale.Workflow.PreparedRaidAsync(command, null, static () => { }, (_, canRetry) =>
            { Require(canRetry && !stale.Sync.IsBusy, "Prepared failure retained lease or lost retry."); failed++; }, _ => throw new Exception("Unexpected success"));
            await task;
            var pending = await stale.Ports.Next();
            if (replacement == "identity") stale.Session.ClearIdentity();
            if (replacement == "game") stale.Ports.GameOwner = new();
            if (replacement == "mutation") stale.Mutations.Begin(LocalMutationKind.Raid, "new attempt");
            if (replacement == "generation") ClientSessionWork.Reset();
            pending();
            Require(failed == (replacement == "none" ? 1 : 0), "Queued raid failure escaped ownership: " + replacement);
        }
    }

    internal static void RunPreparationAndRecovery() => PreparationAndRecovery().GetAwaiter().GetResult();
    private static async Task PreparationAndRecovery()
    {
        try
        {
            PreparationAdmission();
            PreparationFailures();
            await RecoveryDecisions();
            await RecoveryReplacement();
        }
        finally { ClientSessionWork.Reset(); }
    }

    private static void PreparationAdmission()
    {
        foreach (string missing in new[] { "identity", "snapshot" })
        {
            var f = new Fixture();
            if (missing == "identity") f.Ports.Colony = ""; else f.Ports.Snapshot = "";
            var result = f.Preparation.Current(LocalMutationKind.Gift, "uploading");
            Require(result.Failure?.Admission == (missing == "identity" ? LocalMutationAdmission.NotConfigured : LocalMutationAdmission.SnapshotMissing)
                && f.Ports.Captures == 0 && !f.Sync.IsBusy && !f.Mutations.IsPending, "Missing configuration reached snapshot capture.");
        }
        var blocked = new Fixture(); blocked.Mutations.Begin(LocalMutationKind.Bank, "bank waiting");
        var denied = blocked.Preparation.Current(LocalMutationKind.Gift, "gift");
        Require(denied.Failure?.Kind == ClientEventPreparationFailureKind.Blocked && denied.Failure.Blocked?.Status == "bank waiting"
            && blocked.Mutations.Current?.Kind == LocalMutationKind.Bank && blocked.Ports.Captures == 0, "Preparation replaced an unrelated mutation.");
        foreach (bool snapshot in new[] { false, true })
        {
            var busy = new Fixture();
            ClientSynchronizationCoordinator.Lease? held;
            Require(snapshot ? busy.Sync.TryAcquireSnapshot(out held) : busy.Sync.TryBeginManual(out held), "Fixture lease missing.");
            var deniedBusy = busy.Preparation.Current(LocalMutationKind.Gift, "gift");
            Require(deniedBusy.Failure?.Kind == ClientEventPreparationFailureKind.Busy && held!.IsCurrent
                && !busy.Mutations.IsPending && busy.Ports.Captures == 0, "Busy preparation changed an existing operation.");
            held!.Dispose();
        }
        var successful = new Fixture(); successful.Mutations.Begin(LocalMutationKind.Gift, "waiting");
        var ready = successful.Preparation.Current(LocalMutationKind.Gift, "uploading");
        Require(ready.Failure is null && ready.Attempt!.Admission.IsCurrent && ready.Build?.Success == true
            && ready.Build.Package!.OwnerId == "alice" && ready.Build.Package.ColonyId == "colony"
            && successful.Ports.Captures == 1 && successful.Mutations.Current?.Status == "uploading", "Current preparation lost its captured identity or lease.");
        ready.Attempt!.Admission.Dispose();

        foreach (bool snapshot in new[] { false, true })
        {
            var f = new Fixture(); f.Ports.Snapshot = "";
            ClientSynchronizationCoordinator.Lease? continuation;
            Require(snapshot ? f.Sync.TryAcquireSnapshot(out continuation) : f.Sync.TryBeginManual(out continuation), "Prepared fixture lease missing.");
            var result = f.Preparation.Prepared(LocalMutationKind.None, "prepared", Package(), new byte[] { 1 }, continuation);
            Require(result.Failure is null && result.Attempt!.Admission.IsSnapshot && result.Attempt.Admission.IsCurrent
                && result.Operation == LocalMutationKind.PreparedEvent && f.Ports.Captures == 0 && result.Build is null,
                "Prepared submission rebuilt a package or rejected its continuation.");
            result.Attempt!.Admission.Dispose();
        }
        var invalid = new Fixture(); var old = invalid.Capture();
        var empty = invalid.Preparation.Prepared(LocalMutationKind.Raid, "prepared", null, Array.Empty<byte>(), old.Admission);
        Require(empty.Failure?.Kind == ClientEventPreparationFailureKind.InvalidPreparedData && !invalid.Sync.IsBusy && !invalid.Mutations.IsPending,
            "Invalid prepared input retained its lease.");
        var stale = new Fixture(); var staleLease = stale.Capture().Admission;
        staleLease.Dispose(); var current = stale.Capture().Admission;
        var staleResult = stale.Preparation.Prepared(LocalMutationKind.Raid, "prepared", Package(), new byte[] { 1 }, staleLease);
        Require(staleResult.Failure?.Kind == ClientEventPreparationFailureKind.Busy && current.IsCurrent, "Stale continuation affected a replacement lease.");
        current.Dispose();
        var blockedPrepared = new Fixture(); var blockedLease = blockedPrepared.Capture().Admission;
        blockedPrepared.Mutations.Begin(LocalMutationKind.Bank, "bank");
        var blockedPackage = blockedPrepared.Preparation.Prepared(LocalMutationKind.Raid, "raid", Package(), new byte[] { 1 }, blockedLease);
        Require(blockedPackage.Failure?.Kind == ClientEventPreparationFailureKind.Blocked && !blockedPrepared.Sync.IsBusy
            && blockedPrepared.Mutations.Current?.Kind == LocalMutationKind.Bank, "Blocked prepared submission retained its lease or erased unrelated work.");
    }

    private static void PreparationFailures()
    {
        foreach (string kind in new[] { "capture", "xml", "exception", "cancel" })
        {
            var f = new Fixture();
            if (kind == "capture") f.Ports.CaptureFailure = new("capture-failed", "capture failure", SnapshotUploadMessage.SaveFailed);
            if (kind == "xml") f.Ports.SaveBytes = Encoding.UTF8.GetBytes("<broken");
            if (kind == "exception") f.Ports.OnCapture = () => throw new InvalidOperationException("capture exception");
            if (kind == "cancel") f.Ports.OnCapture = () => throw new OperationCanceledException("capture cancellation");
            var result = f.Preparation.Current(LocalMutationKind.Gift, "uploading");
            Require(result.Failure?.Kind == ClientEventPreparationFailureKind.Build && result.Failure.SnapshotFailure is not null
                && !f.Sync.IsBusy && f.Mutations.IsPending, "Build failure leaked admission or discarded unconfirmed local work: " + kind);
            f.Ports.CaptureFailure = null; f.Ports.OnCapture = null; f.Ports.SaveBytes = SnapshotPackageModuleTests.Save();
            var retry = f.Preparation.Current(LocalMutationKind.Gift, "retry");
            Require(retry.Failure is null, "Failed preparation blocked its own retry."); retry.Attempt!.Admission.Dispose();
        }
        foreach (string replacement in new[] { "login", "generation", "game", "snapshot" })
        {
            var f = new Fixture();
            f.Ports.OnCapture = () => { if (replacement == "game") f.Ports.GameOwner = new(); else Replace(f, replacement); };
            var result = f.Preparation.Current(LocalMutationKind.Gift, "uploading");
            Require(result.Failure?.Kind == ClientEventPreparationFailureKind.Obsolete && !f.Sync.IsBusy,
                "Preparation published after owner replacement: " + replacement);
        }
    }

    private static async Task RecoveryDecisions()
    {
        var f = new Fixture(); int retries = 0, exits = 0;
        f.Recovery.ReturningToMenu += () => exits++;
        f.Recovery.Show(LocalMutationKind.Gift, "failed", () => retries++, true);
        Require(f.View.Shown == 0 && f.Mutations.IsPending, "Recovery showed a window before the game thread.");
        (await f.Ports.Next())();
        Require(f.View.Message == "failed" && f.View.CurrentSession!() && f.View.Retry is not null, "Failure view lost its state.");
        Action retry = f.View.Retry!;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(retry)));
        Require(retries == 1 && f.Mutations.IsPending && f.View.CurrentSession(), "Retry was consumed twice or cleared unconfirmed work.");
        f.View.Exit!(); Require(exits == 0, "Consumed retry still allowed exit.");
        f.Recovery.Show(LocalMutationKind.Gift, "terminal", () => retries++, false); (await f.Ports.Next())();
        Require(f.View.Retry is null, "Terminal failure offered retry.");
        Action exit = f.View.Exit!;
        Require(f.Sync.TryAcquireSnapshot(out var held), "Exit fixture admission missing.");
        exit(); exit();
        Require(exits == 1 && f.View.Menus == 1 && !f.Mutations.IsPending && !held.IsCurrent && f.View.Visible is null,
            "Exit did not clear the matching failure and synchronization exactly once.");

        var nested = new Fixture();
        nested.Recovery.Show(LocalMutationKind.Trade, "first", () => nested.Recovery.Show(LocalMutationKind.Trade, "second", static () => { }, true), true);
        (await nested.Ports.Next())(); Action oldExit = nested.View.Exit!; nested.View.Retry!();
        (await nested.Ports.Next())(); oldExit();
        Require(nested.View.Message == "second" && nested.View.Menus == 0 && nested.Mutations.Current?.Status == "second",
            "Old decision erased a new failure reported by retry.");
    }

    private static async Task RecoveryReplacement()
    {
        foreach (string change in new[] { "login", "clear", "colony", "server", "generation", "game" })
        foreach (bool shown in new[] { false, true })
        {
            var f = new Fixture(); int retries = 0;
            f.Recovery.Show(LocalMutationKind.Gift, "old", () => retries++, true);
            var show = await f.Ports.Next();
            if (shown) show();
            if (change == "game") f.Ports.GameOwner = new(); else Replace(f, change);
            if (shown)
            {
                Require(!f.View.CurrentSession!(), "Old failure window still belongs to replacement: " + change);
                f.View.Retry!(); f.View.Exit!();
            }
            else show();
            Require(retries == 0 && f.View.Menus == 0 && (shown || f.View.Shown == 0), "Old failure reached the replacement session.");
        }
        var lineage = new Fixture(); int continued = 0;
        lineage.Recovery.Show(LocalMutationKind.Gift, "retry", () => continued++, true); (await lineage.Ports.Next())();
        lineage.Ports.Snapshot = "next"; lineage.View.Retry!();
        Require(continued == 1, "Snapshot progression alone invalidated failure recovery identity.");

        var ordered = new Fixture();
        ordered.Recovery.Show(LocalMutationKind.Gift, "first", static () => { }, true); (await ordered.Ports.Next())();
        ordered.Recovery.Close(); Action closeOld = await ordered.Ports.Next();
        ordered.Recovery.Show(LocalMutationKind.Trade, "new", static () => { }, false); (await ordered.Ports.Next())();
        closeOld(); Require(ordered.View.Message == "new" && ordered.View.Visible is not null, "Queued old close hid the new failure.");
        ordered.Recovery.Close(); (await ordered.Ports.Next())(); Require(ordered.View.Visible is null, "Current failure did not close.");
        var lateShow = new Fixture(); lateShow.Recovery.Show(LocalMutationKind.Gift, "closed", static () => { }, true);
        var queuedShow = await lateShow.Ports.Next(); lateShow.Recovery.Close(); queuedShow();
        Require(lateShow.View.Shown == 0, "Closed failure appeared from an older queued show.");
    }

    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        try
        {
            await WireContracts();
            await SuccessfulPublication();
            await Failures();
            await ReplacedOwners();
            await LateCompletions();
            RetryPolicy();
        }
        finally { ClientSessionWork.Reset(); }
    }

    private static async Task WireContracts()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "fr-FR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var context = new ClashOfRimClientNetworkContext("http://localhost/base", "用户", "colony", "base", null, null, "auth");
                int calls = 0, diagnostics = 0;
                using var http = new HttpClient(new Handler(async request =>
                {
                    int index = calls++;
                    bool batch = index == 2;
                    Require(request.RequestUri!.AbsolutePath == "/base/" + ProtocolContractManifest.Find(batch
                        ? ProtocolMessageKind.ConfirmEventApplications : ProtocolMessageKind.ConfirmEventApplication).Route.TrimStart('/'), "Confirmation route changed.");
                    var parts = ((MultipartFormDataContent)request.Content!).ToArray();
                    Require(parts.Length == 2 && (await parts[1].ReadAsByteArrayAsync()).SequenceEqual(new byte[] { 1, 2, 3 }), "Snapshot payload changed.");
                    using var body = JsonDocument.Parse(await parts[0].ReadAsStringAsync());
                    var json = body.RootElement;
                    Require(json.GetProperty("authToken").GetString() == "auth" && json.GetProperty("userId").GetString() == (index == 1 ? "target" : "用户")
                        && json.GetProperty("colonyId").GetString() == (index == 1 ? "target-colony" : "colony"), "Prepared target or authenticated owner was lost.");
                    Require(json.GetProperty("baseSnapshotId").GetString() == "base" && json.GetProperty("idempotencyKey").GetString() == "key", "Snapshot/idempotency contract changed.");
                    Require(json.GetProperty("confirmedSnapshot").GetProperty("snapshotUploadKind").GetString() == (batch
                        ? ModSnapshotUploadKinds.BatchEventApplicationConfirmation : ModSnapshotUploadKinds.EventApplicationConfirmation), "Snapshot upload kind changed.");
                    if (batch) Require(json.GetProperty("applications").EnumerateArray().Select(x => x.GetProperty("eventId").GetString()).SequenceEqual(new[] { "é", "A" }), "Batch wire order changed.");
                    else Require(json.GetProperty("eventId").GetString() == "event" && json.GetProperty("sourceEventId").GetString() == "source"
                        && json.GetProperty("clientApplicationResult").GetString() == "arrived", "Application contract changed.");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                }));
                var protocol = new ClientEventConfirmationProtocol(new ClientProtocolTransport(http, context, new Environment()), () => { diagnostics++; return "unconfigured"; });
                var single = new ClientEventConfirmationCommand("key", "event", "source", "用户", "colony", "base", Package(), new byte[] { 1, 2, 3 }, "arrived");
                Require((await protocol.ConfirmAsync(context, single, default)).Success, "Single protocol failed.");
                Require((await protocol.ConfirmAsync(context, single with { UserId = "target", ColonyId = "target-colony" }, default)).Success, "Prepared protocol failed.");
                var batchCommand = new ClientEventBatchConfirmationCommand("key", "base", Package(), single.Payload, new[] { Entry("é"), Entry("A") });
                Require((await protocol.ConfirmBatchAsync(context, batchCommand, default)).Success && diagnostics == 0, "Batch or lazy diagnostics failed.");
                var missing = new ClashOfRimClientNetworkContext("", "", "", null, null, null, "");
                Require((await protocol.ConfirmAsync(missing, single, default)).ErrorCode == "NotConfigured"
                    && (await protocol.ConfirmBatchAsync(missing, batchCommand, default)).Message == "unconfigured" && calls == 3 && diagnostics == 2, "Missing configuration sent HTTP.");
            }
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    private static async Task SuccessfulPublication()
    {
        foreach (string mode in new[] { "single", "batch", "prepared-current", "prepared-other" })
        {
            var f = new Fixture();
            var attempt = f.Capture();
            Task work = f.Run(attempt, mode);
            var pending = await f.Ports.Next();
            Require(f.Sync.IsBusy && f.Ports.Published == 0 && f.Accepted == 0 && !work.IsCompleted, "Admission released before main-thread publication.");
            pending();
            await work;
            Require(f.Accepted == 1 && f.Failures.Count == 0 && !f.Sync.IsBusy, "Success did not complete exactly once.");
            bool current = mode != "prepared-other";
            Require(f.Ports.Published == (current ? 1 : 0) && f.Ports.GamePublished == (current ? 1 : 0), "Wrong colony received snapshot publication.");
            Require(f.Ports.Trace.SequenceEqual(current ? new[] { "store", "game", "persist", "accepted" } : new[] { "accepted" }), "Confirmation publication order changed.");
            Require(f.Protocol.Context!.UserId == "alice" && f.Protocol.Context.AuthToken == "auth", "Confirmation borrowed another identity.");
            if (mode == "single") Require(f.Protocol.Single!.IdempotencyKey == "prefix:alice:colony:event:confirmed" && f.Inbox.GroupOf("event") is null, "Single key/group cleanup failed.");
            if (mode == "batch") Require(f.Protocol.Batch!.IdempotencyKey == "prefix:alice:colony:A,a,é:confirmed"
                && f.Protocol.Batch.Applications.Select(e => e.EventId).SequenceEqual(new[] { "é", "a", "A" }), "Ordinal digest or batch order changed.");
            if (mode.StartsWith("prepared")) Require(f.Protocol.Single!.IdempotencyKey == "prepared-key" && f.Protocol.Single.BaseSnapshotId == "target-base"
                && f.Protocol.Single.HttpTimeout == TimeSpan.FromMinutes(7), "Prepared data/timeout was recomputed.");
        }
        var empty = new Fixture(); empty.Protocol.Snapshot = "";
        var task = empty.Run(empty.Capture(), "single"); (await empty.Ports.Next())(); await task;
        Require(empty.Ports.Published == 0 && empty.Accepted == 1, "Empty snapshot prevented domain acknowledgement.");
    }

    private static async Task Failures()
    {
        foreach (string mode in new[] { "single", "batch", "prepared-other" })
        foreach (string error in new[] { "transport", "null", "rejected", "exception" })
        {
            var f = new Fixture(); f.Protocol.Failure = error;
            await f.Run(f.Capture(), mode);
            Require(f.Failures.Single() == (error == "rejected" ? ClientSnapshotTransactionFailureKind.Rejected
                : error == "exception" ? ClientSnapshotTransactionFailureKind.Exception : ClientSnapshotTransactionFailureKind.Transport), "Wrong failure kind: " + error);
            Require(!f.Sync.IsBusy && f.Accepted == 0 && f.Ports.Published == 0 && f.Ports.Queued == 0, "Failure leaked admission or published a snapshot.");
            Require(f.Sync.TryAcquireSnapshot(out var next), "Failure blocked retry admission."); next.Dispose();
        }
    }

    private static async Task ReplacedOwners()
    {
        foreach (string change in new[] { "login", "clear", "colony", "server", "snapshot", "generation", "lease", "game" })
        foreach (bool queued in new[] { false, true })
        {
            var f = new Fixture(); var attempt = f.Capture();
            Task? work = null; Action? pending = null;
            if (queued) { work = f.Run(attempt, "single"); pending = await f.Ports.Next(); }
            Replace(f, change);
            if (queued) { pending!(); await work!; }
            else await f.Run(attempt, "single");
            Require(f.Accepted == 0 && f.Failures.Count == 0 && f.Ports.Published == 0, "Stale operation published after " + change);
            Require(queued || f.Protocol.Calls == 0, "Expired request sent HTTP after " + change);
        }
        var game = new Fixture(); var task = game.Run(game.Capture(), "single");
        Action callback = await game.Ports.Next(); game.Ports.GameOwner = new(); callback(); await task;
        Require(game.Accepted == 0 && game.Ports.Published == 0 && !game.Sync.IsBusy, "Replaced game received confirmation.");
    }

    private static async Task LateCompletions()
    {
        foreach (string mode in new[] { "single", "batch", "prepared-other" })
        foreach (string change in new[] { "login", "clear", "colony", "generation", "lease", "game" })
        foreach (bool throws in new[] { false, true })
        {
            var f = new Fixture(); f.Protocol.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = f.Run(f.Capture(), mode);
            Require(f.Protocol.Calls == 1, "Request was not started.");
            Replace(f, change);
            f.Sync.Reset(); Require(f.Sync.TryAcquireSnapshot(out var next), "Replacement could not acquire admission.");
            if (throws) f.Protocol.Hold.SetException(new HttpRequestException("late")); else f.Protocol.Hold.SetResult();
            await work;
            Require(f.Accepted == 0 && f.Failures.Count == 0 && f.Ports.Queued == 0 && next.IsCurrent, "Late response/finally affected replacement after " + change);
            next.Dispose();
        }
        var exit = new Fixture(); var queued = exit.Run(exit.Capture(), "single"); var old = await exit.Ports.Next();
        ClientSessionWork.Reset(); exit.Sync.Reset(); await queued.WaitAsync(TimeSpan.FromSeconds(5));
        old(); Require(exit.Accepted == 0, "Generation cancellation did not drain queued confirmation.");
        var nextOperation = new Fixture(); nextOperation.StartNextOnAccepted = true;
        var accepted = nextOperation.Run(nextOperation.Capture(), "single"); (await nextOperation.Ports.Next())(); await accepted;
        Require(nextOperation.Next!.IsCurrent, "Old finally released callback's new lease."); nextOperation.Next.Dispose();
    }

    private static void RetryPolicy()
    {
        foreach (string code in new[] { nameof(HttpRequestException), nameof(TaskCanceledException), nameof(System.IO.IOException), nameof(ObjectDisposedException) })
            Require(ClientNetworkFailurePolicy.CanRetry(code, null), "Transient exception code became terminal: " + code);
        foreach (string message in new[] { "408 Timeout", "429 Slow down", " 503 Unavailable" })
            Require(ClientNetworkFailurePolicy.CanRetry("HttpError", message), "Transient HTTP error became terminal.");
        foreach (string? message in new[] { null, "", "404 Not found", "401 Unauthorized", "unknown" })
            Require(!ClientNetworkFailurePolicy.CanRetry("HttpError", message), "Terminal HTTP error became retryable.");
        Require(!ClientNetworkFailurePolicy.CanRetry("ValidationFailed", "503")
            && !ClientNetworkFailurePolicy.CanRetry(new InvalidOperationException())
            && ClientNetworkFailurePolicy.CanRetry(new HttpRequestException()), "Retry policy changed its error source semantics.");
        Require(!new ClientSnapshotTransactionFailure<ModConfirmEventApplicationResponseDto>(ClientSnapshotTransactionFailureKind.Rejected,
            Rejection: new() { ErrorCode = 503 }).CanRetry, "Business rejection became retryable.");
    }

    private static void Replace(Fixture f, string change)
    {
        switch (change)
        {
            case "login": f.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth" }); break;
            case "clear": f.Session.ClearIdentity(); break;
            case "colony": f.Ports.Colony = "new-colony"; break;
            case "server": f.Ports.Server = "http://other"; break;
            case "snapshot": f.Ports.Snapshot = "new-snapshot"; break;
            case "game": f.Ports.GameOwner = new(); break;
            case "generation": ClientSessionWork.Reset(); f.Sync.Reset(); break;
            case "lease": f.Sync.Reset(); break;
        }
    }

    private sealed class Fixture
    {
        internal readonly Ports Ports = new();
        internal readonly Protocol Protocol = new();
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientEventInbox Inbox = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly FailureView View;
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientEventConfirmation Confirmation;
        internal readonly ClientEventConfirmationPreparation Preparation;
        internal readonly ClientSnapshotFailureRecovery Recovery;
        internal readonly WorkflowPresentation Presentation = new();
        internal readonly WorkflowEffects Effects = new();
        internal readonly ClientEventConfirmationWorkflow Workflow;
        internal readonly List<ClientSnapshotTransactionFailureKind> Failures = new();
        internal int Accepted;
        internal bool StartNextOnAccepted;
        internal ClientSynchronizationCoordinator.Lease? Next;
        internal Fixture()
        {
            View = new(Ports);
            Session = new(Ports, Ports, new ClientSessionConnection(Ports));
            Confirmation = new(Session, Ports, new(Ports, Ports), Inbox, Protocol);
            Preparation = new(Session, Sync, Mutations, Confirmation);
            Recovery = new(Session, Mutations, Sync, Ports, View);
            Workflow = new(Session, Preparation, Confirmation, Mutations, Recovery, Inbox, Presentation, Effects);
            Inbox.ReplaceQueue(new() { DirectlyProcessable = new() { new() { EventId = "event" } } });
        }
        internal ClientSnapshotTransaction.Attempt Capture()
        {
            Require(Sync.TryAcquireSnapshot(out var lease), "Fixture admission failed.");
            return Confirmation.Capture(lease!);
        }
        internal Task Run(ClientSnapshotTransaction.Attempt attempt, string mode)
        {
            if (mode == "batch")
            {
                var entries = new[] { Entry("é"), Entry("a"), Entry("A") };
                var command = Confirmation.Batch(attempt, "prefix", Package(), new byte[] { 1 }, entries);
                entries[0].EventId = "mutated";
                return Confirmation.ConfirmBatchAsync(attempt, command, f => Failures.Add(f.Kind), r => "selected", (r, id) =>
                { Require(id == "selected" && Ports.Snapshot == "selected", "Batch selection not published."); OnAccepted(); });
            }
            var single = Confirmation.Single(attempt, "prefix", "event", "source", null, Package(), new byte[] { 1 }, "arrived");
            if (mode.StartsWith("prepared")) single = single with { IdempotencyKey = "prepared-key", BaseSnapshotId = "target-base", HttpTimeout = TimeSpan.FromMinutes(7),
                UserId = mode == "prepared-other" ? "other" : "alice", ColonyId = mode == "prepared-other" ? "other-colony" : "colony" };
            return Confirmation.ConfirmAsync(attempt, single, f => Failures.Add(f.Kind), _ => OnAccepted());
        }
        private void OnAccepted()
        {
            Require(!Sync.IsBusy, "Domain callback cannot continue synchronization.");
            Accepted++; Ports.Trace.Add("accepted");
            if (StartNextOnAccepted) { Require(Sync.TryAcquireSnapshot(out var next), "Callback could not acquire next lease."); Next = next; }
        }
    }

    private sealed class Protocol : IClientEventConfirmationProtocol
    {
        internal string? Failure;
        internal string Snapshot = "accepted";
        internal int Calls;
        internal ClashOfRimClientNetworkContext? Context;
        internal ClientEventConfirmationCommand? Single;
        internal ClientEventBatchConfirmationCommand? Batch;
        internal TaskCompletionSource? Hold;
        internal readonly TaskCompletionSource Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<ModConfirmEventApplicationResultDto> Applications = new();
        public async Task<ClashOfRimClientNetworkResult<ModConfirmEventApplicationResponseDto>> ConfirmAsync(
            ClashOfRimClientNetworkContext context, ClientEventConfirmationCommand command, CancellationToken token)
        { Calls++; Context = context; Single = command; Sent.TrySetResult(); if (Hold is not null) await Hold.Task;
          return Result(new ModConfirmEventApplicationResponseDto { AppliedSnapshotId = Snapshot, NextLineageToken = "next", Result = new() { Accepted = Failure != "rejected" } }); }
        public async Task<ClashOfRimClientNetworkResult<ModConfirmEventApplicationsResponseDto>> ConfirmBatchAsync(
            ClashOfRimClientNetworkContext context, ClientEventBatchConfirmationCommand command, CancellationToken token)
        { Calls++; Context = context; Batch = command; Sent.TrySetResult(); if (Hold is not null) await Hold.Task;
          return Result(new ModConfirmEventApplicationsResponseDto { Applications = Applications, AppliedSnapshotId = Snapshot, NextLineageToken = "next", Result = new() { Accepted = Failure != "rejected" } }); }
        private ClashOfRimClientNetworkResult<T> Result<T>(T response) => Failure switch
        {
            "transport" => ClashOfRimClientNetworkResult<T>.Failed("HttpError", "503 failure"),
            "null" => ClashOfRimClientNetworkResult<T>.Ok(default!),
            "exception" => throw new HttpRequestException("failure"),
            _ => ClashOfRimClientNetworkResult<T>.Ok(response)
        };
    }
    private sealed class WorkflowPresentation : IClientEventConfirmationPresentation
    {
        internal readonly List<ClientEventConfirmationNotice> Notices = new();
        public string Format(ClientEventConfirmationNotice notice) => $"{notice.Purpose}:{notice.Stage}";
        public void Report(ClientEventConfirmationNotice notice, string message) => Notices.Add(notice);
    }
    private sealed class WorkflowEffects : IClientEventConfirmationEffects
    {
        internal readonly List<string> Handled = new();
        public void MarkHandled(string eventId) => Handled.Add(eventId);
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> send;
        internal Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }
    private sealed class Environment : IClientProtocolEnvironment
    {
        public string? CurrentLanguage => null;
        public string DeserializationFailure(string name) => name;
        public void SessionExpired(string message, string? token) => throw new Exception("Unexpected expiry.");
        public void MultipartDisposalFailed(string message) => throw new Exception(message);
    }
    private static ModSnapshotPackageMetadataDto Package() => new() { SnapshotId = "confirmed" };
    private static ModConfirmEventApplicationEntryDto Entry(string id) => new() { EventId = id, SourceEventId = "source", ClientApplicationResult = "arrived" };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
