using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    // Validate without reconciling pending operations. Recovery may invalidate identity, snapshot or game time.
    private static RecoveredSnapshotMutation EnterBankRequestMutation(ClashOfRimNetworkState state,
        string userId, string colonyId, string snapshotId, string? token, long ticks, int wealth,
        out BankRequestContext? context, out ProtocolResponse? rejection, Func<ProtocolResponse?>? validate = null,
        [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
    {
        BankRequestContext? current = null;
        var mutation = EnterRecoveredSnapshotMutation(state, () =>
        {
            current = ReadBankRequestContext(state, userId, colonyId, snapshotId, token, ticks, wealth,
                DateTimeOffset.UtcNow, out var denied);
            return denied ?? validate?.Invoke();
        }, operation);
        try
        {
            rejection = mutation.Rejection;
            context = null;
            if (rejection is null)
            {
                ReconcilePendingConfirmationsLocked(state, userId, colonyId, DateTimeOffset.UtcNow, forceCancel: false);
                context = current! with { ActiveLoan = state.BankLoans.GetActive(userId, colonyId) };
            }
            return mutation;
        }
        catch { mutation.Dispose(); throw; }
    }

    // Only new snapshot work reconciles pending operations; a committed receipt replays without cleanup.
    private static BankRequestContext ReconcileBankSnapshotContext(ClashOfRimNetworkState state,
        BankRequestContext context, DateTimeOffset nowUtc, out ProtocolResponse? rejection)
    {
        rejection = ReconcileSnapshotConfirmationsLocked(state, context.UserId, context.ColonyId, nowUtc);
        return context with { ActiveLoan = state.BankLoans.GetActive(context.UserId, context.ColonyId) };
    }

    private static ProtocolResponse? ReconcileSnapshotConfirmationsLocked(ClashOfRimNetworkState state,
        string userId, string colonyId, DateTimeOffset nowUtc)
    {
        if (!Monitor.IsEntered(state.RaidSettlementSnapshotMutationGate))
            throw new InvalidOperationException("Snapshot reconciliation requires the recovered mutation scope.");
        return PendingConfirmationRejection(
            ReconcilePendingConfirmationsLocked(state, userId, colonyId, nowUtc, forceCancel: false));
    }

    private static ProtocolResponse? ValidateMercenaryIncidentShape(ReportMercenaryIncidentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || string.IsNullOrWhiteSpace(request.ContractId))
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.IncidentMissingFields"));
        return request.IncidentKind is "Completed" or "Death" or "HostileBehavior" or "HarmfulSurgery" or "OvertimeService" or "ShuttleDestroyed"
            ? null : ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.InvalidIncidentKind"));
    }

    private static BankRequestContext? ReadBankRequestContext(
        ClashOfRimNetworkState state,
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
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Bank.RequestMissingIdentity"));
            return null;
        }

        if (!IsAuthorizedForColony(
                state,
                authToken,
                userId,
                colonyId,
                authorizationEventId: null,
                authorizationScope: null,
                nowUtc,
                out string authFailure))
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, authFailure);
            return null;
        }

        LatestSnapshotRecord? latest = state.SnapshotStore.GetLatest(userId, colonyId);
        if (latest is null)
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("Bank.MissingSnapshot"));
            return null;
        }

        if (!string.Equals(latest.Identity.SnapshotId, currentSnapshotId, StringComparison.Ordinal)
            && !string.Equals(latest.Identity.SnapshotId, committedSnapshotId, StringComparison.Ordinal))
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("Bank.SnapshotNotLatest"));
            return null;
        }

        long currentGameTicks = requestedCurrentGameTicks > 0
            ? requestedCurrentGameTicks
            : latest.Envelope.GameTicks.GetValueOrDefault();
        if (currentGameTicks <= 0)
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Bank.MissingGameTime"));
            return null;
        }

        BankLoanRecord? activeLoan = state.BankLoans.GetActive(userId, colonyId);
        return new BankRequestContext(
            userId,
            colonyId,
            currentSnapshotId,
            currentGameTicks,
            Math.Max(0, colonyWealth),
            activeLoan);
    }

}
