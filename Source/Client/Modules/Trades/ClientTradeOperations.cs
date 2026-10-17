using System;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Packages;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Trades;

// Creation and both fulfillment modes share one prepare/submit/retry lifecycle.
// Engine adapters own inventory algorithms; HTTP and late callbacks use captured ownership.
internal sealed class ClientTradeOperations
{
    private readonly ClientSnapshotMutationAdmission admission;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientSnapshotTransaction transactions;
    private readonly ClientSnapshotFailureRecovery recovery;
    private readonly ClientTradeState state;
    private readonly IClientTradeOperationsTransport transport;
    private readonly IClientTradeOperationsPresentation presentation;
    internal ClientTradeOperations(ClientSnapshotMutationAdmission admission, ClientSynchronizationCoordinator synchronization,
        ClientLocalMutationCoordinator mutations, ClientSnapshotTransaction transactions, ClientSnapshotFailureRecovery recovery,
        ClientTradeState state, IClientTradeOperationsTransport transport, IClientTradeOperationsPresentation presentation)
    { this.admission = admission; this.synchronization = synchronization; this.mutations = mutations; this.transactions = transactions;
        this.recovery = recovery; this.state = state; this.transport = transport; this.presentation = presentation; }
    internal event Action<ClientTradeOperation>? Completed;

