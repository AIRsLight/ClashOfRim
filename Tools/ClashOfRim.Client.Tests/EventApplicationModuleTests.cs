using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.EventLetters;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Protocol;

internal static class EventApplicationModuleTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        try
        {
            ManualCommands();
            await AutomaticAndPartialFailure();
            await HydrationOwnership();
            await QueuedOwnership();
            await ReentrantEffects();
        }
        finally { ClientSessionWork.Reset(); }
    }

    private static void ManualCommands()
    {
        foreach (var kind in new[] { ClientEventApplicationKind.Gift, ClientEventApplicationKind.SupportArrival, ClientEventApplicationKind.SupportReturn })
        {
            var f = new Fixture(); f.Add("é", kind);
            Require(f.Module.HandleLetterAction("é", ClashOfRimEventLetterActionKind.Accept), "Accept failed: " + kind);
            Require(f.Applied.SequenceEqual(new[] { "é" }) && f.Confirmed.Single().EventId == "é", "Effect/confirmation mismatch.");
            Require(f.NextLease?.IsCurrent == true, "Application finally released confirmation lease.");
            Require(f.Confirmed[0].ClientApplicationResult == ResultName(kind), "Wrong application semantic: " + kind);
            Require(!f.Module.HandleLetterAction("é", ClashOfRimEventLetterActionKind.Accept) && f.Applied.Count == 1, "Duplicate accept applied twice.");
        }
        var preview = new Fixture(); preview.Add("gift");
        preview.Module.ProcessFirstGift(GiftClientDecision.Reject);
        Require(preview.Commands.Count == 0 && preview.Applied.Count == 0 && !preview.Mutations.IsPending, "Reject preview sent command.");
        preview.Module.RejectFirstGift();
        Require(preview.Commands.Single() == "reject-gift:gift" && preview.Prepared == 2, "Reject did not reuse processor.");
        preview.Module.HandleLetterAction("missing", ClashOfRimEventLetterActionKind.Accept);
        Require(preview.Refreshes == 1, "Missing detail did not request refresh.");
        var first = new Fixture(); first.Add("gift"); first.Module.ProcessFirstGift(GiftClientDecision.Accept);
        Require(first.Prepared == 1 && first.Hydrated == 1 && first.Applied.Count == 1, "First gift bypassed common application path.");
        var confirm = new Fixture(); confirm.Module.ConfirmFirstGift();
        Require(confirm.Confirmed.Count == 0, "Empty pending confirmation sent.");
        confirm.Inbox.TryTrackPendingConfirmation("pending"); confirm.Mutations.Begin(LocalMutationKind.Gift, "pending");
        confirm.Module.ConfirmFirstGift(); Require(confirm.Confirmed.Single().EventId == "pending", "Pending gift not resumed.");
        var blocked = new Fixture(); blocked.Add("gift"); blocked.Mutations.Begin(LocalMutationKind.Bank, "bank");
        Require(!blocked.Module.HandleLetterAction("gift", ClashOfRimEventLetterActionKind.Accept) && blocked.Prepared == 0
            && blocked.Mutations.Current?.Kind == LocalMutationKind.Bank, "Application overwrote unrelated pending mutation.");
        var pending = new Fixture(); pending.Add("gift"); pending.Inbox.TryTrackPendingConfirmation("gift");
        Require(!pending.Module.HandleLetterAction("gift", ClashOfRimEventLetterActionKind.Accept) && pending.Applied.Count == 0
            && !pending.Sync.IsBusy && !pending.Mutations.IsPending, "Tracked gift applied again or leaked admission.");
        var support = new Fixture(); support.Add("support", ClientEventApplicationKind.SupportArrival);
        Require(support.Module.HandleLetterAction("support", ClashOfRimEventLetterActionKind.Reject)
            && support.Commands.Single() == "reject-support:support", "Support rejection routing changed.");
        var diplomacy = new Fixture(); diplomacy.Add("diplomacy", ClientEventApplicationKind.Other, ServerEventType.AllianceRequest);
        Require(diplomacy.Module.HandleLetterAction("diplomacy", ClashOfRimEventLetterActionKind.Accept)
            && diplomacy.Commands.Single() == "diplomacy:True:diplomacy", "Diplomacy did not use existing decision command.");
    }

    private static async Task AutomaticAndPartialFailure()
    {
        var f = new Fixture(); f.Add("gift"); f.Add("return", ClientEventApplicationKind.SupportReturn); f.Add("raid", ClientEventApplicationKind.RaidLoss);
        await f.Auto();
        Require(f.Applied.SequenceEqual(new[] { "gift", "return", "raid" }) && f.Confirmed.Count == 3 && f.Prepared == 1 && f.Hydrated == 1,
            "Automatic domain routing or shared gift path changed.");
        Require(f.Confirmed.Last().SourceEventId == "source-raid" && f.NextLease?.IsCurrent == true, "Raid source or confirmation lease lost.");
        foreach (string stage in new[] { "prepare", "hydrate", "presentation", "handled" })
        {
            var partial = new Fixture(); partial.Add("first"); partial.Add("second");
            if (stage == "prepare") partial.BeforePrepare = id => { if (id == "second") throw new InvalidOperationException("prepare"); };
            if (stage == "hydrate") partial.OnHydrate = (p, _, _) => p.EventId == "second" ? throw new InvalidOperationException("download") : Task.FromResult(PawnPackageTransferResult.Ok());
            if (stage == "presentation") partial.OnReport = n => { if (n.EventId == "first" && n.Stage == ClientEventApplicationStage.Applied) throw new InvalidOperationException("presentation"); };
            if (stage == "handled") partial.OnHandled = () => throw new InvalidOperationException("handled");
            await partial.Auto();
            Require(partial.Confirmed.Single().EventId == "first" && partial.NextLease?.IsCurrent == true
                && partial.Inbox.HasPendingConfirmation("first"), "Earlier success lost after " + stage);
        }
        var uncertain = new Fixture(); uncertain.Add("first"); uncertain.Add("second");
        uncertain.OnApply = id => { if (id == "second") throw new InvalidOperationException("unknown partial game write"); };
        await uncertain.Auto();
        Require(uncertain.Confirmed.Count == 0 && uncertain.Mutations.IsPending && !uncertain.Sync.IsBusy
            && uncertain.Inbox.HasPendingConfirmation("first"), "Uncertain game write was uploaded or prior pending gift was cleared.");
        (await uncertain.Ports.Next())();
        Require(uncertain.View.Visible is not null && uncertain.View.Retry is null, "Uncertain game write offered unsafe replay.");
        var fail = new Fixture(); fail.Add("return", ClientEventApplicationKind.SupportReturn); fail.FailApply = true;
        await fail.Auto();
        Require(fail.Commands.Single() == "failure:return" && fail.Confirmed.Count == 0 && !fail.Mutations.IsPending && fail.Completed == 1,
            "Terminal failure did not use shared report/cleanup.");
        var busy = new Fixture(); busy.Add("gift"); busy.Sync.TryAcquireSnapshot(out var lease);
        await busy.Auto(); Require(busy.Deferred == 1 && lease!.IsCurrent && busy.Applied.Count == 0, "Busy auto lost deferred refresh or existing lease.");
    }

    private static async Task HydrationOwnership()
    {
        foreach (bool automatic in new[] { false, true })
        foreach (string replacement in new[] { "none", "generation", "login", "server", "auth", "colony", "snapshot", "game", "group", "mutation", "lease" })
        {
            var f = new Fixture(); f.Add("gift");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<PawnPackageTransferResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.OnHydrate = (_, context, token) =>
            {
                Require(context.UserId == "alice" && context.CurrentSnapshotId == "base" && context.AuthToken == "auth", "Hydration borrowed identity.");
                entered.SetResult(); return release.Task;
            };
            var choose = f.Module.BindLetterActions("gift", static () => true);
            Func<bool> apply = () => choose(ClashOfRimEventLetterActionKind.Accept);
            if (automatic)
            {
                f.Module.QueueAutomatic(f.Inbox.CopyDetails(), "read");
                var action = await f.Ports.Next();
                apply = () => { action(); return f.Applied.Count > 0; };
            }
            var work = Task.Run(apply);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            LocalMutationState? nextMutation = null;
            ClientSynchronizationCoordinator.Lease? nextLease = null;
            try
            {
                // This has to finish while the download is still held: detects a publication lock around network waits.
                await Task.Run(() =>
                {
                    using var publication = ClientSessionWork.EnterPublication();
                    f.Replace(replacement);
                    if (replacement is "generation" or "login" or "mutation" or "lease")
                    {
                        f.Sync.Reset(); f.Mutations.Begin(LocalMutationKind.Bank, "replacement"); nextMutation = f.Mutations.Current;
                        Require(f.Sync.TryAcquireSnapshot(out nextLease), "Cannot create replacement lease.");
                    }
                }).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { release.TrySetResult(PawnPackageTransferResult.Ok()); }
            bool applied = await work.WaitAsync(TimeSpan.FromSeconds(5));
            Require(applied == (replacement == "none") && f.Applied.Count == (replacement == "none" ? 1 : 0), "Late hydration applied: " + replacement);
            if (nextMutation is not null) Require(ReferenceEquals(f.Mutations.Current, nextMutation) && nextLease!.IsCurrent,
                "Old finalizer changed new state: " + replacement);
            if (replacement != "none") Require(f.Confirmed.Count == 0, "Stale hydration confirmed: " + replacement);
        }
    }

    private static async Task QueuedOwnership()
    {
        foreach (var replacement in new[] { "login", "generation", "snapshot", "game" })
        {
            var f = new Fixture(); var detail = f.Add("gift"); f.Module.QueueAutomatic(new[] { detail }, "read");
            var queued = await f.Ports.Next(); f.Replace(replacement); queued();
            Require(f.Prepared == 0 && f.Applied.Count == 0 && f.Notices.Count == 0, "Old read borrowed new owner: " + replacement);
        }
        var detached = new Fixture(); var original = detached.Add("gift"); detached.Module.QueueAutomatic(new[] { original }, "read");
        original.EventId = "changed"; (await detached.Ports.Next())();
        Require(detached.Applied.Single() == "gift", "Queued batch kept caller-owned detail.");
    }

    private static async Task ReentrantEffects()
    {
        var f = new Fixture(); f.Add("gift"); LocalMutationState? replacement = null;
        f.OnApply = _ =>
        {
            f.Replace("login"); f.Sync.Reset(); f.Mutations.Begin(LocalMutationKind.Bank, "new"); replacement = f.Mutations.Current;
            Require(f.Sync.TryAcquireSnapshot(out var next), "Cannot start reentrant operation."); f.NextLease = next;
        };
        await f.Auto();
        Require(f.Confirmed.Count == 0 && f.Handled.Count == 0 && !f.Inbox.HasPendingConfirmation("gift")
            && ReferenceEquals(f.Mutations.Current, replacement) && f.NextLease!.IsCurrent, "Reentrant game effect contaminated replacement session.");
    }

    internal sealed class Fixture : IClientEventApplicationRuntime, IClientGiftPackageHydration, IClientEventApplicationPresentation,
        IClientEventApplicationConfirmations, IClientEventApplicationDecisions, IClientEventLetterRuntime
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientEventInbox Inbox = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly EventModuleFailureView View;
        internal readonly ClientEventApplications Module;
        internal readonly List<string> Applied = new(), Handled = new(), Commands = new();
        internal readonly List<ModConfirmEventApplicationEntryDto> Confirmed = new();
        internal readonly List<ClientEventApplicationNotice> Notices = new();
        internal ClientSynchronizationCoordinator.Lease? NextLease;
        internal int Prepared, Hydrated, Refreshes, Deferred, Completed;
        internal bool FailApply;
        internal Action<string>? BeforePrepare, OnApply;
        internal Action? OnHandled;
        internal Action? OnReason, OnFormat;
        internal Action<ClientEventApplicationNotice>? OnReport;
        internal Func<GiftLandingPlan, ClashOfRimClientNetworkContext, CancellationToken, Task<PawnPackageTransferResult>>? OnHydrate;
        private readonly Dictionary<string, ClientEventApplicationKind> kinds = new();
        internal Fixture()
        {
            ClientSessionWork.Reset();
            Session = new(Ports, Ports, new ClientSessionConnection(Ports));
            View = new(Ports);
            Module = new(Session, Sync, Mutations, Inbox, Ports, this, this, this, this, this,
                new ClientSnapshotFailureRecovery(Session, Mutations, Sync, Ports, View), new ClientEventDelivery(Inbox, this));
            Module.RefreshRequested += _ => Refreshes++;
            Module.Deferred += (_, _) => Deferred++;
            Module.CompletedWithoutConfirmation += () => Completed++;
        }
        internal ModEventDetailDto Add(string id, ClientEventApplicationKind kind = ClientEventApplicationKind.Gift, ServerEventType type = ServerEventType.ItemDelivery)
        {
            kinds.Add(id, kind); var detail = new ModEventDetailDto { EventId = id, EventType = type };
            Inbox.ReplaceDetails(Inbox.CopyDetails().Append(detail).ToArray());
            Inbox.ReplaceQueue(new() { DirectlyProcessable = kinds.Keys.Select(key => new ModEventReferenceDto { EventId = key }).ToList() });
            return detail;
        }
        internal async Task Auto() { Module.QueueAutomatic(Inbox.CopyDetails(), "read"); (await Ports.Next())(); }
        internal void Replace(string kind)
        {
            switch (kind)
            {
                case "generation": ClientSessionWork.Reset(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth, SessionId = "new" }); break;
                case "server": Ports.Server += "/new"; break;
                case "auth": Ports.Auth = "new"; break;
                case "colony": Ports.Colony = "new"; break;
                case "snapshot": Ports.Snapshot = "new"; break;
                case "game": Ports.GameOwner = new(); break;
                case "group": Inbox.ForgetGroup("gift"); break;
            }
        }
        public ClientEventApplicationKind Classify(ModEventDetailDto detail) => kinds[detail.EventId];
        public bool CanRun(ModEventDetailDto detail, ClashOfRimEventLetterActionKind action, string group) => group == "DirectlyProcessable"
            && action is ClashOfRimEventLetterActionKind.Accept or ClashOfRimEventLetterActionKind.Reject;
        public bool CanAutoApply(ModEventDetailDto detail) => true;
        public ItemDeliveryClientProcessingResult PrepareGift(ModEventDetailDto detail, GiftClientDecision decision, ClashOfRimClientNetworkContext context, string? reason)
        {
            Prepared++; BeforePrepare?.Invoke(detail.EventId);
            return decision == GiftClientDecision.Accept ? ItemDeliveryClientProcessingResult.Accepted(new(detail.EventId, null, "map", null, "DropPod", Array.Empty<GiftItemReference>(), true))
                : ItemDeliveryClientProcessingResult.Rejected(new(detail.EventId, context.UserId!, context.ColonyId!, context.CurrentSnapshotId!, reason));
        }
        public Task<PawnPackageTransferResult> HydrateAsync(GiftLandingPlan plan, ClashOfRimClientNetworkContext context, CancellationToken cancellationToken = default)
        { Hydrated++; return OnHydrate?.Invoke(plan, context, cancellationToken) ?? Task.FromResult(PawnPackageTransferResult.Ok()); }
        public ClientEventApplicationResult Apply(ModEventDetailDto detail, ClientEventApplicationKind kind, GiftLandingPlan? gift, ClashOfRimClientNetworkContext context)
        {
            Applied.Add(detail.EventId); OnApply?.Invoke(detail.EventId);
            return new(!FailApply, "result", ResultName(kind), kind == ClientEventApplicationKind.RaidLoss ? "source-raid" : null, FailApply);
        }
        public void MarkHandled(string id) { Handled.Add(id); OnHandled?.Invoke(); }
        public void RemoveLetters(string id) { Handled.Add(id); OnHandled?.Invoke(); }
        public string Format(ClientEventApplicationNotice notice) { OnFormat?.Invoke(); return notice.Stage.ToString(); }
        public void Report(ClientEventApplicationNotice notice) { Notices.Add(notice); OnReport?.Invoke(notice); }
        public string Reason(ClientEventApplicationReason reason) { OnReason?.Invoke(); return reason.ToString(); }
        public Task GiftAsync(string eventId, string applicationResult) => AutomaticAsync(new[] { new ModConfirmEventApplicationEntryDto { EventId = eventId, ClientApplicationResult = applicationResult } });
        public Task SupportArrivalAsync(string eventId) => GiftAsync(eventId, "SupportPawnArrived");
        public Task AutomaticAsync(IReadOnlyList<ModConfirmEventApplicationEntryDto> applications)
        {
            Require(!Sync.IsBusy && Mutations.IsPending, "Confirmation handoff lost pending intent or retained manual lease.");
            Require(Sync.TryAcquireSnapshot(out var lease), "Cannot acquire confirmation lease."); NextLease = lease;
            Confirmed.AddRange(applications); return Task.CompletedTask;
        }
        public bool RejectGift(string eventId, string? reason, out Task work) { Commands.Add("reject-gift:" + eventId); work = Task.CompletedTask; return true; }
        public bool RejectSupport(string eventId, string? reason, out Task work) { Commands.Add("reject-support:" + eventId); work = Task.CompletedTask; return true; }
        public bool RespondDiplomacy(string eventId, string actor, ServerEventType type, bool accepted, string? reason, out Task work)
        { Commands.Add("diplomacy:" + accepted + ":" + eventId); work = Task.CompletedTask; return true; }
        public Task ReportFailureAsync(string eventId, string? source, string reason) { Commands.Add("failure:" + eventId); return Task.CompletedTask; }
        public bool ShouldPost(ModEventDetailDto detail, string? group) => false;
        public bool HasOpenLetter(string id) => false;
        public void RegisterPendingManualEvent(string id) => throw new InvalidOperationException();
        public void ShowLetter(ModEventDetailDto detail, string group, string source) => throw new InvalidOperationException();
        public ServerNotificationPayloadSummary? ReadNotification(ModEventDetailDto detail) => null;
        public bool ApplyRelation(ClientEventRelationChange change) => throw new InvalidOperationException();
    }
    private static string ResultName(ClientEventApplicationKind kind) => kind switch
    {
        ClientEventApplicationKind.SupportArrival => "SupportPawnArrived",
        ClientEventApplicationKind.SupportReturn => "SupportPawnReturnCaravanCreated",
        ClientEventApplicationKind.RaidLoss => "AlreadyApplied",
        _ => "ItemDeliveryAnchored"
    };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
