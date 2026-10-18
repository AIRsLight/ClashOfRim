namespace AIRsLight.ClashOfRim.Events;

// An explicit target response completes diplomacy without applying a colony snapshot.
// Keep this separate from the automatic acceptance performed while reading notifications.
public sealed record LedgerDiplomacyDecisionChange(AuthoritativeEvent Expected, bool Accepted,
    DateTimeOffset DecidedAtUtc, string? Reason)
{
    public AuthoritativeEvent Updated => Accepted
        ? new LedgerAcceptanceChange(Expected, DecidedAtUtc, Reason).Updated
        : new LedgerRejectionChange(Expected, DecidedAtUtc, Reason).Updated;

    public static bool CanDecide(AuthoritativeEvent source) => EventSemanticClassifier.IsDiplomacyEventType(source.Type)
        && source.TargetDecision == TargetEventDecision.None
        && source.Status is ServerEventStatus.Recorded or ServerEventStatus.PendingOfflineDelivery
            or ServerEventStatus.ReadyForImmediateDelivery or ServerEventStatus.DeliveredToClient or ServerEventStatus.Conflict;
}