    internal Task StartAsync(IClientTradeSource source, ModTradeOrderSummaryDto order)
    {
        using var publication = ClientSessionWork.EnterPublication();
        state.Capture();
        var attempt = Admit(admission.Begin());
        if (attempt is null) return Task.CompletedTask;
        bool handedOff = false;
        try
        {
            if (!state.Enabled) { Reject(Text("Disabled")); return Task.CompletedTask; }
            var captured = ClientProtocolJson.Deserialize<ModTradeOrderSummaryDto>(ClientProtocolJson.Serialize(order), static type => type);
            ClientPackageCopies.CopyPreviewSidecars(order.OfferedThings, captured.OfferedThings);
            ClientPackageCopies.CopyPreviewSidecars(order.RequestedThings, captured.RequestedThings);
            if (source.Operation == ClientTradeOperation.Create)
            {
                if (captured.OfferedThings.Count == 0 && captured.RequestedThings.Count == 0)
                { Reject(Text("StatusDraftMissingThings")); return Task.CompletedTask; }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(captured.EventId)) { Reject(Text("StatusMissingOrderEventId")); return Task.CompletedTask; }
                if (!(ClientTradeRules.IsOpen(captured.Status) || string.IsNullOrWhiteSpace(captured.Status))
                    || (source.Operation == ClientTradeOperation.SelfDelivery ? !captured.AllowSelfPickup : !captured.AllowServerDropPod))
                { Reject(Text("StatusOrderUnavailable")); return Task.CompletedTask; }
                if (source.Operation == ClientTradeOperation.SelfDelivery && (!captured.ViewerHasAccepted || string.IsNullOrWhiteSpace(captured.ViewerAcceptedMemoEventId)))
                { Reject(Text("StatusMissingAcceptMemo")); return Task.CompletedTask; }
            }
            string? rejection = source.Validate(attempt.Context, captured);
            if (rejection is not null) { Reject(rejection); return Task.CompletedTask; }
            string key = source.Operation switch
            {
                ClientTradeOperation.Create => FormattableString.Invariant($"manual-trade:{attempt.Context.UserId}:{DateTime.UtcNow.Ticks}"),
                ClientTradeOperation.SelfDelivery => FormattableString.Invariant($"trade-fulfill:{attempt.Context.UserId}:{captured.EventId}:{captured.ViewerAcceptedMemoEventId}:{source.Key}:{DateTime.UtcNow.Ticks}"),
                _ => FormattableString.Invariant($"trade-drop-pod:{attempt.Context.UserId}:{captured.EventId}:{attempt.Context.CurrentSnapshotId}:{DateTime.UtcNow.Ticks}")
            };
            var job = new Job(source, new(source.Operation, key, captured, Array.Empty<ModThingReferenceDto>()));
            handedOff = true;
            return ApplyAndSubmitAsync(attempt, job);
        }
        catch (Exception ex) { Reject(ExceptionText(source.Operation, ex)); presentation.Diagnose(ex); return Task.CompletedTask; }
        finally { if (!handedOff) attempt.Admission.Dispose(); }
    }

    private async Task ApplyAndSubmitAsync(ClientSnapshotTransaction.Attempt attempt, Job job)
    {
        Task submitted = Task.CompletedTask;
        try
        {
            if (job.Command.Operation == ClientTradeOperation.SelfDelivery)
            {
                mutations.Begin(LocalMutationKind.Trade, Text("StatusCaravanFulfillPreparing"));
                state.Message = Text("StatusSubmittingCaravanFulfill", ("EVENTID", job.Command.Order.EventId));
                await ClientSessionWork.Run(async () =>
                {
                    await transport.HydrateAsync(attempt.Context, job.Command.Order.OfferedThings, attempt.Owner.Token).ConfigureAwait(false);
                    await ClientGamePublication.EnqueueAsync(attempt.Game.Enqueue, attempt.Owner.Token,
                        action => transactions.TryApply(attempt, action), () => submitted = Apply(attempt, job)).ConfigureAwait(false);
                });
            }
            else submitted = Apply(attempt, job); // Capture/remove on the initiating game thread.
            await submitted;
        }
        catch (Exception ex)
        {
            transactions.TryApply(attempt, () => FailedLocal(attempt, job, ExceptionText(job.Command.Operation, ex), ex));
        }
        finally { attempt.Admission.Dispose(); }
    }

    private Task Apply(ClientSnapshotTransaction.Attempt attempt, Job job)
    {
        if (!transactions.TryApply(attempt, static () => { })) return Task.CompletedTask;
        // Only self-delivery awaited hydration since initial validation.
        string? rejection = job.Command.Operation == ClientTradeOperation.SelfDelivery ? job.Source.Validate(attempt.Context, job.Command.Order) : null;
        if (rejection is not null) { mutations.Reset(); Reject(rejection); return Task.CompletedTask; }
        mutations.Begin(LocalMutationKind.Trade, Text(job.Command.Operation == ClientTradeOperation.Create ? "StatusOrderReserving" : "StatusDropPodReserving"));
        var applied = job.Source.Apply(attempt.Context, job.Command.Order, () => job.MutationStarted = true);
        if (!transactions.TryApply(attempt, static () => { })) return Task.CompletedTask;
        if (!applied.Success)
        {
            FailedLocal(attempt, job, job.Command.Operation == ClientTradeOperation.Create
                ? Text("StatusOrderCreatedReservationFailed", ("MESSAGE", applied.Message))
                : Text("StatusFulfillLocalApplyFailed", ("MESSAGE", applied.Message)));
            return Task.CompletedTask;
        }
        job.Message = applied.Message;
        job.Command = job.Command with { Delivered = ClientPackageCopies.CopyThings(applied.Delivered) };
        job.LocalPrepared = true;
        return PrepareAndSubmit(attempt, job);
    }

    private Task PrepareAndSubmit(ClientSnapshotTransaction.Attempt attempt, Job job)
    {
        mutations.Begin(LocalMutationKind.Trade, Text(IsCreate(job) ? "StatusOrderPackagingSnapshot" : "StatusFulfillPackagingSnapshot"));
        if (job.Package is null || job.Payload is null)
        {
            bool built = ClientSnapshotPreparation.TryBuild(attempt.Game, attempt.Context.UserId, attempt.Context.ColonyId, out var build, out var failure);
            if (!transactions.TryApply(attempt, static () => { })) return Task.CompletedTask;
            if (!built)
            {
                ShowFailure(attempt, job, Text(IsCreate(job) ? "StatusOrderSnapshotBuildFailed" : "StatusFulfillSnapshotBuildFailed", ("REASON", failure?.Message)), true);
                return Task.CompletedTask;
            }
            job.Package = build.Package!; job.Payload = build.Payload!;
        }
        if (!synchronization.TryAcquireSnapshot(out var upload, attempt.Admission))
        { ShowFailure(attempt, job, presentation.Text("ClashOfRim.SnapshotUpload.Busy"), true); return Task.CompletedTask; }
        attempt = attempt with { Admission = upload };
        mutations.Begin(LocalMutationKind.Trade, Text(IsCreate(job) ? "StatusOrderSubmittingTransaction" : "StatusFulfillSubmittingTransaction"));
        state.Message = IsCreate(job) ? Text("StatusSubmittingOrder", ("FEE", job.Command.Order.FeeSilver))
            : Text(job.Command.Operation == ClientTradeOperation.SelfDelivery ? "StatusSubmittingCaravanFulfill" : "StatusSubmittingDropPodFulfill",
                ("EVENTID", job.Command.Order.EventId), ("MESSAGE", job.Message));
        return ClientSessionWork.Run(() => transactions.ExecuteAsync(attempt,
            () => transport.SubmitAsync(attempt.Context, job.Command, job.Package!, job.Payload!, attempt.Owner.Token), r => r.Result,
            failure =>
            {
                if (failure.Exception is not null) presentation.Diagnose(failure.Exception);
                string message = failure.Exception is { } ex ? ExceptionText(job.Command.Operation, ex) : FailureText(job, failure);
                ShowFailure(attempt, job, message, !job.Publishing && failure.CanRetry);
            }, response =>
            {
                if (string.IsNullOrWhiteSpace(response.EventId) || string.IsNullOrWhiteSpace(response.AppliedSnapshotId))
                { ShowFailure(attempt, job, Text("StatusConfirmationIncomplete"), false); return; }
                job.Publishing = true;
                transactions.Publish(attempt, response.AppliedSnapshotId, response.NextLineageToken);
                transactions.TryApply(attempt, () =>
                {
                    if (!IsCreate(job)) state.Fulfilled(job.Command.Order.EventId);
                    string message = IsCreate(job) ? Text("StatusOrderCreatedReserved", ("EVENTID", response.EventId), ("MESSAGE", job.Message))
                        : Text("StatusDropPodFulfillCreated", ("EVENTID", response.EventId), ("ACCEPTOR", response.AcceptorDeliveryId), ("OWNER", response.OwnerDeliveryId));
                    state.Message = message; presentation.Message(message, false);
                    transactions.TryApply(attempt, () => { mutations.Reset(); recovery.Close(); }, requireBaseSnapshot: false);
                }, requireBaseSnapshot: false);
                if (transactions.TryApply(attempt, static () => { }, requireBaseSnapshot: false))
                { attempt.Admission.Dispose(); Completed?.Invoke(job.Command.Operation); }
            }));
    }

    private Task Retry(ClientSnapshotTransaction.Attempt original, Job job)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var retry = admission.Retry(original, LocalMutationKind.Trade);
        if (retry.Failure == ClientSnapshotAdmissionFailure.Obsolete) return Task.CompletedTask;
        if (retry.Failure == ClientSnapshotAdmissionFailure.BaseSnapshotChanged)
        { ShowFailure(original, job, presentation.Text("ClashOfRim.SnapshotConfirmation.BaseSnapshotChanged"), false); return Task.CompletedTask; }
        var attempt = Admit(retry);
        if (attempt is null) return Task.CompletedTask;
        try { return PrepareAndSubmit(attempt, job); }
        catch (Exception ex) { FailedLocal(attempt, job, ExceptionText(job.Command.Operation, ex), ex); return Task.CompletedTask; }
        finally { attempt.Admission.Dispose(); }
    }
    private void FailedLocal(ClientSnapshotTransaction.Attempt attempt, Job job, string message, Exception? exception = null)
    {
        if (exception is not null) presentation.Diagnose(exception);
        Reject(message);
        if (job.MutationStarted) ShowFailure(attempt, job, message, job.LocalPrepared);
        else mutations.Reset();
    }
    private void ShowFailure(ClientSnapshotTransaction.Attempt attempt, Job job, string message, bool retry)
    {
        state.Message = message;
        recovery.Show(LocalMutationKind.Trade, message, () => _ = Retry(attempt, job), canRetry: retry);
    }
    private ClientSnapshotTransaction.Attempt? Admit(ClientSnapshotAdmissionResult result)
    {
        if (result.Attempt is null) Reject(result.Failure == ClientSnapshotAdmissionFailure.Admission
            ? presentation.Admission(result.Admission, mutations.Current) : presentation.Text("ClashOfRim.SnapshotUpload.Busy"));
        return result.Attempt;
    }
    private string FailureText(Job job, ClientSnapshotTransactionFailure<ClientTradeReceipt> failure)
    {
        if (IsCreate(job)) return failure.Rejection is null ? Text("StatusOrderSubmitException", ("TYPE", failure.Network?.ErrorCode), ("MESSAGE", failure.Network?.Message))
            : Text("StatusCreateRejected", ("CODE", failure.Rejection.ErrorCode), ("MESSAGE", failure.Rejection.Message));
        var missing = failure.Network?.Response?.Missing;
        string suffix = missing is { Count: > 0 } ? Text("MissingSuffix", ("MISSING", string.Join("；", missing.Take(5)))) : "";
        return Text(failure.Rejection is null ? "StatusFulfillFailed" : "StatusFulfillRejected",
            ("CODE", (object?)failure.Rejection?.ErrorCode ?? failure.Network?.ErrorCode), ("MESSAGE", failure.Rejection?.Message ?? failure.Network?.Message), ("MISSING", suffix));
    }
    private string ExceptionText(ClientTradeOperation operation, Exception exception) => Text(operation == ClientTradeOperation.Create ? "StatusOrderSubmitException" : "StatusFulfillException",
        ("TYPE", exception.GetType().Name), ("MESSAGE", exception.Message));
    private string Text(string key, params (string Name, object? Value)[] args) => presentation.Text("ClashOfRim.Trade." + key, args);
    private void Reject(string message) { state.Message = message; presentation.Message(message, true); }
    private static bool IsCreate(Job job) => job.Command.Operation == ClientTradeOperation.Create;
    private sealed class Job
    {
        internal Job(IClientTradeSource source, ClientTradeCommand command) { Source = source; Command = command; }
        internal readonly IClientTradeSource Source;
        internal ClientTradeCommand Command;
        internal bool MutationStarted, LocalPrepared, Publishing;
        internal string Message = "";
        internal ModSnapshotPackageMetadataDto? Package;
        internal byte[]? Payload;
    }
}
