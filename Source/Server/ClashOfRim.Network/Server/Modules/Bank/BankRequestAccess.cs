using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Modules.Economy;

namespace AIRsLight.ClashOfRim.Network.Modules.Bank;

internal sealed class BankRequestAccess(
    ColonyAccessAuthorization authorization, SnapshotMutationCoordinator mutations,
    IColonySnapshotIndexStore snapshots, BankLoanRegistry loans, PendingConfirmationReconciler pending)
{
    // Read-only admission is repeated after recovery; reconciliation happens only after acceptance.
    internal RecoveredSnapshotMutation EnterBankRequestMutation(string userId, string colonyId, string snapshotId, string? token, long ticks, int wealth,
        out BankRequestContext? context, out ProtocolResponse? rejection, Func<ProtocolResponse?>? validate = null,
        [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
    {
        BankRequestContext? current = null;
        var mutation = mutations.Enter(() =>
        {
            current = ReadBankRequestContext(userId, colonyId, snapshotId, token, ticks, wealth,
                DateTimeOffset.UtcNow, out var denied);
            return denied ?? validate?.Invoke();
        }, operation);
        try
        {
            rejection = mutation.Rejection;
            context = null;
            if (rejection is null)
            {
                pending.Reconcile(userId, colonyId, DateTimeOffset.UtcNow, forceCancel: false);
                context = current! with { ActiveLoan = loans.GetActive(userId, colonyId) };
            }
            return mutation;
        }
        catch { mutation.Dispose(); throw; }
    }

    // Call only for new snapshot work. A committed receipt must replay before pending cleanup.
    internal BankRequestContext ReconcileBankSnapshotContext(BankRequestContext context, DateTimeOffset nowUtc, out ProtocolResponse? rejection)
    {
        rejection = pending.ReconcileSnapshot(context.UserId, context.ColonyId, nowUtc);
        return context with { ActiveLoan = loans.GetActive(context.UserId, context.ColonyId) };
    }

    internal BankRequestContext? ReadBankRequestContext(
        string userId,
        string colonyId,
        string currentSnapshotId,
        string? authToken,
        long requestedCurrentGameTicks,
        int colonyWealth,
        DateTimeOffset nowUtc,
        out ProtocolResponse? rejection,
        string? committedSnapshotId = null)
    {
        rejection = null;
        if (string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(colonyId)
            || string.IsNullOrWhiteSpace(currentSnapshotId))
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Bank.RequestMissingIdentity"));
            return null;
        }

        if (authorization.Authorize(
                authToken,
                userId,
                colonyId,
                authorizationEventId: null,
                authorizationScope: null,
                nowUtc) is { } authFailure)
        {
            rejection = authFailure;
            return null;
        }

        LatestSnapshotRecord? latest = snapshots.GetLatest(userId, colonyId);
        if (latest is null)
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, ServerLocalization.Text("Bank.MissingSnapshot"));
            return null;
        }

        if (!string.Equals(latest.Identity.SnapshotId, currentSnapshotId, StringComparison.Ordinal)
            && !string.Equals(latest.Identity.SnapshotId, committedSnapshotId, StringComparison.Ordinal))
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, ServerLocalization.Text("Bank.SnapshotNotLatest"));
            return null;
        }

        long currentGameTicks = requestedCurrentGameTicks > 0
            ? requestedCurrentGameTicks
            : latest.Envelope.GameTicks.GetValueOrDefault();
        if (currentGameTicks <= 0)
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Bank.MissingGameTime"));
            return null;
        }

        BankLoanRecord? activeLoan = loans.GetActive(userId, colonyId);
        return new BankRequestContext(
            userId,
            colonyId,
            currentSnapshotId,
            currentGameTicks,
            Math.Max(0, colonyWealth),
            activeLoan);
    }
}

internal sealed record BankRequestContext(
    string UserId,
    string ColonyId,
    string CurrentSnapshotId,
    long CurrentGameTicks,
    int ColonyWealth,
    BankLoanRecord? ActiveLoan);
