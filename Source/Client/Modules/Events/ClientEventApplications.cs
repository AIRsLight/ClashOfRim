using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.EventLetters;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Events;

// Owns application intent, admission and effect/confirmation ordering. Network
// hydration never runs inside a publication scope; only owned game effects do.
internal sealed class ClientEventApplications : IClientAutomaticEventApplications
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientEventInbox inbox;
    private readonly IClientSnapshotGame game;
    private readonly IClientEventApplicationRuntime runtime;
    private readonly IClientGiftPackageHydration hydration;
    private readonly IClientEventApplicationConfirmations confirmations;
    private readonly IClientEventApplicationDecisions decisions;
    private readonly IClientEventApplicationPresentation presentation;
    private readonly ClientSnapshotFailureRecovery recovery;
    private readonly ClientEventDelivery delivery;
    private readonly ClientGameRequestTracker letterRequests;

    internal ClientEventApplications(ClientSessionLifecycle session, ClientSynchronizationCoordinator synchronization,
        ClientLocalMutationCoordinator mutations, ClientEventInbox inbox, IClientSnapshotGame game,
        IClientEventApplicationRuntime runtime, IClientGiftPackageHydration hydration,
        IClientEventApplicationConfirmations confirmations, IClientEventApplicationDecisions decisions,
        IClientEventApplicationPresentation presentation, ClientSnapshotFailureRecovery recovery, ClientEventDelivery delivery)
    {
        this.session = session; this.synchronization = synchronization; this.mutations = mutations; this.inbox = inbox;
        this.game = game; this.runtime = runtime; this.hydration = hydration; this.confirmations = confirmations;
        this.decisions = decisions; this.presentation = presentation; this.recovery = recovery; this.delivery = delivery;
        letterRequests = new(session, game, static () => { });
    }

    internal event Action<string>? RefreshRequested;
    internal event Action<string, string>? Deferred;
    internal event Action? CompletedWithoutConfirmation;

    // Called during the read request's owned publication, not after queue dispatch.
    public void QueueAutomatic(IReadOnlyCollection<ModEventDetailDto> details, string reason)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var scope = Capture();
        var copy = details.Select(d => d.Copy()).ToArray();
        scope.Game.Enqueue(() => Apply(scope, copy, automatic: true, reason), static () => { });
    }

    // A displayed choice keeps its identity, while admission uses the latest snapshot.
    // Do not hold publication across HandleLetterAction: gift hydration may wait on IO.
    internal Func<ClashOfRimEventLetterActionKind, bool> BindLetterActions(string eventId, Func<bool> sourceCurrent)
    {
        var owner = letterRequests.Capture();
        bool selected = false, running = false;
        bool Current() => sourceCurrent() && !session.IsUnavailable && owner.Boundary.HasGame
            && letterRequests.TryApply(owner, static () => { });
        return action =>
        {
            if (selected || running || !Current()) return false;
            running = true;
            try
            {
                selected = HandleLetterAction(eventId, action, Current);
                return selected && Current();
            }
            finally { running = false; }
        };
    }

    internal bool HandleLetterAction(string eventId, ClashOfRimEventLetterActionKind action, Func<bool>? sourceCurrent = null)
    {
        Scope scope;
        ModEventDetailDto detail;
        using (ClientSessionWork.EnterPublication())
        {
            scope = Capture();
            bool Current() => (sourceCurrent?.Invoke() ?? true) && Owns(scope);
            if (!Current()) return false;
            var found = inbox.FindDetail(eventId);
            if (found is null)
            {
                presentation.Report(new(ClientEventApplicationStage.MissingDetail, EventId: eventId));
                if (!Current()) return false;
                string reason = presentation.Reason(ClientEventApplicationReason.MissingDetail);
                if (Current()) RefreshRequested?.Invoke(reason);
                return false;
            }
            detail = found;
            if (!runtime.CanRun(detail, action, inbox.GroupOf(eventId) ?? "Unknown") || !Current()) return false;
            bool accept = action == ClashOfRimEventLetterActionKind.Accept;
            if (ClientEventDiplomacyPolicy.IsDiplomacyEvent(detail))
            {
                string reason = presentation.Reason(accept ? ClientEventApplicationReason.AcceptLetter : ClientEventApplicationReason.RejectLetter);
                if (!Current()) return false;
                return decisions.RespondDiplomacy(detail.EventId, detail.Actor?.UserId ?? string.Empty, detail.EventType,
                    accept, reason, out _);
            }
            if (!accept)
                return Reject(scope, detail, ClientEventApplicationReason.RejectLetter, sourceCurrent);
        }
        return Apply(scope, new[] { detail }, automatic: false, string.Empty, sourceCurrent);
    }

    internal void ProcessFirstGift(GiftClientDecision decision)
    {
        Scope scope;
        ModEventDetailDto? detail;
        using (ClientSessionWork.EnterPublication())
        {
            detail = FirstGift();
            if (detail is null) return;
            var action = decision == GiftClientDecision.Accept ? ClashOfRimEventLetterActionKind.Accept : ClashOfRimEventLetterActionKind.Reject;
            if (!runtime.CanRun(detail, action, inbox.GroupOf(detail.EventId) ?? "Unknown")) return;
            scope = Capture();
            // Preserve the diagnostic Reject preview; the actual command has its own entry point.
            if (decision != GiftClientDecision.Accept)
            {
                if (!Admit(scope, false, ClientEventApplicationKind.Gift) || synchronization.IsBusy) return;
                var prepared = runtime.PrepareGift(detail, decision, scope.Context, presentation.Reason(ClientEventApplicationReason.RejectGift));
                if (Owns(scope)) presentation.Report(new(ClientEventApplicationStage.Prepared, Prepared: prepared));
                return;
            }
        }
        Apply(scope, new[] { detail }, automatic: false, string.Empty);
    }

    internal void RejectFirstGift()
    {
        using var publication = ClientSessionWork.EnterPublication();
        var scope = Capture();
        var detail = FirstGift();
        if (detail is not null && runtime.CanRun(detail, ClashOfRimEventLetterActionKind.Reject, inbox.GroupOf(detail.EventId) ?? "Unknown"))
            Reject(scope, detail, ClientEventApplicationReason.RejectGift);
    }

    internal void ConfirmFirstGift()
    {
        using var publication = ClientSessionWork.EnterPublication();
        if (inbox.FirstPendingConfirmation is { } id) _ = confirmations.GiftAsync(id, "ItemDeliveryAnchored");
        else presentation.Report(new(ClientEventApplicationStage.NoPending));
    }

    private ModEventDetailDto? FirstGift()
    {
        var detail = inbox.CopyDetails().FirstOrDefault(d => runtime.Classify(d) == ClientEventApplicationKind.Gift);
        if (detail is null) presentation.Report(new(ClientEventApplicationStage.NoGift));
        return detail;
    }

    private bool Reject(Scope scope, ModEventDetailDto detail, ClientEventApplicationReason reason, Func<bool>? sourceCurrent = null)
    {
        bool Current() => (sourceCurrent?.Invoke() ?? true) && Owns(scope);
        if (!Current()) return false;
        var kind = runtime.Classify(detail);
        bool support = kind is ClientEventApplicationKind.SupportArrival or ClientEventApplicationKind.SupportReturn;
        if (support && reason == ClientEventApplicationReason.RejectLetter) reason = ClientEventApplicationReason.RejectSupportLetter;
        string text = presentation.Reason(reason);
        if (!Current()) return false;
        if (support)
            return decisions.RejectSupport(detail.EventId, text, out _);
        if (!Admit(scope, false, ClientEventApplicationKind.Gift) || !Current()) return false;
        var prepared = runtime.PrepareGift(detail, GiftClientDecision.Reject, scope.Context, text);
        if (!Current()) return false;
        if (prepared.RejectionRequest is not { } request)
        {
            presentation.Report(new(ClientEventApplicationStage.Prepared, EventId: detail.EventId, Prepared: prepared));
            return false;
        }
        return decisions.RejectGift(request.EventId, request.Reason, out _);
    }

    private bool Apply(Scope scope, IReadOnlyCollection<ModEventDetailDto> details, bool automatic, string reason, Func<bool>? sourceCurrent = null)
    {
        Attempt attempt;
        using (ClientSessionWork.EnterPublication())
        {
            if (!(sourceCurrent?.Invoke() ?? true) || !Owns(scope)) return false;
            var kind = automatic ? ClientEventApplicationKind.Other : runtime.Classify(details.First());
            if (!Owns(scope) || !Admit(scope, automatic, kind, reason)) return false;
            var operation = automatic || kind is ClientEventApplicationKind.SupportReturn or ClientEventApplicationKind.RaidLoss
                ? LocalMutationKind.AutomaticEvents : kind == ClientEventApplicationKind.SupportArrival ? LocalMutationKind.SupportArrival : LocalMutationKind.Gift;
            string status = presentation.Format(new(ClientEventApplicationStage.Applying, automatic, Kind: kind));
            if (!(sourceCurrent?.Invoke() ?? true) || !Owns(scope)) return false;
            if (!synchronization.TryBeginManual(out var lease))
            {
                var busy = new ClientEventApplicationNotice(ClientEventApplicationStage.Busy, automatic, Kind: kind);
                presentation.Report(busy);
                if (automatic) Deferred?.Invoke(reason, presentation.Format(busy));
                return false;
            }
            mutations.Begin(operation, status);
            attempt = new(scope, lease, mutations.Current!, automatic);
        }
        try
        {
            foreach (var detail in details)
            {
                if (!ApplyOne(attempt, detail)) break;
            }
            if (automatic && attempt.Applications.Count > 0)
                WithOwned(attempt, () => presentation.Report(new(ClientEventApplicationStage.Confirming, true, Count: attempt.Applications.Count)));
        }
        catch (Exception ex)
        {
            WithOwned(attempt, () =>
            {
                var notice = new ClientEventApplicationNotice(ClientEventApplicationStage.Exception, automatic, Message: ex.Message, Exception: ex);
                // An exception inside an engine write has no proven rollback. Keep the
                // pending mutation and already tracked gifts; never upload an uncertain save.
                if (attempt.WritingGame)
                    recovery.Show(attempt.Mutation.Kind, presentation.Format(notice), static () => { });
                presentation.Report(notice);
            });
        }
        finally
        {
            try { Finish(attempt); }
            finally { attempt.Lease.Dispose(); }
        }
        if (automatic)
            ClientSessionWork.TryApply(() => { if (Owns(scope)) delivery.Post(details, reason); });
        return attempt.Applications.Count > 0;
    }

    private bool ApplyOne(Attempt attempt, ModEventDetailDto detail)
    {
        GiftLandingPlan? plan = null;
        var kind = ClientEventApplicationKind.Other;
        bool eligible = false;
        if (!WithOwned(attempt, () =>
        {
            if (!Eligible(attempt, detail)) return;
            kind = runtime.Classify(detail);
            if (kind == ClientEventApplicationKind.Gift)
            {
                var prepared = runtime.PrepareGift(detail, GiftClientDecision.Accept, attempt.Scope.Context,
                    attempt.Automatic ? null : presentation.Reason(ClientEventApplicationReason.RejectGift));
                if (!Owns(attempt)) return;
                plan = prepared.LandingPlan;
                if (plan is null)
                {
                    presentation.Report(new(ClientEventApplicationStage.Prepared, attempt.Automatic, detail.EventId, kind, prepared.Message, Prepared: prepared));
                    return;
                }
            }
            eligible = true;
        })) return false;
        if (!eligible) return true;

        if (plan is not null)
        {
            // Deliberately outside WithOwned / EnterPublication. The captured identity
            // and token also reach every package request through the hydration port.
            var result = hydration.HydrateAsync(plan, attempt.Scope.Context, attempt.Scope.Owner.Token).GetAwaiter().GetResult();
            if (!result.Success)
                return WithOwned(attempt, () => presentation.Report(new(ClientEventApplicationStage.HydrationFailed,
                    attempt.Automatic, detail.EventId, kind, result.Message)));
        }

        return WithOwned(attempt, () =>
        {
            if (!Eligible(attempt, detail)) return;
            attempt.WritingGame = true;
            var applied = runtime.Apply(detail, kind, plan, attempt.Scope.Context);
            if (!Owns(attempt)) return;
            attempt.WritingGame = false;
            if (applied.Applied)
            {
                // Record the intent before any presentation/letter effect can throw.
                attempt.Applications.Add(new() { EventId = detail.EventId, SourceEventId = applied.SourceEventId,
                    ClientApplicationResult = applied.ApplicationResult });
                if (kind == ClientEventApplicationKind.Gift) inbox.TryTrackPendingConfirmation(detail.EventId);
                if (attempt.Automatic) runtime.MarkHandled(detail.EventId);
                else if (kind is ClientEventApplicationKind.SupportArrival or ClientEventApplicationKind.SupportReturn) runtime.RemoveLetters(detail.EventId);
            }
            else if (applied.TerminalFailure)
                _ = decisions.ReportFailureAsync(detail.EventId, applied.SourceEventId, applied.Message);
            if (Owns(attempt)) presentation.Report(new(applied.Applied ? ClientEventApplicationStage.Applied : ClientEventApplicationStage.Failed,
                attempt.Automatic, detail.EventId, kind, applied.Message, Pending: inbox.PendingConfirmationCount, Gift: applied.Gift));
        });
    }

    private bool Eligible(Attempt attempt, ModEventDetailDto detail) => !inbox.HasPendingConfirmation(detail.EventId)
        && !attempt.Applications.Any(a => a.EventId == detail.EventId)
        && (attempt.Automatic
            ? (inbox.IsInGroup(detail.EventId, "DirectlyProcessable") || inbox.IsInGroup(detail.EventId, "DeliveredUnconfirmed")) && runtime.CanAutoApply(detail)
            : runtime.CanRun(detail, ClashOfRimEventLetterActionKind.Accept, inbox.GroupOf(detail.EventId) ?? "Unknown"));

    private void Finish(Attempt attempt) => WithOwned(attempt, () =>
    {
        // Handoff and synchronous snapshot preparation share this publication scope.
        // Disposing the old manual lease again cannot release the workflow's new lease.
        attempt.Lease.Dispose();
        if (attempt.Applications.Count == 0)
        {
            mutations.Reset();
            CompletedWithoutConfirmation?.Invoke();
        }
        else if (attempt.Mutation.Kind == LocalMutationKind.Gift)
            _ = confirmations.GiftAsync(attempt.Applications[0].EventId, attempt.Applications[0].ClientApplicationResult);
        else if (attempt.Mutation.Kind == LocalMutationKind.SupportArrival)
            _ = confirmations.SupportArrivalAsync(attempt.Applications[0].EventId);
        else
        {
            _ = confirmations.AutomaticAsync(attempt.Applications);
        }
    });

    private bool Admit(Scope scope, bool automatic, ClientEventApplicationKind kind, string reason = "")
    {
        var admission = mutations.CheckAdmission(scope.Context.IsConfigured, !string.IsNullOrWhiteSpace(scope.Context.CurrentSnapshotId));
        if (admission == LocalMutationAdmission.Allowed) return true;
        var notice = new ClientEventApplicationNotice(ClientEventApplicationStage.AdmissionFailed, automatic, Kind: kind,
            Admission: admission, Mutation: mutations.Current);
        presentation.Report(notice);
        if (automatic && admission == LocalMutationAdmission.PendingMutation) Deferred?.Invoke(reason, presentation.Format(notice));
        return false;
    }

    private Scope Capture() => new(session.CaptureOwnership(), session.ReadContext(), game.BindCurrentGame());
    private bool Owns(Scope scope) => session.Owns(scope.Owner, scope.Context, requireSnapshot: true) && scope.Game.HasGame && scope.Game.IsCurrent;
    private bool Owns(Attempt attempt) => Owns(attempt.Scope) && attempt.Lease.IsCurrent && ReferenceEquals(mutations.Current, attempt.Mutation);
    private bool WithOwned(Attempt attempt, Action action)
    {
        bool applied = false;
        ClientSessionWork.TryApply(() => { if (Owns(attempt)) { action(); applied = true; } });
        return applied;
    }
    private sealed record Scope(ClientSessionLifecycle.Ownership Owner, ClashOfRimClientNetworkContext Context, IClientSnapshotGameSession Game);
    private sealed record Attempt(Scope Scope, ClientSynchronizationCoordinator.Lease Lease, LocalMutationState Mutation, bool Automatic)
    {
        internal readonly List<ModConfirmEventApplicationEntryDto> Applications = new();
        internal bool WritingGame;
    }
}
