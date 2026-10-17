using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Economy;

internal sealed class PendingConfirmationReconciler(
    BankLoanRegistry loans, MercenaryContractRegistry mercenaries,
    Func<ClashOfRimServerConfiguration> configuration, object gate)
{
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
        if (!Monitor.IsEntered(gate))
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
