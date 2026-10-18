using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.Network.Modules.Economy;

internal sealed class PendingConfirmationReconciler(
    BankLoanRegistry loans, MercenaryContractRegistry mercenaries,
    Func<ClashOfRimServerConfiguration> configuration, SnapshotMutationCoordinator mutations)
{
    internal PendingConfirmationReconciliationResult ReconcileAfterRecovery(
        string userId, string colonyId, DateTimeOffset nowUtc, bool forceCancel)
    {
        using var mutation = mutations.Enter();
        return Reconcile(userId, colonyId, nowUtc, forceCancel);
    }

    internal PendingConfirmationReconciliationResult Reconcile(
        string userId, string colonyId, DateTimeOffset nowUtc, bool forceCancel)
    {
        TimeSpan timeout = configuration().PendingConfirmationTimeout;
        BankPendingConfirmationReconciliationResult bankResult = loans.ReconcilePendingConfirmations(
            userId,
            colonyId,
            nowUtc,
            timeout,
            forceCancel);
        MercenaryPendingConfirmationReconciliationResult mercenaryResult = mercenaries.ReconcilePendingConfirmations(
            userId,
            colonyId,
            nowUtc,
            timeout,
            forceCancel);
        return new PendingConfirmationReconciliationResult(bankResult, mercenaryResult);
    }

    internal ProtocolResponse? ReconcileSnapshot(string userId, string colonyId, DateTimeOffset nowUtc)
    {
        if (!mutations.IsEntered)
            throw new InvalidOperationException("Snapshot reconciliation requires the recovered mutation scope.");
        return Rejection(
            Reconcile(userId, colonyId, nowUtc, forceCancel: false));
    }

    internal static ProtocolResponse? Rejection(PendingConfirmationReconciliationResult result) =>
        !result.Changed ? null : ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerLocalization.Text("PendingConfirmation.ExpiredCancelled"));
}

internal sealed record PendingConfirmationReconciliationResult(
    BankPendingConfirmationReconciliationResult Bank,
    MercenaryPendingConfirmationReconciliationResult Mercenary)
{
    public bool Changed => Bank.Changed || Mercenary.Changed;
}
