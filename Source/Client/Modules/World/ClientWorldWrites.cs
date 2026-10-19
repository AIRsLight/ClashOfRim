using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Modules.World;

internal enum ClientWorldWriteKind { Baseline, Storyteller }
internal enum ClientWorldWriteMessage { Reject, Progress, Success }
internal interface IClientWorldWriteRuntime
{
    bool IsObserving { get; }
    void Enqueue(Action action, Action discard);
}
internal interface IClientWorldWritePresentation : IClientWorldPreparationPresentation
{
    string CaptureFailure(ClientWorldBaselineCapture.Failure failure);
    void Status(string text);
    void Message(string text, ClientWorldWriteMessage kind);
}

// Both administrator write entry points share capture, admission, submission
// and completion. The initial server setup has additional provisioning steps.
internal sealed class ClientWorldWrites
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientAdminAccess access;
    private readonly ClientWorldBaselineCapture capture;
    private readonly ClientWorldBaselineSubmission submission;
    private readonly ClientStorytellerConfiguration storyteller;
    private readonly ClientWorldPublication publication;
    private readonly IClientWorldWriteRuntime runtime;
    private readonly IClientWorldWritePresentation presentation;
    internal event Action<Func<Action, bool>>? BaselineUpdated;
    internal ClientWorldWrites(ClientSessionLifecycle session, ClientAdminAccess access, ClientWorldBaselineCapture capture,
        ClientWorldBaselineSubmission submission, ClientStorytellerConfiguration storyteller, ClientWorldPublication publication,
        IClientWorldWriteRuntime runtime, IClientWorldWritePresentation presentation)
    { this.session = session; this.access = access; this.capture = capture; this.submission = submission; this.storyteller = storyteller; this.publication = publication; this.runtime = runtime; this.presentation = presentation; }

    internal bool OpenStorytellerEdit(out StorytellerEdit? edit)
    {
        using var p = ClientSessionWork.EnterPublication();
        edit = null;
        var source = submission.Capture();
        // A page opened outside a multiplayer world must not become a server
        // write merely because the player signs in before closing it.
        if (!session.HasSnapshotSession(source.Context) || runtime.IsObserving) return true;
        var permission = access.CurrentPermission;
        bool Current() => OwnsEdit(source, permission);
        if (!Current()) return false;
        if (!permission.Allowed)
        {
            string message = presentation.Text("ClashOfRim.Storyteller.SettingsLocked");
            if (Current()) presentation.Message(message, ClientWorldWriteMessage.Reject);
            return false;
        }
        var initial = storyteller.Capture();
        if (!Current()) return false;
        if (initial.CaptureFailure is { } failure)
        {
            string message = presentation.Text("ClashOfRim.Storyteller.AdminSyncFailed", ("CODE", "CaptureFailed"), ("MESSAGE", failure));
            if (Current()) presentation.Message(message, ClientWorldWriteMessage.Reject);
            return false;
        }
        edit = new(this, source, permission, initial.Signature);
        return true;
    }
    private bool OwnsEdit(ClientWorldBaselineSubmission.Captured source, ClientAdminAccess.Permission permission)
        => !session.IsUnavailable && !runtime.IsObserving && submission.IsCurrent(source)
            && ReferenceEquals(permission, access.CurrentPermission);
    internal sealed class StorytellerEdit
    {
        private readonly ClientWorldWrites owner;
        private readonly ClientWorldBaselineSubmission.Captured source;
        private readonly ClientAdminAccess.Permission permission;
        private readonly string signature;
        private bool closed;
        internal StorytellerEdit(ClientWorldWrites owner, ClientWorldBaselineSubmission.Captured source, ClientAdminAccess.Permission permission, string signature)
        { this.owner = owner; this.source = source; this.permission = permission; this.signature = signature; }
        internal bool IsCurrent => !closed && owner.OwnsEdit(source, permission);
        internal Task CloseAsync(Func<ModColonyAppearanceDto?> captureAppearance)
        {
            using var p = ClientSessionWork.EnterPublication();
            if (closed) return Task.CompletedTask;
            closed = true;
            if (!owner.OwnsEdit(source, permission)) return Task.CompletedTask;
            var appearance = captureAppearance();
            return owner.OwnsEdit(source, permission)
                ? owner.StartAsync(ClientWorldWriteKind.Storyteller, appearance, signature) : Task.CompletedTask;
        }
    }

    internal async Task StartAsync(ClientWorldWriteKind kind, ModColonyAppearanceDto? appearance, string? initialSignature = null)
    {
        var observation = publication.Capture();
        bool story = kind == ClientWorldWriteKind.Storyteller;
        var source = submission.Capture();
        if (story && (!source.Context.IsConfigured || string.IsNullOrWhiteSpace(source.Context.CurrentSnapshotId)
            || string.IsNullOrWhiteSpace(session.SessionId) || runtime.IsObserving || !access.IsAdministrator)) return;
        var ticket = access.Begin(out string? denial, () => submission.IsCurrent(source) && (!story || !runtime.IsObserving));
        if (ticket is null)
        {
            if (denial is not null)
            {
                string text = presentation.Text(denial);
                if (submission.IsCurrent(source)) { if (story) presentation.Message(text, ClientWorldWriteMessage.Reject); else presentation.Status(text); }
            }
            return;
        }
        ClientWorldBaselineSubmission.Prepared? prepared = null;
        bool Current() => access.IsCurrent(ticket) && (prepared is null || submission.IsCurrent(prepared));
        bool Apply(Action action)
        {
            bool applied = false;
            access.TryApply(ticket, () => { if (prepared is null || submission.IsCurrent(prepared)) { action(); applied = true; } });
            return applied;
        }
        bool Step(Action action) => Apply(action) && Current();
        void Notice(string key, ClientWorldWriteMessage? message = null, params (string Name, object? Value)[] args)
        {
            string text = "";
            if (!Step(() => text = presentation.Text(key, args)) || !Step(() => presentation.Status(text))) return;
            if (message is { } value) Apply(() => presentation.Message(text, value));
        }
        Task Publish(Action action)
        {
            bool published = false;
            return ClientGamePublication.EnqueueAsync(runtime.Enqueue, ticket.Request.Scope.Owner.Token, Apply,
                () => { if (published) return; published = true; action(); });
        }
        bool sending = false;
        try
        {
            if (!Current()) return;
            if (story)
            {
                var current = storyteller.Capture();
                if (!Current()) return;
                if (current.CaptureFailure is { } failure) throw new InvalidOperationException(failure);
                if (!string.IsNullOrWhiteSpace(initialSignature) && string.Equals(initialSignature, current.Signature, StringComparison.Ordinal)) return;
            }
            var captured = capture.Capture(source, appearance, storytellerOnly: story, current: Current);
            prepared = captured.Prepared;
            if (!Current()) return;
            if (captured.Error is { } error)
            {
                string reason = "";
                if (!Step(() => reason = presentation.CaptureFailure(error))) return;
                if (story) Notice("ClashOfRim.Storyteller.AdminSyncFailed", ClientWorldWriteMessage.Reject, ("CODE", error.Code), ("MESSAGE", reason));
                else Notice("ClashOfRim.Admin.WorldBaselineInvalid", ClientWorldWriteMessage.Reject, ("REASON", reason));
                return;
            }
            if (prepared is null) return;
            var original = ClientStorytellerBaseline.From(captured.Configuration!);
            Notice(story ? "ClashOfRim.Storyteller.AdminSyncing" : "ClashOfRim.Admin.WorldBaselineUpdating", story ? ClientWorldWriteMessage.Progress : null);
            sending = true;
            await ClientSessionWork.Run(async () =>
            {
                if (!Current()) return;
                var result = await submission.SubmitAsync(prepared, Current).ConfigureAwait(false);
                if (result is null || !Current()) return;
                if (!result.Success || result.Response?.Result is null)
                {
                    await Publish(() => Notice(story ? "ClashOfRim.Storyteller.AdminSyncFailed" : "ClashOfRim.Admin.StatusFailed",
                        story ? ClientWorldWriteMessage.Reject : null, ("CODE", result.ErrorCode ?? ""), ("MESSAGE", result.Message ?? ""))).ConfigureAwait(false);
                    return;
                }
                if (!result.Response.Result.Accepted)
                {
                    await Publish(() => Notice(story ? "ClashOfRim.Storyteller.AdminSyncRejected" : "ClashOfRim.Admin.StatusRejected",
                        story ? ClientWorldWriteMessage.Reject : null, ("CODE", result.Response.Result.ErrorCode), ("MESSAGE", result.Response.Result.Message ?? ""))).ConfigureAwait(false);
                    return;
                }
                var accepted = result.Response.WorldConfiguration;
                await Publish(() => submission.Complete(prepared, () =>
                {
                    if (accepted is not null)
                    {
                        if (!publication.Publish(observation, accepted, story ? ClientWorldProjection.Storyteller : ClientWorldProjection.Full, Current)) return;
                    }
                    if (story) Notice("ClashOfRim.Storyteller.AdminSynced", ClientWorldWriteMessage.Success,
                        ("STORYTELLER", accepted?.StorytellerDefName ?? original.Storyteller ?? ""), ("DIFFICULTY", accepted?.DifficultyDefName ?? original.Difficulty ?? ""));
                    else
                    {
                        Notice("ClashOfRim.Admin.WorldBaselineUpdated", ClientWorldWriteMessage.Success,
                            ("FACTIONS", accepted?.Factions.Count ?? 0), ("WORLDOBJECTS", accepted?.WorldObjects.Count ?? 0));
                        Step(() => BaselineUpdated?.Invoke(Step));
                    }
                })).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            void Failure()
            {
                string key = story ? (sending ? "ClashOfRim.Storyteller.AdminSyncException" : "ClashOfRim.Storyteller.AdminSyncFailed")
                    : sending ? "ClashOfRim.Admin.StatusException" : "ClashOfRim.Admin.WorldBaselineCaptureFailed";
                Notice(key, story || !sending ? ClientWorldWriteMessage.Reject : null, ("TYPE", ex.GetType().Name), ("CODE", ex.GetType().Name), ("MESSAGE", ex.Message));
                Apply(() => presentation.Diagnose("World " + kind + " update failed: " + ex));
            }
            if (sending) await Publish(Failure).ConfigureAwait(false); else Failure();
        }
        finally { if (prepared is not null) submission.Discard(prepared); access.End(ticket); }
    }
}
