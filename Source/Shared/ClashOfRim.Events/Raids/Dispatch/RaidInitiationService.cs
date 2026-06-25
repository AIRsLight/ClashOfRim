using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Events;

public static class RaidInitiationService
{
    public static RaidInitiationResult StartRaid(
        IAuthoritativeEventLedger ledger,
        RaidInitiationRequest request,
        RaidEligibilityPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);

        RaidEligibilityResult eligibility = RaidEligibilityChecker.Check(request.Eligibility, policy);
        if (!eligibility.Eligible)
        {
            return RecordRejectedStart(ledger, request, eligibility);
        }

        AuthoritativeEvent raidEvent = AuthoritativeEventFactory.Create(
            ServerEventType.Raid,
            request.Eligibility.Attacker!,
            request.Eligibility.Defender!,
            request.IdempotencyKey,
            targetOnline: request.Eligibility.DefenderOnline,
            new RaidEventPayload(
                request.Eligibility.DefenderSnapshot!.SnapshotId!,
                ReturnedSnapshotId: null,
                StartedAtUtc: request.CreatedAtUtc,
                FinishedAtUtc: null,
                Settlement: null,
                AttackForce: request.AttackForce),
            request.CreatedAtUtc,
            request.TargetContext);

        LedgerAppendResult appendResult = ledger.Append(raidEvent);
        return new RaidInitiationResult(
            appendResult.Created
                ? RaidInitiationResultKind.RaidEventCreated
                : RaidInitiationResultKind.RaidEventAlreadyExists,
            eligibility,
            appendResult.Event,
            NotificationEvent: null,
            appendResult.Created);
    }

    private static RaidInitiationResult RecordRejectedStart(
        IAuthoritativeEventLedger ledger,
        RaidInitiationRequest request,
        RaidEligibilityResult eligibility)
    {
        if (request.Eligibility.Attacker == null || string.IsNullOrWhiteSpace(request.Eligibility.Attacker.UserId))
        {
            return new RaidInitiationResult(
                RaidInitiationResultKind.RejectedWithoutNotification,
                eligibility,
                RaidEvent: null,
                NotificationEvent: null,
                Created: false);
        }

        string reasonText = string.Join(", ", eligibility.FailureReasons);
        AuthoritativeEvent notification = AuthoritativeEventFactory.Create(
            ServerEventType.ServerNotification,
            new EventParty("server"),
            request.Eligibility.Attacker,
            "raid-denied:" + request.IdempotencyKey,
            targetOnline: request.AttackerOnline,
            new ServerNotificationEventPayload(
                "raid-denied:" + request.IdempotencyKey,
                ServerLocalization.Text("Raid.NotificationDeniedTitle"),
                ServerLocalization.Text("Raid.NotificationDeniedMessage", new Dictionary<string, string?> { ["REASON"] = reasonText }),
                ServerNotificationSeverity.Warning,
                FromAdministrator: false,
                AdministratorUserId: null),
            request.CreatedAtUtc,
            targetContext: null);

        LedgerAppendResult appendResult = ledger.Append(notification);
        return new RaidInitiationResult(
            appendResult.Created
                ? RaidInitiationResultKind.RejectedNotificationCreated
                : RaidInitiationResultKind.RejectedNotificationAlreadyExists,
            eligibility,
            RaidEvent: null,
            appendResult.Event,
            appendResult.Created);
    }
}
