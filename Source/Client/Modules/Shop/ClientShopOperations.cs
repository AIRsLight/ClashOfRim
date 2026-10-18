using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Packages;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Shop;

// Buy and sell share one local exchange and prepared snapshot submission. Only
// snapshot preparation/transmission may retry; inventory and silver never do.
internal sealed class ClientShopOperations
{
    private readonly ClientShopState state;
    private readonly ClientSnapshotMutationAdmission admission;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientSnapshotTransaction transactions;
    private readonly ClientSnapshotFailureRecovery recovery;
    private readonly IClientShopRuntime runtime;
    private readonly IClientShopPurchaseTransport transport;
    private readonly IClientShopPresentation presentation;
    private readonly IClientShopOperationsPresentation notices;
    internal ClientShopOperations(ClientShopState state, ClientSnapshotMutationAdmission admission, ClientSynchronizationCoordinator synchronization,
        ClientLocalMutationCoordinator mutations, ClientSnapshotTransaction transactions, ClientSnapshotFailureRecovery recovery,
        IClientShopRuntime runtime, IClientShopPurchaseTransport transport, IClientShopPresentation presentation, IClientShopOperationsPresentation notices)
    { this.state = state; this.admission = admission; this.synchronization = synchronization; this.mutations = mutations;
        this.transactions = transactions; this.recovery = recovery; this.runtime = runtime; this.transport = transport;
        this.presentation = presentation; this.notices = notices; }
    internal event Action<Func<Action, bool>>? Completed;

    internal Action BindPurchase(ModServerShopListingDto listing, int count)
    {
        var scope = state.Capture();
        var copy = ClientShopState.Copy(new[] { listing })[0];
        return () => state.TryApply(scope, () => _ = StartAsync(copy, count));
    }

