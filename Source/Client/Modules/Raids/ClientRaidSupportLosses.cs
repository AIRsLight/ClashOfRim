using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Support;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Raids;

internal sealed record ClientRaidSupportLoss(string EventId, string GlobalKey, string PawnName);
internal interface IClientRaidSupportLossSource
{
    IReadOnlyList<ClientRaidSupportLoss> Capture();
    void InProgress(ClientRaidSupportLoss loss, bool value);
    bool RemoveAndComplete(ClientRaidSupportLoss loss);
}
internal enum ClientRaidSupportLossFailureKind { Transport, Rejected, Incomplete, LocalRemoval, Exception }
internal sealed record ClientRaidSupportLossFailure(ClientRaidSupportLossFailureKind Kind, string? PawnName = null,
    object? Code = null, string? Message = null, Exception? Exception = null, bool CanRetry = false);
internal sealed record ClientRaidSupportLossResult(bool Success, ClientRaidSupportLossFailure? Failure = null);

// Report the captured batch before deleting any local pawn. The caller owns the
// admission across this prerequisite and the following settlement snapshot.
internal sealed class ClientRaidSupportLosses
{
    private readonly ClientSnapshotTransaction transactions;
    private readonly IClientSupportReturnTransport transport;
    internal ClientRaidSupportLosses(ClientSnapshotTransaction transactions, IClientSupportReturnTransport transport)
    { this.transactions = transactions; this.transport = transport; }

    internal Batch? Capture(ClientSnapshotTransaction.Attempt attempt, IClientRaidSupportLossSource source)
    {
        Batch? batch = null;
        transactions.TryApply(attempt, () =>
        {
            var losses = source.Capture().ToArray();
            transactions.TryApply(attempt, () => batch = new(attempt, source, losses));
        });
        return batch;
    }

    // Starts on the game thread. Only transport runs on the worker; source
    // effects return through the same captured game and transaction ownership.
    internal async Task<ClientRaidSupportLossResult> ResolveAsync(Batch batch)
    {
        var attempt = batch.Attempt;
        var result = new ClientRaidSupportLossResult(false);
        bool completed = false;
        bool removing = false;
        try
        {
            if (!transactions.TryApply(attempt, () =>
            {
                foreach (var loss in batch.Losses) batch.Source.InProgress(loss, true);
            })) return result;
            await ClientSessionWork.Run(async () =>
            {
                foreach (var loss in batch.Losses)
                {
                    if (!transactions.TryApply(attempt, static () => { })) return;
                    var command = ClientSupportReturnCommand.Create(attempt.Context,
                        new(loss.EventId, loss.GlobalKey, loss.PawnName, "RaidSettlementLost", true, null));
                    var reply = await transport.FinishAsync(attempt.Context, command, attempt.Owner.Token).ConfigureAwait(false);
                    if (!transactions.TryApply(attempt, static () => { })) return;
                    if (!reply.Success || reply.Response is null)
                    {
                        result = new(false, new(ClientRaidSupportLossFailureKind.Transport, loss.PawnName, reply.ErrorCode, reply.Message,
                            CanRetry: ClientNetworkFailurePolicy.CanRetry(reply.ErrorCode, reply.Message))); return;
                    }
                    if (reply.Response.Result is { Accepted: false } rejection)
                    { result = new(false, new(ClientRaidSupportLossFailureKind.Rejected, loss.PawnName, rejection.ErrorCode, rejection.Message)); return; }
                    if (reply.Response.Result?.Accepted != true
                        || string.IsNullOrWhiteSpace(reply.Response.NotificationEventId) && string.IsNullOrWhiteSpace(reply.Response.ReturnEventId))
                    { result = new(false, new(ClientRaidSupportLossFailureKind.Incomplete, loss.PawnName)); return; }
                }
                await Publish(batch, () =>
                {
                    removing = true;
                    foreach (var loss in batch.Losses)
                        if (!batch.Source.RemoveAndComplete(loss))
                        { result = new(false, new(ClientRaidSupportLossFailureKind.LocalRemoval, loss.PawnName)); return; }
                    completed = true; result = new(true);
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        { result = new(false, new(ClientRaidSupportLossFailureKind.Exception, Exception: ex, CanRetry: !removing && ClientNetworkFailurePolicy.CanRetry(ex))); }
        finally
        {
            if (!completed)
                await Publish(batch, () =>
                {
                    foreach (var loss in batch.Losses) batch.Source.InProgress(loss, false);
                }).ConfigureAwait(false);
        }
        return result;
    }
    private Task Publish(Batch batch, Action action) => ClientGamePublication.EnqueueAsync(batch.Attempt.Game.Enqueue,
        batch.Attempt.Owner.Token, run => transactions.TryApply(batch.Attempt, run), action);

    internal sealed class Batch
    {
        internal Batch(ClientSnapshotTransaction.Attempt attempt, IClientRaidSupportLossSource source, IReadOnlyList<ClientRaidSupportLoss> losses)
        { Attempt = attempt; Source = source; Losses = losses; }
        internal readonly ClientSnapshotTransaction.Attempt Attempt;
        internal readonly IClientRaidSupportLossSource Source;
        internal readonly IReadOnlyList<ClientRaidSupportLoss> Losses;
        internal int Count => Losses.Count;
        internal Batch WithAdmission(ClientSynchronizationCoordinator.Lease admission) => new(Attempt with { Admission = admission }, Source, Losses);
    }
}
