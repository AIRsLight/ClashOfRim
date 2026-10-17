using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Support;

// Called within event delivery's existing recovered mutation boundary.
internal sealed class SupportDeliveryReconciliation(IAuthoritativeEventLedger ledger, PlayerRegistry players,
    EventNotificationHub notifications)
{
    internal int ReconcileDeletedActors(
        string? visibleUserId)
    {
        if (string.IsNullOrWhiteSpace(visibleUserId))
        {
            return 0;
        }

        int updated = 0;
        foreach (AuthoritativeEvent ledgerEvent in ledger.ListForUser(visibleUserId!))
        {
            if (ledgerEvent.Type != ServerEventType.SupportPawn
                || ledgerEvent.Status != ServerEventStatus.Failed
                || !string.IsNullOrWhiteSpace(ledgerEvent.AppliedSnapshotId)
                || string.IsNullOrWhiteSpace(ledgerEvent.Actor.UserId)
                || string.IsNullOrWhiteSpace(ledgerEvent.Actor.ColonyId)
                || !players.IsDeleted(ledgerEvent.Actor.UserId, ledgerEvent.Actor.ColonyId))
            {
                continue;
            }

            ledger.ChangeStatus(ledgerEvent.EventId, ServerEventStatus.Cancelled);
            updated++;
        }

        if (updated > 0)
        {
            notifications.SignalUser(visibleUserId!);
        }

        return updated;
    }

}
