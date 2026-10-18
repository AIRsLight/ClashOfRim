using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Events;

internal enum ClientEventConfirmationPurpose { Gift, SupportArrival, Trade, AutomaticEvents }
internal enum ClientEventConfirmationStage { Empty, Uploading, PreparationFailed, Failed, Accepted }
internal sealed record ClientEventConfirmationNotice(ClientEventConfirmationPurpose Purpose, ClientEventConfirmationStage Stage,
    string EventId = "", int Count = 0, string? SnapshotId = null, int Rejected = 0, int Pending = 0,
    ClientEventPreparationFailure? PreparationFailure = null, ClientEventConfirmationProblem? Problem = null);
internal sealed record ClientEventConfirmationProblem(ClientSnapshotTransactionFailureKind Kind, string Code, string Message,
    string Validation, Exception? Exception, bool CanRetry);

internal interface IClientEventConfirmationPresentation
{
    string Format(ClientEventConfirmationNotice notice);
    void Report(ClientEventConfirmationNotice notice, string message);
}
internal interface IClientEventConfirmationEffects { void MarkHandled(string eventId); }

// Owns the confirmation lifecycle. Callers supply business intent; protocol,
// admission, retry ownership and completion are shared by all event domains.
internal sealed class ClientEventConfirmationWorkflow : IClientEventApplicationConfirmations
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientEventConfirmationPreparation preparation;
    private readonly ClientEventConfirmation confirmation;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientSnapshotFailureRecovery recovery;
    private readonly ClientEventInbox inbox;
    private readonly IClientEventConfirmationPresentation presentation;
    private readonly IClientEventConfirmationEffects effects;

    internal ClientEventConfirmationWorkflow(ClientSessionLifecycle session, ClientEventConfirmationPreparation preparation,
        ClientEventConfirmation confirmation, ClientLocalMutationCoordinator mutations, ClientSnapshotFailureRecovery recovery,
        ClientEventInbox inbox, IClientEventConfirmationPresentation presentation, IClientEventConfirmationEffects effects)
    {
        this.session = session; this.preparation = preparation; this.confirmation = confirmation; this.mutations = mutations;
        this.recovery = recovery; this.inbox = inbox; this.presentation = presentation; this.effects = effects;
    }

    internal event Action? Completed;

    public Task GiftAsync(string eventId, string applicationResult) => SingleAsync(ClientEventConfirmationPurpose.Gift,
        LocalMutationKind.Gift, "gift-confirm", eventId, applicationResult);
    public Task SupportArrivalAsync(string eventId) => SingleAsync(ClientEventConfirmationPurpose.SupportArrival,
        LocalMutationKind.SupportArrival, "support-confirm", eventId, "SupportPawnArrived");
    internal Task TradeAsync(string eventId, string sourceEventId, string baseSnapshotId) => SingleAsync(ClientEventConfirmationPurpose.Trade,
        LocalMutationKind.Trade, "trade-confirm", eventId, "SelfDeliveryExchangeApplied", sourceEventId, baseSnapshotId);

    private Task SingleAsync(ClientEventConfirmationPurpose purpose, LocalMutationKind operation, string prefix,
        string eventId, string applicationResult, string? sourceEventId = null, string? baseSnapshotId = null)
    {
        var notice = new ClientEventConfirmationNotice(purpose, ClientEventConfirmationStage.Uploading, eventId);
        if (string.IsNullOrWhiteSpace(eventId)) { Report(notice with { Stage = ClientEventConfirmationStage.Empty }); return Task.CompletedTask; }
        Action retry = () => _ = SingleAsync(purpose, operation, prefix, eventId, applicationResult, sourceEventId, baseSnapshotId);
        string uploading = presentation.Format(notice);
        var prepared = preparation.Current(operation, uploading);
        if (ReportPreparationFailure(prepared, notice, retry)) return Task.CompletedTask;
        presentation.Report(notice, uploading);
        var command = confirmation.Single(prepared.Attempt!, prefix, eventId, sourceEventId, baseSnapshotId,
            prepared.Build!.Package!, prepared.Build.Payload!, applicationResult);
        return ClientSessionWork.Run(() => confirmation.ConfirmAsync(prepared.Attempt!, command,
            failure => Fail(operation, notice, Problem(failure, failure.Network?.Response?.ServerValidationResult), retry), response =>
            {
                if (purpose == ClientEventConfirmationPurpose.Gift) inbox.ForgetPendingConfirmation(eventId);
                if (purpose is ClientEventConfirmationPurpose.Gift or ClientEventConfirmationPurpose.SupportArrival) effects.MarkHandled(eventId);
                Report(notice with { Stage = ClientEventConfirmationStage.Accepted, SnapshotId = response.AppliedSnapshotId, Pending = inbox.PendingConfirmationCount });
                Complete();
            }));
    }

    public Task AutomaticAsync(IReadOnlyList<ModConfirmEventApplicationEntryDto> applications) =>
        AutomaticOwnedAsync(ClientEventConfirmation.CopyEntries(applications));

    private Task AutomaticOwnedAsync(IReadOnlyList<ModConfirmEventApplicationEntryDto> applications)
    {
        if (applications.Count == 0) return Task.CompletedTask;
        var notice = new ClientEventConfirmationNotice(ClientEventConfirmationPurpose.AutomaticEvents,
            ClientEventConfirmationStage.Uploading, Count: applications.Count);
        Action retry = () => _ = AutomaticOwnedAsync(applications);
        string uploading = presentation.Format(notice);
        var prepared = preparation.Current(LocalMutationKind.AutomaticEvents, uploading);
        if (ReportPreparationFailure(prepared, notice, retry)) return Task.CompletedTask;
        presentation.Report(notice, uploading);
        var command = confirmation.Batch(prepared.Attempt!, "auto-event-confirm", prepared.Build!.Package!, prepared.Build.Payload!, applications);
        return ClientSessionWork.Run(() => confirmation.ConfirmBatchAsync(prepared.Attempt!, command,
            failure => Fail(LocalMutationKind.AutomaticEvents, notice, Problem(failure), retry),
            response => response.Applications.Where(a => a.Result?.Accepted == true).Select(a => a.AppliedSnapshotId)
                .LastOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? response.AppliedSnapshotId,
            (response, snapshotId) =>
            {
                var accepted = response.Applications.Where(a => a.Result?.Accepted == true).ToArray();
                foreach (var entry in accepted)
                    if (!string.IsNullOrWhiteSpace(entry.EventId))
                    {
                        inbox.ForgetPendingConfirmation(entry.EventId!);
                        effects.MarkHandled(entry.EventId!);
                    }
                Report(notice with { Stage = ClientEventConfirmationStage.Accepted, Count = accepted.Length,
                    Rejected = response.Applications.Count - accepted.Length, SnapshotId = snapshotId });
                Complete();
            }));
    }

    private bool ReportPreparationFailure(ClientEventPreparationResult prepared, ClientEventConfirmationNotice notice, Action retry)
    {
        if (prepared.Failure is not { } failure) return false;
        if (failure.Kind == ClientEventPreparationFailureKind.Obsolete) return true;
        string status = Report(notice with { Stage = ClientEventConfirmationStage.PreparationFailed, PreparationFailure = failure });
        if (failure.Kind is not (ClientEventPreparationFailureKind.Blocked or ClientEventPreparationFailureKind.Busy))
            recovery.Show(prepared.Operation, status, retry);
        return true;
    }

    private void Fail(LocalMutationKind operation, ClientEventConfirmationNotice notice, ClientEventConfirmationProblem problem, Action retry)
    {
        string status = Report(notice with { Stage = ClientEventConfirmationStage.Failed, Problem = problem });
        recovery.Show(operation, status, retry, problem.CanRetry);
    }

    private static ClientEventConfirmationProblem Problem<T>(ClientSnapshotTransactionFailure<T> failure, string? validation = null) => new(
        failure.Kind, failure.Rejection?.ErrorCode.ToString() ?? failure.Network?.ErrorCode ?? string.Empty,
        failure.Exception?.Message ?? failure.Rejection?.Message ?? failure.Network?.Message ?? string.Empty,
        validation ?? string.Empty, failure.Exception, failure.CanRetry);

    private string Report(ClientEventConfirmationNotice notice)
    { string message = presentation.Format(notice); presentation.Report(notice, message); return message; }

    private void Complete()
    {
        mutations.Reset();
        recovery.Close();
        Completed?.Invoke();
    }
}
