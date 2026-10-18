using AIRsLight.ClashOfRim.Network.Modules.Economy;
using AIRsLight.ClashOfRim.Network.Modules.Raids;

namespace AIRsLight.ClashOfRim.Network.Modules.Sessions;

internal sealed class ClientEntryLifecycle(RaidProtection protection, PendingConfirmationReconciler pending)
{
    internal void Apply(ClientLifecycleEvent entry)
    {
        if (entry.Kind is not (ClientLifecycleEventKind.LoggedIn or ClientLifecycleEventKind.InitialWorldSessionPrepared)
            || string.IsNullOrWhiteSpace(entry.UserId) || string.IsNullOrWhiteSpace(entry.ColonyId)) return;
        if (entry.Kind == ClientLifecycleEventKind.LoggedIn) protection.OnLifecycle(entry);
        pending.ReconcileAfterRecovery(entry.UserId, entry.ColonyId, entry.OccurredAtUtc, forceCancel: true);
    }
}
