using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Network.Plugins;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    internal static void RecoverSnapshotCommits(ClashOfRimNetworkState state)
    {
        using var mutation = EnterRecoveredSnapshotMutation(state);
    }

    // Returns ownership of the mutation gate on this thread. Callers must dispose before awaiting.
    private static RecoveredSnapshotMutation EnterRecoveredSnapshotMutation(ClashOfRimNetworkState state,
        Func<ProtocolResponse?>? authorize = null, [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
    {
        bool nested = Monitor.IsEntered(state.RaidSettlementSnapshotMutationGate);
        while (true)
        {
            SnapshotCommitService.RecoveryPreparation? preparation;
            var scope = state.EnterSnapshotMutation(operation);
            try
            {
                var rejection = authorize?.Invoke();
                if (rejection is not null) return new(scope, rejection);
                RecoverBeforeSnapshotCommits(state);
                preparation = nested ? null : state.SnapshotCommits.CaptureRecoveryPreparation(state.SnapshotStore);
                // A nested caller may own other domain gates, so its existing boundary cannot be released here.
                if (preparation is null || !preparation.NeedsPreparation)
                {
                    RecoverSnapshotCommitRecords(state);
                    // Recovery itself can revoke a session or change event-based authorization.
                    return new(scope, authorize?.Invoke());
                }
            }
            catch { scope.Dispose(); throw; }
            scope.Dispose();
            Exception? failure = null;
            try { SnapshotPreparationService.Run(() => { preparation.Prepare(); return true; }); }
            catch (Exception ex) { failure = ex; }
            scope = state.EnterSnapshotMutation(operation);
            try
            {
                var rejection = authorize?.Invoke();
                if (rejection is not null) return new(scope, rejection);
                RecoverBeforeSnapshotCommits(state);
                // A competing recovery may already have completed or replaced the captured intent.
                if (preparation.IsCurrent)
                {
                    if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                    if (preparation.TryInstall())
                    {
                        RecoverSnapshotCommitRecords(state);
                        return new(scope, authorize?.Invoke());
                    }
                }
            }
            catch { scope.Dispose(); throw; }
            scope.Dispose();
        }
    }

    private sealed class RecoveredSnapshotMutation(SnapshotMutationScope scope, ProtocolResponse? rejection) : IDisposable
    {
        public ProtocolResponse? Rejection { get; } = rejection;
        public void Dispose() => scope.Dispose();
    }

    private static void RecoverBeforeSnapshotCommits(ClashOfRimNetworkState state)
    {
        if (state.ColonyDeletions.IsApplying)
            throw new InvalidOperationException("Cannot begin nested mutations while a colony deletion is being applied.");
        RecoverWorldLayerNotifications(state);
        RecoverAdminBaselineTradeInvalidations(state);
        state.ColonyDeletions.Recover(intent => ApplyColonyDeletion(state, intent));
    }

    private static void RecoverSnapshotCommitRecords(ClashOfRimNetworkState state)
    {
        state.SnapshotCommits.Recover(state.SnapshotStore, state.BankLoans,
            (snapshot, kind) => RunCommittedSnapshotPostUpload(state, snapshot, kind), state.Ledger,
            userId => state.EventNotifications.SignalUsers(new[] { userId }),
            item => RecordAuthoritativeEventAchievements(state, item, item.AppliedAtUtc ?? item.CreatedAtUtc), state.ServerShop,
            state.MercenaryContracts, state.MercenaryGuards, state.Players,
            (snapshot, kind, mutation) => RunCommittedOrdinaryUpload(state, snapshot, kind, mutation),
            (mutation, snapshot) => PlanSnapshotConfirmations(state, mutation, snapshot));
        RecoverMercenaryIncidents(state);
    }

    private static void RecoverMercenaryIncidents(ClashOfRimNetworkState state) =>
        MercenaryIncidentRecovery.Recover(state.BankLoans, state.MercenaryContracts, state.Ledger,
            debt => CreateBankFineNotification(state, debt), user => state.EventNotifications.SignalUser(user));

    internal static void RecoverWorldLayerNotifications(ClashOfRimNetworkState state)
    {
        using (state.EnterSnapshotMutation())
        {
            foreach (var work in state.WorldConfiguration.ListPendingLayerNotifications())
            {
                state.Ledger.ApplyBatch(new LedgerBatch(work.Notifications, []));
                state.EventNotifications.SignalUsers(work.Notifications.Select(item => item.Target.UserId));
                state.WorldConfigurationNotifications.SignalUsers(work.WorldNotifyUsers.Concat(state.Players.List().Select(player => player.UserId)));
                state.WorldConfiguration.CompleteLayerNotifications(work.Id);
            }
        }
    }

    private static void RunCommittedSnapshotPostUpload(ClashOfRimNetworkState state, LatestSnapshotRecord snapshot, string? kind)
    {
        RunSnapshotPostUploadProcessors(state, snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!, null,
            SnapshotUploadResult.Accept(snapshot, kind), snapshot.AcceptedAtUtc);
    }

    private static void RunCommittedOrdinaryUpload(ClashOfRimNetworkState state, LatestSnapshotRecord snapshot, string? kind,
        SnapshotUploadMutation mutation)
    {
        RunSnapshotPostUploadProcessors(state, snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!, null,
            SnapshotUploadResult.Accept(snapshot, kind), snapshot.AcceptedAtUtc,
            new SnapshotPostUploadExtraData(kind, mutation.ConfirmationOperation, mutation.AchievementCandidates),
            registerPlayerColonySite: mutation.ConfirmationOperation != SnapshotConfirmationOperations.ColonyRelocation);
    }
}
