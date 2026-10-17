using System.Globalization;
using static AIRsLight.ClashOfRim.Network.ServerText;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Mercenaries;

/// <summary>Confirmed fine records are durable work items. Run under the snapshot mutation gate.</summary>
internal sealed class MercenaryIncidentRecovery(BankLoanRegistry bank, MercenaryContractRegistry contracts,
    IAuthoritativeEventLedger ledger, OnlinePresenceRegistry presence, EventNotificationHub notifications)
{
    internal static bool ClosesContract(string reason) => reason is "Mercenary:Death" or "Mercenary:ShuttleDestroyed";

    internal static void Recover(BankLoanRegistry bank, MercenaryContractRegistry contracts, IAuthoritativeEventLedger ledger,
        Func<BankDebtRecord, AuthoritativeEvent> createNotification, Action<string> signalUser)
    {
        foreach (var debt in bank.ListUnfinalizedMercenaryIncidents())
        {
            if (debt.Status == BankLoanRegistry.StatusPendingActivation) continue;
            var contract = contracts.Find(debt.SourceId);
            if (contract is null || contract.UserId != debt.UserId || contract.ColonyId != debt.ColonyId
                || contract.Status is not (MercenaryContractRegistry.StatusActive or MercenaryContractRegistry.StatusCompleted))
                throw new InvalidDataException("Confirmed mercenary fine has no matching active or completed contract: " + debt.DebtId);
            string key = "bank-fine-created:" + debt.DebtId;
            var existing = ledger.FindByIdempotencyKey(key);
            if (existing is not null) ValidateNotification(existing, debt, key);

            if (ClosesContract(debt.Reason) && contract.Status != MercenaryContractRegistry.StatusCompleted)
                contracts.Complete(contract.ContractId, debt.UserId, debt.ColonyId);
            if (existing is null) ValidateNotification(ledger.Append(createNotification(debt)).Event, debt, key);
            // Repeat the wake-up if a previous attempt committed the notification but failed before marking completion.
            signalUser(debt.UserId);
            bank.MarkMercenaryIncidentFinalized(debt.DebtId);
        }
    }

    private static void ValidateNotification(AuthoritativeEvent notification, BankDebtRecord debt, string key)
    {
        if (notification.Type != ServerEventType.ServerNotification || notification.Actor.UserId != "server"
            || notification.Target.UserId != debt.UserId || notification.Target.ColonyId != debt.ColonyId
            || notification.Payload is not ServerNotificationEventPayload payload || payload.NotificationId != key)
            throw new InvalidDataException("Mercenary fine notification key belongs to another event: " + key);
    }

    internal void Recover() => Recover(bank, contracts, ledger, CreateBankFineNotification, user => notifications.SignalUser(user));

    private AuthoritativeEvent CreateBankFineNotification(
        BankDebtRecord debt)
    {
        string notificationId = "bank-fine-created:" + debt.DebtId;
        return AuthoritativeEventFactory.CreateForLedger(ledger,
            ServerEventType.ServerNotification,
            new EventParty("server"),
            new EventParty(debt.UserId, debt.ColonyId),
            notificationId,
            presence.IsUserOnline(debt.UserId),
            new ServerNotificationEventPayload(
                notificationId,
                Format("Bank.FineCreatedTitle"),
                Format(
                    "Bank.FineCreatedMessage",
                    ("AMOUNT", debt.AmountSilver.ToString(CultureInfo.InvariantCulture))),
                ServerNotificationSeverity.Warning,
                FromAdministrator: false,
                RelatedUserId: debt.UserId,
                RelatedColonyId: debt.ColonyId),
            debt.CreatedAtUtc);
    }
}