    internal Task StartAsync(ModServerShopListingDto listing, int count)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (state.Busy) return Task.CompletedTask;
        var admitted = admission.Begin();
        if (admitted.Attempt is not { } attempt) { Reject(AdmissionText(admitted)); return Task.CompletedTask; }
        var request = state.Begin(attempt.Admission)!;
        bool handedOff = false;
        try
        {
            var copy = ClientShopState.Copy(new[] { listing })[0];
            if (copy.Item is null || string.IsNullOrWhiteSpace(copy.ListingId)) { Reject(Text("StatusMissingListing")); return Task.CompletedTask; }
            count = Math.Max(1, count);
            if (count > copy.StockCount) { Reject(Text("StatusStockNotEnough")); return Task.CompletedTask; }
            int price = ClientShopRules.TotalPrice(copy, count);
            if (price >= int.MaxValue) { Reject(Text("StatusPriceOverflow")); return Task.CompletedTask; }
            long quantity = (long)Math.Max(1, copy.Item.StackCount) * count;
            if (quantity > int.MaxValue) { Reject(Text("StatusItemCountOverflow")); return Task.CompletedTask; }
            string prefix = ClientShopRules.IsBuyOrder(copy) ? "shop-sell" : "shop-buy";
            string key = FormattableString.Invariant($"{prefix}:{attempt.Context.UserId}:{attempt.Context.ColonyId}:{attempt.Context.CurrentSnapshotId}:{copy.ListingId}:{Guid.NewGuid():N}");
            var job = new Job(runtime.Capture(), new(key, copy, count, price, (int)quantity, Array.Empty<ModThingReferenceDto>()));
            string? rejection = job.Source.Validate(job.Command);
            if (rejection is not null) { Reject(rejection); return Task.CompletedTask; }
            handedOff = true;
            return ApplyAndSubmitAsync(attempt, request, job);
        }
        catch (Exception ex) { Reject(ExceptionText(ex)); presentation.Diagnose(ex); return Task.CompletedTask; }
        finally { if (!handedOff) state.End(request); }
    }

    private async Task ApplyAndSubmitAsync(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request, Job job)
    {
        Task submitted = Task.CompletedTask;
        try
        {
            if (!ClientShopRules.IsBuyOrder(job.Command.Listing) && NeedsPackages(job.Command.Listing.Item!))
            {
                state.Message = Text("StatusRefreshing");
                await ClientSessionWork.Run(async () =>
                {
                    if (!TryApply(attempt, request, static () => { })) return;
                    await transport.HydrateAsync(attempt.Context, new[] { job.Command.Listing }, attempt.Owner.Token).ConfigureAwait(false);
                    await ClientGamePublication.EnqueueAsync(attempt.Game.Enqueue, attempt.Owner.Token,
                        action => TryApply(attempt, request, action), () => submitted = Apply(attempt, request, job, revalidate: true)).ConfigureAwait(false);
                });
            }
            else submitted = Apply(attempt, request, job, revalidate: false);
            await submitted;
        }
        catch (Exception ex) { TryApply(attempt, request, () => FailedLocal(attempt, request, job, ExceptionText(ex), ex)); }
        finally { attempt.Admission.Dispose(); state.End(request); }
    }

    private Task Apply(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request, Job job, bool revalidate)
    {
        if (!TryApply(attempt, request, static () => { })) return Task.CompletedTask;
        // Map and silver may have changed while packages were downloading.
        string? rejection = revalidate ? job.Source.Validate(job.Command) : null;
        if (rejection is not null) { Reject(rejection); return Task.CompletedTask; }
        state.Message = Text(ClientShopRules.IsBuyOrder(job.Command.Listing) ? "StatusApplyingSale" : "StatusApplyingPurchase");
        mutations.Begin(LocalMutationKind.Shop, state.Message); job.Registered = true;
        var result = job.Source.Apply(attempt.Context, job.Command, () => job.MutationStarted = true);
        if (!TryApply(attempt, request, static () => { })) return Task.CompletedTask;
        if (!result.Success) { FailedLocal(attempt, request, job, result.Message); return Task.CompletedTask; }
        job.Command = job.Command with { Delivered = ClientPackageCopies.CopyThings(result.Delivered) };
        job.Prepared = true;
        return PrepareAndSubmit(attempt, request, job);
    }

    private Task PrepareAndSubmit(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request, Job job)
    {
        if (job.Package is null || job.Payload is null)
        {
            bool built = ClientSnapshotPreparation.TryBuild(attempt.Game, attempt.Context.UserId, attempt.Context.ColonyId, out var build, out var failure);
            if (!TryApply(attempt, request, static () => { })) return Task.CompletedTask;
            if (!built) { Fail(attempt, request.Scope, job, notices.SnapshotBuildFailed(failure?.Message), true); return Task.CompletedTask; }
            job.Package = build.Package!; job.Payload = build.Payload!;
        }
        if (!synchronization.TryAcquireSnapshot(out var upload, attempt.Admission))
        { Fail(attempt, request.Scope, job, notices.Busy, true); return Task.CompletedTask; }
        attempt = attempt with { Admission = upload };
        state.Track(request, upload);
        state.Message = Text("StatusUploadingPurchase");
        mutations.UpdateStatus(state.Message);
        return ClientSessionWork.Run(() => transactions.ExecuteAsync(attempt,
            () => transport.PurchaseAsync(attempt.Context, job.Command, job.Package!, job.Payload!, attempt.Owner.Token), r => r.Result,
            failure => state.TryApply(request, () =>
            {
                if (failure.Exception is not null) presentation.Diagnose(failure.Exception);
                string message = failure.Exception is { } ex ? ExceptionText(ex)
                    : Text(failure.Rejection is null ? "StatusPurchaseFailed" : "StatusPurchaseRejected",
                        ("CODE", (object?)failure.Rejection?.ErrorCode ?? failure.Network?.ErrorCode), ("MESSAGE", failure.Rejection?.Message ?? failure.Network?.Message));
                Fail(attempt, request.Scope, job, message, !job.Publishing && failure.CanRetry);
            }), response => state.TryApply(request, () =>
            {
                if (response.ListingId != job.Command.Listing.ListingId || string.IsNullOrWhiteSpace(response.AppliedSnapshotId))
                { Fail(attempt, request.Scope, job, Text("StatusConfirmationIncomplete"), false); return; }
                job.Publishing = true;
                transactions.Publish(attempt, response.AppliedSnapshotId, response.NextLineageToken);
                TryApply(attempt, request, () =>
                {
                    state.Message = Text(ClientShopRules.IsBuyOrder(job.Command.Listing) ? "StatusSoldToServer" : "StatusPurchased",
                        ("COUNT", job.Command.PurchaseCount), ("PRICE", job.Command.TotalPrice));
                    notices.Message(state.Message, false);
                    mutations.Reset(); recovery.Close();
                    var completion = transactions.CaptureCompletion(attempt, response.AppliedSnapshotId,
                        () => state.TryApply(request.Scope, static () => { }));
                    state.End(request); // Exact upload lease is released before dependent refresh.
                    completion(() => Completed?.Invoke(completion));
                }, requireBaseSnapshot: false);
            })));
    }

    private Task Retry(ClientSnapshotTransaction.Attempt original, ClientGameRequestTracker.Scope scope, Job job)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!state.TryApply(scope, static () => { })) return Task.CompletedTask;
        if (state.Busy) { Fail(original, scope, job, notices.Busy, true); return Task.CompletedTask; }
        var admitted = admission.Retry(original, LocalMutationKind.Shop);
        if (admitted.Failure == ClientSnapshotAdmissionFailure.Obsolete) return Task.CompletedTask;
        if (admitted.Failure == ClientSnapshotAdmissionFailure.BaseSnapshotChanged)
        { Fail(original, scope, job, notices.BaseSnapshotChanged, false); return Task.CompletedTask; }
        if (admitted.Attempt is not { } attempt)
        { Fail(original, scope, job, AdmissionText(admitted), admitted.Failure == ClientSnapshotAdmissionFailure.Busy); return Task.CompletedTask; }
        var request = state.Begin(attempt.Admission)!;
        return RetryAndFinishAsync(attempt, request, job);
    }
    private async Task RetryAndFinishAsync(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request, Job job)
    {
        try { await PrepareAndSubmit(attempt, request, job); }
        catch (Exception ex) { TryApply(attempt, request, () => FailedLocal(attempt, request, job, ExceptionText(ex), ex)); }
        finally { attempt.Admission.Dispose(); state.End(request); }
    }
    private void FailedLocal(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request, Job job, string message, Exception? exception = null)
    {
        if (exception is not null) presentation.Diagnose(exception);
        Reject(message);
        if (job.MutationStarted) Fail(attempt, request.Scope, job, message, job.Prepared);
        else if (job.Registered) mutations.Reset();
    }
    private void Fail(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Scope scope, Job job, string message, bool retry)
    { state.Message = message; recovery.Show(LocalMutationKind.Shop, message, () => _ = Retry(attempt, scope, job), canRetry: retry); }
    private bool TryApply(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request, Action action, bool requireBaseSnapshot = true)
    {
        bool applied = false;
        transactions.TryApply(attempt, () => applied = state.TryApply(request, action), requireBaseSnapshot);
        return applied;
    }
    private static bool NeedsPackages(ModThingReferenceDto item) => (item.PawnPackage is null && !string.IsNullOrWhiteSpace(item.PawnPackageId))
        || (item.ThingPackage is null && !string.IsNullOrWhiteSpace(item.ThingPackageId));
    private string AdmissionText(ClientSnapshotAdmissionResult result) => result.Failure == ClientSnapshotAdmissionFailure.Busy
        ? notices.Busy : presentation.Admission(result.Admission, mutations.Current);
    private string Text(string key, params (string Name, object? Value)[] args) => presentation.Text(key, args);
    private string ExceptionText(Exception ex) => Text("StatusException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message));
    private void Reject(string message) { state.Message = message; notices.Message(message, true); }
    private sealed class Job
    {
        internal Job(IClientShopSource source, ClientShopCommand command) { Source = source; Command = command; }
        internal readonly IClientShopSource Source;
        internal ClientShopCommand Command;
        internal bool Registered, MutationStarted, Prepared, Publishing;
        internal ModSnapshotPackageMetadataDto? Package;
        internal byte[]? Payload;
    }
}
