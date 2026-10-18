using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Support;
using AIRsLight.ClashOfRim.Modules.Synchronization;

internal static class ClientRaidSettlementTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string language in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            foreach (double seconds in new[] { 1d, 30d, 120d })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                using var f = new Fixture { Remaining = TimeSpan.FromSeconds(seconds) };
                await f.Run();
                Check(f.Closed == 1 && f.Completed == 1 && f.Uploads == 1 && f.Commands.Count == 1 && f.Removed == 1
                    && f.SourceCaptures == 1 && f.Ports.CaptureModes.SequenceEqual(new[] { false, true })
                    && !f.Sync.IsBusy && !f.Mutations.IsPending, "Settlement did not complete support, evidence and cleanup once.");
                var command = f.Commands.Single();
                Check(command.IdempotencyKey == "raid-settlement:alice:raid:撤退-é" && command.ApplicationResult == "RaidBattleReturned:撤退-é"
                    && command.BaseSnapshotId == "base" && command.UserId == "alice" && command.ColonyId == "colony"
                    && command.HttpTimeout == TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 60)), "Settlement wire intent changed.");
                Check(f.CleanupContext!.CurrentSnapshotId == command.Snapshot.SnapshotId && f.Ports.Snapshot == "cleanup"
                    && f.Inbox.GroupOf("raid") is null, "Cleanup used old lineage or evidence remained queued.");
            }
            foreach (string fault in new[] { "network", "permanent", "rejected", "result", "event", "snapshot", "save", "queue", "packaging", "close", "after-close", "publish", "cleanup" })
            {
                using var f = new Fixture { Fault = fault };
                if (fault == "save") f.Ports.CaptureFailure = new("save", "failed", SnapshotUploadMessage.SaveFailed);
                if (fault == "publish") f.Ports.OnPublish = () => throw new Injected("publish");
                await f.Run();
                if (fault == "cleanup")
                { Check(f.Closed == 1 && f.Uploads == 1 && !f.Mutations.IsPending && !f.Sync.IsBusy && f.Completed == 0, "Best effort cleanup failure replayed evidence or blocked game."); continue; }
                Check(f.Uploads == 0 && f.Mutations.IsPending && !f.Sync.IsBusy && f.View.Visible is not null, "Failure escaped recovery or leaked admission: " + fault);
                bool retry = fault is "network" or "save" or "queue" or "packaging";
                Check((f.View.Retry is not null) == retry, "Wrong retry policy: " + fault);
                if (!retry) continue;
                var command = f.Commands.LastOrDefault(); int removed = f.Removed; int sourceCaptures = f.SourceCaptures;
                f.Fault = ""; f.Ports.CaptureFailure = null;
                var retryAction = f.View.Retry!; retryAction(); retryAction();
                await f.Pump(f.Finished.Task);
                Check(f.Removed == removed && f.SourceCaptures == sourceCaptures && f.Completed == 1, "Retry replayed support loss or completed twice.");
                if (command is not null)
                    Check(ReferenceEquals(command.Payload, f.Commands.Last().Payload) && ReferenceEquals(command.Snapshot, f.Commands.Last().Snapshot)
                        && command.IdempotencyKey == f.Commands.Last().IdempotencyKey && f.Ports.Captures == 2, "Evidence retry rebuilt or changed the captured command.");
            }
            foreach (string change in new[] { "user", "colony", "auth", "server", "snapshot", "game", "login", "generation" })
            foreach (string stage in new[] { "support", "saving", "capture", "request", "receipt", "failure" })
            {
                using var f = new Fixture(); bool changed = false;
                void Change() { if (changed) return; changed = true; f.Change(change); }
                if (stage == "support") f.OnSupport = Change;
                if (stage == "saving") f.OnSaving = Change;
                if (stage == "capture") f.Ports.OnCapture = Change;
                if (stage == "request") f.OnRequest = Change;
                if (stage is "receipt" or "failure") f.Ports.BeforeGameAction = () => { if (f.Commands.Count > 0) Change(); };
                if (stage == "failure") f.Fault = "network";
                await f.Run();
                Check(changed && f.Ports.Published == 0 && f.Closed == 0 && f.Uploads == 0 && f.View.Visible is null,
                    "Obsolete settlement published into a replacement owner: " + stage + "/" + change);
            }
            using (var f = new Fixture { Fault = "network" })
            {
                await f.Run(); f.Ports.Snapshot = "new"; f.View.Retry!();
                await f.Pump(Task.CompletedTask);
                Check(f.View.Retry is null && f.Commands.Count == 1, "Changed base could replay evidence.");
            }
            using (var f = new Fixture { Fault = "support" })
            {
                await f.Run(); Check(f.Removed == 0 && f.Ports.Captures == 0 && f.View.Retry is not null, "Support failure proceeded to evidence.");
                f.Fault = ""; f.View.Retry!(); await f.Pump(f.Finished.Task);
                Check(f.SourceCaptures == 1 && f.Removed == 1 && f.SupportCalls == 2, "Support retry rescanned or repeated deletion.");
            }
            foreach (string stage in new[] { "start", "saving", "failure" })
            {
                using var f = new Fixture();
                if (stage == "start") f.Remaining = TimeSpan.Zero;
                if (stage == "saving") f.OnSaving = () => f.Remaining = TimeSpan.Zero;
                if (stage == "failure") { f.Fault = "network"; f.OnRequest = () => f.Remaining = TimeSpan.Zero; }
                await f.Run(); f.Module.Expire(f);
                Check(f.Expired == 1 && f.Closed == 0 && f.Uploads == 0 && !f.Sync.IsBusy, "Expiry failed or repeated recovery: " + stage);
            }
            using (var f = new Fixture())
            {
                f.OnRequest = () => f.Current = false;
                await f.Run(); Check(f.Closed == 0 && f.Ports.Published == 0, "Replaced battle accepted old evidence.");
            }
            foreach (string change in new[] { "user", "snapshot", "game", "login" })
            {
                using var f = new Fixture(); bool released = false;
                f.Sync.SnapshotChanged += notice =>
                { if (!released && notice.Phase == SnapshotTransactionPhase.Ended && f.Closed > 0) { released = true; f.Change(change); } };
                await f.Run(); Check(released && f.Uploads == 0 && f.Ports.Captures == 1, "Cleanup rebound after evidence lease release: " + change);
            }
            using (var f = new Fixture())
            {
                ClientSynchronizationCoordinator.Lease? replacement = null;
                f.Sync.SnapshotChanged += notice =>
                {
                    if (notice.Phase != SnapshotTransactionPhase.Ended || f.Closed == 0 || replacement is not null) return;
                    Check(f.Mutations.Current?.Kind == LocalMutationKind.RaidAttacker, "Handoff exposed unconfirmed game changes.");
                    Check(f.Sync.TryBeginManual(out replacement), "Could not inject competing admission.");
                };
                await f.Run();
                Check(replacement!.IsCurrent && f.View.Retry is not null && f.Uploads == 0, "Old evidence finally released replacement or lost cleanup retry.");
                replacement.Dispose(); f.View.Retry!(); await f.Pump(f.Finished.Task);
                Check(f.Commands.Count == 1 && f.Removed == 1 && f.Closed == 1 && f.Completed == 1, "Cleanup retry replayed settlement effects.");
            }
            using (var f = new Fixture { Fault = "network" })
            {
                ClientSynchronizationCoordinator.Lease? replacement = null;
                f.OnRequest = () => { f.Sync.Reset(); Check(f.Sync.TryBeginManual(out replacement), "Missing replacement."); };
                await f.Run(); Check(replacement!.IsCurrent && f.View.Visible is null, "Stale failure escaped its lease."); replacement.Dispose();
            }
            using (var f = new Fixture())
            {
                ClientSynchronizationCoordinator.Lease? next = null;
                f.Module.Completed += () => Check(f.Sync.TryBeginManual(out next), "Completion could not start next operation.");
                await f.Run(); Check(next!.IsCurrent, "Old completion released new operation."); next.Dispose();
            }
            using (var f = new Fixture { Remaining = TimeSpan.Zero })
            {
                f.Ports.User = "another"; await f.Run(); f.Module.Expire(f);
                Check(!f.Finishing && f.Expired == 0 && f.Current && f.Commands.Count == 0, "Foreign account controlled old battle.");
            }
            using (var f = new Fixture { Fault = "publish" })
            {
                f.Ports.OnPublish = () => throw new Injected("partial publication");
                f.Ports.BeforeGameAction = () =>
                {
                    if (f.Ports.Published == 0) return;
                    f.Ports.Snapshot = "another-commit"; f.Mutations.Begin(LocalMutationKind.Bank, "new operation");
                };
                await f.Run();
                Check(f.View.Visible is null && f.Mutations.Current?.Kind == LocalMutationKind.Bank && f.Closed == 0, "Queued partial failure overwrote another commit.");
            }
            using (var f = new Fixture { Fault = "after-close" })
            {
                f.ExpireAfterClose = true; await f.Run();
                Check(f.Closed == 1 && f.Expired == 0 && f.View.Visible is not null && f.View.Retry is null, "Partial close at deadline lost exit recovery.");
            }
            SaveValidation();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static byte[] Save()
    {
        var doc = XDocument.Parse(Encoding.UTF8.GetString(SnapshotPackageModuleTests.Save()));
        doc.Root!.Element("game")!.Add(XElement.Parse("<maps><li><uniqueID>7</uniqueID><mapInfo><parent>WorldObject_8</parent></mapInfo><complex>状态-é</complex></li></maps>"),
            XElement.Parse("<worldObjects><li><ID>8</ID><clashOfRimMode>RaidBattle</clashOfRimMode><clashOfRimRelatedEventId>raid</clashOfRimRelatedEventId></li></worldObjects>"));
        return Encoding.UTF8.GetBytes(doc.ToString());
    }
    private static void SaveValidation()
    {
        var save = Save(); var prepared = ClientRaidSettlementSnapshot.Prepare(save, "raid", "Map_42", out var failure);
        Check(failure is null && XNode.DeepEquals(XDocument.Parse(Encoding.UTF8.GetString(save)), XDocument.Parse(Encoding.UTF8.GetString(prepared!).TrimStart('\uFEFF'))), "Preparation changed game state.");
        foreach (string fault in new[] { "empty", "target", "xml", "parent", "map", "id" })
        {
            var doc = XDocument.Parse(Encoding.UTF8.GetString(save));
            if (fault == "map") doc.Root!.Element("game")!.Element("maps")!.Remove();
            if (fault == "id") doc.Descendants("uniqueID").Single().Value = "Map_";
            var input = fault == "empty" ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(fault == "xml" ? "<" : doc.ToString());
            Check(ClientRaidSettlementSnapshot.Prepare(input, fault == "parent" ? "other" : "raid", fault == "target" ? "Map_" : "42", out failure) is null && failure is not null, "Invalid evidence accepted: " + fault);
        }
    }
    private sealed class Injected : Exception { internal Injected(string message) : base(message) { } }
    private sealed class Fixture : IDisposable, IClientRaidSettlementRuntime, IClientRaidSettlementPresentation, IClientRaidSupportLossSource,
        IClientSupportReturnTransport, IClientEventConfirmationProtocol, IClientSnapshotTransport, IClientLocalSnapshotPresentation
    {
        internal readonly EventModuleTestPorts Ports = new() { AllowRaidRemoval = true, SaveBytes = Save() };
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientEventInbox Inbox = new();
        internal readonly EventModuleFailureView View;
        internal readonly ClientRaidSettlements Module;
        internal readonly List<ClientEventConfirmationCommand> Commands = new();
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ClashOfRimClientNetworkContext? CleanupContext;
        internal bool Current = true, Finishing, ExpireAfterClose;
        internal int Closed, Expired, Uploads, Completed, Removed, SourceCaptures, SupportCalls;
        internal TimeSpan Remaining = TimeSpan.FromMinutes(2);
        internal string Fault = "";
        internal Action? OnSaving, OnSupport, OnRequest;
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(Ports, Ports, new ClientSessionConnection(Ports)); View = new(Ports);
            var transactions = new ClientSnapshotTransaction(Session, Ports, new ClientSnapshotPublication(Ports, Ports));
            var admission = new ClientSnapshotMutationAdmission(Session, Sync, Mutations, transactions);
            var recovery = new ClientSnapshotFailureRecovery(Session, Mutations, Sync, Ports, View);
            var local = new ClientLocalSnapshotConfirmation(Session, Ports, admission, Sync, Mutations, transactions, recovery,
                new ClientSnapshotUploader(Ports, Ports, this, Sync), this);
            Module = new(Session, admission, Sync, Mutations, transactions, recovery, new(transactions, this), this, Inbox, local, this);
            Inbox.ReplaceQueue(new() { DirectlyProcessable = new() { new() { EventId = "raid" } } });
            Module.Completed += () => { Check(!Sync.IsBusy && !Mutations.IsPending, "Completion retained cleanup lease."); Completed++; Finished.TrySetResult(); };
            Module.Expired += _ => { Expired++; Mutations.Reset(); };
        }
        internal Task Run() => Pump(Module.StartAsync(this, "撤退-é"));
        internal Task Pump(Task task) => Ports.PumpUntil(task, ex => { if (ex is not Injected) throw ex; });
        public ClientRaidSettlementIdentity Identity => new("raid", "alice", "colony", "42");
        public bool IsCurrent => Current;
        public TimeSpan FinalRemaining => Remaining;
        public bool TryBegin() { if (!Current || Finishing) return false; Finishing = true; return true; }
        public void KeepFinish() => Finishing = true;
        public void ResetFinish() => Finishing = false;
        public IClientRaidSupportLossSource SupportLosses => this;
        public void EnqueueSaving(Action action, Action discarded)
        { if (Fault == "queue") throw new Injected("queue"); Ports.Enqueue(() => { OnSaving?.Invoke(); action(); }); }
        public void CloseAccepted(string reason)
        { if (Fault == "close") throw new Injected("close"); Closed++; Current = false; if (ExpireAfterClose) Remaining = TimeSpan.Zero; if (Fault == "after-close") throw new Injected("after close"); }
        public void CloseExpired(bool closeLocalMap) => Current = false;
        public IReadOnlyList<ClientRaidSupportLoss> Capture() { SourceCaptures++; return new[] { new ClientRaidSupportLoss("support", "key", "pawn") }; }
        public void InProgress(ClientRaidSupportLoss loss, bool value) { }
        public bool RemoveAndComplete(ClientRaidSupportLoss loss) { Removed++; return true; }
        public Task<ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>> FinishAsync(ClashOfRimClientNetworkContext context, ClientSupportReturnCommand command, CancellationToken token)
        {
            SupportCalls++; OnSupport?.Invoke();
            return Task.FromResult(Fault == "support" ? ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>.Failed("HttpError", "503 unavailable")
                : ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>.Ok(new() { Result = new() { Accepted = true }, NotificationEventId = "loss" }));
        }
        public Task<ClashOfRimClientNetworkResult<ModConfirmEventApplicationResponseDto>> ConfirmAsync(ClashOfRimClientNetworkContext context, ClientEventConfirmationCommand command, CancellationToken token)
        {
            Check(Removed == 1 && Sync.SnapshotInProgress && Mutations.IsPending, "Evidence preceded loss removal or lacks admission.");
            Commands.Add(command); OnRequest?.Invoke();
            return Task.FromResult(Fault is "network" or "permanent"
                ? ClashOfRimClientNetworkResult<ModConfirmEventApplicationResponseDto>.Failed("HttpError", Fault == "network" ? "503 unavailable" : "400 invalid")
                : ClashOfRimClientNetworkResult<ModConfirmEventApplicationResponseDto>.Ok(new()
                {
                    Result = Fault == "result" ? null : new() { Accepted = Fault != "rejected" }, EventId = Fault == "event" ? "other" : command.EventId,
                    AppliedSnapshotId = Fault == "snapshot" ? "other" : command.Snapshot.SnapshotId, NextLineageToken = "receipt"
                }));
        }
        public Task<ClashOfRimClientNetworkResult<ModConfirmEventApplicationsResponseDto>> ConfirmBatchAsync(ClashOfRimClientNetworkContext context, ClientEventBatchConfirmationCommand command, CancellationToken token) => throw new NotSupportedException();
        public Task<ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>> UploadAsync(ClashOfRimClientNetworkContext context, string key, ModSnapshotPackageMetadataDto package, byte[] payload,
            string? operation, IReadOnlyList<ModSnapshotAchievementCandidateDto> achievements, CancellationToken token)
        {
            Check(Closed == 1 && Sync.SnapshotInProgress && Mutations.Current?.Kind == LocalMutationKind.RaidAttacker, "Cleanup raced old evidence finalizer.");
            Uploads++; CleanupContext = context;
            return Task.FromResult(Fault == "cleanup" ? ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Failed("HttpError", "503 unavailable")
                : ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Ok(new() { Result = new() { Accepted = true }, AcceptedSnapshotId = "cleanup" }));
        }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "new"; break; case "colony": Ports.Colony = "new"; break; case "auth": Ports.Auth = "new"; break;
                case "server": Ports.Server = "http://new"; break; case "snapshot": Ports.Snapshot = "new"; break; case "game": Ports.GameOwner = new(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth" }); break; case "generation": ClientSessionWork.Reset(); Sync.Reset(); break;
            }
        }
        public string Busy => "busy";
        public string BaseSnapshotChanged => "base-changed";
        public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => admission.ToString();
        public string SupportFailure(ClientRaidSupportLossFailure failure) => failure.Kind.ToString();
        public string Text(string key, params (string Name, object? Value)[] args) => key + string.Join("", args.Select(a => ":" + a.Name + "=" + a.Value));
        public void Status(string message) { }
        public void Message(string message, bool? accepted) { }
        public void Packaging() { if (Fault == "packaging") throw new Injected("packaging"); }
        public void Diagnose(Exception exception) { }
        public ModSnapshotUploadResult Result(ClientSnapshotUploadResult result) => result.Failure is { } failure
            ? ModSnapshotUploadResult.Failed(failure.Code, failure.Message ?? "failed") : ModSnapshotUploadResult.Ok(result.AcceptedSnapshotId!, result.SourcePath!);
        public string Failed(string status, string? code, string? reason) => "failure:" + code;
        public string Succeeded(string? id) => "success:" + id;
        public string Exception(string status, Exception ex) => "exception:" + ex.Message;
        public void DefaultStatus(string message) { }
        public void Message(string message, bool accepted, bool bestEffort) { }
        public void Diagnose(LocalMutationKind operation, string message, Exception? exception) { }
        public void Dispose() { Sync.Reset(); ClientSessionWork.Reset(); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
