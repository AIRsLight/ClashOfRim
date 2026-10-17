using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Events;

internal enum ClientEventRefreshStage { InitialQueue, InitialDetails, InitialAction, AdmissionFailed, Busy, Started, Merged, Deferred, Updated, AdoptedQueue }
internal sealed record ClientEventRefreshNotice(ClientEventRefreshStage Stage, ClientEventReadKind Kind = ClientEventReadKind.Queue,
    string Reason = "", string? Status = null, LocalMutationAdmission Admission = LocalMutationAdmission.Allowed,
    LocalMutationState? Mutation = null, ClientEventReadUpdate? Update = null, ModEventQueueSummaryDto? Queue = null);
internal interface IClientEventReadPresentation
{
    string Format(ClientEventRefreshNotice notice);
    void Diagnose(ClientEventRefreshNotice notice);
    string MergedReason { get; }
    string ManualDetailsReason { get; }
}
internal interface IClientAutomaticEventApplications
{
    void QueueAutomatic(IReadOnlyCollection<ModEventDetailDto> details, string reason);
}
internal interface IClientEventLetterDelivery
{
    void Post(IReadOnlyCollection<ModEventDetailDto> details, string source);
}

// Owns refresh scheduling, status and the notification cursor shared with the stream.
// The reader owns HTTP ordering/response publication; application and letter modules
// retain their effects. The host only connects these domain objects.
internal sealed class ClientEventRefresh
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientEventReader reader;
    private readonly ClientEventInbox inbox;
    private readonly ClientEventRefreshQueue automatic;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly IClientAutomaticEventApplications applications;
    private readonly IClientEventLetterDelivery letters;
    private readonly IClientEventReadPresentation presentation;
    private readonly ClientEventState state;

    internal ClientEventRefresh(ClientSessionLifecycle session, ClientEventReader reader, ClientEventInbox inbox,
        ClientSynchronizationCoordinator synchronization, ClientLocalMutationCoordinator mutations,
        IClientAutomaticEventApplications applications, IClientEventLetterDelivery letters, IClientEventReadPresentation presentation, ClientEventState state)
    {
        this.session = session; this.reader = reader; this.inbox = inbox; this.synchronization = synchronization;
        this.mutations = mutations; this.applications = applications; this.letters = letters; this.presentation = presentation; this.state = state;
        automatic = new(reader);
        reader.Updated += Present;
    }

    internal void AdoptQueue(ModEventQueueSummaryDto? queue)
    {
        using var publication = ClientSessionWork.EnterPublication();
        inbox.ReplaceQueue(queue);
        state.Publish(new(ClientEventRefreshStage.AdoptedQueue, Queue: queue));
    }

    internal Task StartManual(ClientEventReadKind kind)
    {
        using var publication = ClientSessionWork.EnterPublication();
        if (!Admit(kind)) return Task.CompletedTask;
        if (!synchronization.TryBeginManual(out var lease))
        {
            state.Publish(new(ClientEventRefreshStage.Busy, kind));
            return Task.CompletedTask;
        }
        try
        {
            var request = reader.Capture(kind, kind == ClientEventReadKind.Details ? presentation.ManualDetailsReason : "manual-refresh", state.NotificationVersion);
            state.Publish(new(ClientEventRefreshStage.Started, kind));
            return ClientSessionWork.Run(async () =>
            {
                Task deferred = Task.CompletedTask;
                try { await reader.ReadAsync(request).ConfigureAwait(false); }
                finally
                {
                    lease.Dispose();
                    ClientSessionWork.TryApply(() => { if (reader.Owns(request.Scope)) deferred = ResumeDeferred(); });
                }
                await deferred.ConfigureAwait(false);
            });
        }
        catch { lease.Dispose(); throw; }
    }

    internal Task StartAutomatic(string reason)
    {
        using var publication = ClientSessionWork.EnterPublication();
        if (!Admit(ClientEventReadKind.Automatic, reason)) return Task.CompletedTask;
        var attempt = automatic.Begin();
        if (attempt is null)
        {
            state.Publish(new(ClientEventRefreshStage.Merged, ClientEventReadKind.Automatic, reason));
            return Task.CompletedTask;
        }
        var request = reader.Capture(ClientEventReadKind.Automatic, reason);
        state.Publish(new(ClientEventRefreshStage.Started, ClientEventReadKind.Automatic, reason));
        return ClientSessionWork.Run(async () =>
        {
            var completion = ClientEventReadCompletion.Failed;
            Task next = Task.CompletedTask;
            try { completion = await reader.ReadAsync(request).ConfigureAwait(false); }
            finally
            {
                ClientSessionWork.TryApply(() =>
                {
                    if (!reader.Owns(request.Scope)) return;
                    // A snapshot may advance while HTTP is in flight. Discard that
                    // result, release the coalescer, then read the new baseline once.
                    if (completion == ClientEventReadCompletion.Obsolete || !reader.TryApply(request, static () => { })) automatic.Defer();
                    if (automatic.Complete(attempt)) next = StartAutomatic(presentation.MergedReason);
                });
            }
            await next.ConfigureAwait(false);
        });
    }

    internal void Defer(string reason, string status)
    {
        using var publication = ClientSessionWork.EnterPublication();
        automatic.Defer();
        state.Publish(new(ClientEventRefreshStage.Deferred, ClientEventReadKind.Automatic, reason, status));
    }

    internal Task ResumeDeferred()
    {
        using var publication = ClientSessionWork.EnterPublication();
        return automatic.TakeDeferred() ? StartAutomatic(presentation.MergedReason) : Task.CompletedTask;
    }

    // Continuation for a domain that already holds manual admission (currently trade).
    // Capture before its request starts; a later read cannot borrow another login/Game.
    internal ClientEventReader.Request CaptureQueueRead() => reader.Capture(ClientEventReadKind.Queue);
    internal Task ReadQueueAsync(ClientEventReader.Request request) => reader.ReadAsync(request);

    private bool Admit(ClientEventReadKind kind, string reason = "")
    {
        var context = session.ReadContext();
        var admission = mutations.CheckAdmission(context.IsConfigured, !string.IsNullOrWhiteSpace(context.CurrentSnapshotId));
        if (admission == LocalMutationAdmission.Allowed) return true;
        var notice = new ClientEventRefreshNotice(ClientEventRefreshStage.AdmissionFailed, kind, reason, Admission: admission, Mutation: mutations.Current);
        if (kind == ClientEventReadKind.Automatic && admission == LocalMutationAdmission.PendingMutation)
            Defer(reason, presentation.Format(notice));
        else state.Publish(notice);
        return false;
    }

    private void Present(ClientEventReadUpdate update)
    {
        // Invoked by the reader under its exact identity, baseline and game scope.
        if (update.Failure is null && update.Request.Kind == ClientEventReadKind.Wait)
            state.ObserveNotificationVersion(update.NotificationVersion);
        state.Publish(new(ClientEventRefreshStage.Updated, update.Request.Kind, Update: update));
        if (update.Failure is not null || update.Empty || update.Stage != ClientEventReadStage.Details) return;
        // Presentation is an external adapter and can re-enter game/session code.
        reader.TryApply(update.Request, () =>
        {
            if (update.Request.Kind == ClientEventReadKind.Automatic)
                applications.QueueAutomatic(update.Details!, update.Request.Reason);
            else update.Request.Scope.Game.Enqueue(() => reader.TryApply(update.Request,
                () => letters.Post(update.Details!, update.Request.Reason)), static () => { });
        });
    }

}
