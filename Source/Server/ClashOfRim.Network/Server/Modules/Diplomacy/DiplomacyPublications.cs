using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using static AIRsLight.ClashOfRim.Events.EventSemanticClassifier;
using static AIRsLight.ClashOfRim.Network.Modules.Diplomacy.DiplomacyPolicy;

namespace AIRsLight.ClashOfRim.Network.Modules.Diplomacy;

// The ledger owns durable facts; this is a rebuildable work index, used under the snapshot mutation gate.
internal sealed class DiplomacyPublications(IAuthoritativeEventLedger ledger, DiplomacyRelationRegistry relations,
    OnlinePresenceRegistry presence, Action<string> signalUser)
{
    [Flags] private enum Phase { Creation = 1, Decision = 2 }
    private readonly Dictionary<string, Phase> pending = new(StringComparer.Ordinal);
    private bool initialized;

    internal void TrackCreation(string eventId) => Track(eventId, Phase.Creation);
    internal void TrackDecision(string eventId) => Track(eventId, Phase.Decision);
    private void Track(string eventId, Phase phase) => pending[eventId] = pending.GetValueOrDefault(eventId) | phase;

    internal static DateTimeOffset NextRelationTime(DiplomacyRelationRecord? current, DateTimeOffset now) =>
        current is not null && current.UpdatedAtUtc >= now ? current.UpdatedAtUtc.AddTicks(1) : now;

    internal void Recover()
    {
        if (!initialized)
        {
            foreach (var type in Enum.GetValues<ServerEventType>().Where(IsDiplomacyEventType))
                foreach (var item in ledger.ListByType(type))
                {
                    TrackCreation(item.EventId);
                    if (HasDecision(item)) TrackDecision(item.EventId);
                }
            foreach (var item in ledger.ListByType(ServerEventType.ServerNotification).Where(IsSupportRequestNotification))
                TrackCreation(item.EventId);
            initialized = true;
        }
        if (pending.Count == 0) return;

        // Publish the newest relation first so replaying historical notifications never rewinds the projection.
        var work = pending.Select(pair => (Id: pair.Key, Phase: pair.Value, Source: ledger.Find(pair.Key)))
            .OrderByDescending(item => item.Source is null ? null : RelationTime(item.Source))
            .ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var ambiguous = work.Where(item => item.Source is not null && RelationTime(item.Source) is not null)
            .GroupBy(item => (Pair: PairKey(item.Source!), At: RelationTime(item.Source!)!.Value))
            .Where(group => group.Select(item => ResolveDiplomacyRelationKind(item.Source!.Type, true)).Distinct().Skip(1).Any())
            .Select(group => group.Key).ToHashSet();
        foreach (var item in work)
        {
            var source = item.Source;
            if (source is not null && !ColonyTombstones.IsDeleted(ledger, source.Actor) && !ColonyTombstones.IsDeleted(ledger, source.Target))
            {
                AuthoritativeEvent? response = (item.Phase & Phase.Decision) != 0 ? RestoreResponse(source) : null;
                if (RelationTime(source) is { } at)
                {
                    var current = relations.GetRelation(source.Actor.UserId, source.Actor.ColonyId, source.Target.UserId, source.Target.ColonyId);
                    if (current is null || current.UpdatedAtUtc < at)
                    {
                        string kind = ResolveDiplomacyRelationKind(source.Type, true)!;
                        if (ambiguous.Contains((PairKey(source), at)))
                            throw new InvalidDataException("Diplomacy history has conflicting relations at the same time: " + source.EventId);
                        relations.SetRelationKind(source.Actor.UserId, source.Actor.ColonyId, source.Target.UserId, source.Target.ColonyId,
                            kind, source.EventId, at);
                    }
                }
                if ((item.Phase & Phase.Creation) != 0 && NeedsWakeup(source)) signalUser(source.Target.UserId);
                if (response is not null && NeedsWakeup(response)) signalUser(response.Target.UserId);
            }
            pending.Remove(item.Id);
        }
    }

    private AuthoritativeEvent? RestoreResponse(AuthoritativeEvent source)
    {
        if (!HasDecision(source)) return null;
        if (FindResponse(source) is { } existing) return existing;
        // Unilateral notices are also automatically accepted by the read path. Without a receipt,
        // an old explicit acknowledgement is indistinguishable from a read and must not be invented.
        if (source.Type is ServerEventType.WarDeclaration or ServerEventType.AllianceCancellation) return null;
        bool accepted = source.TargetDecision == TargetEventDecision.Accepted;
        var notice = CreateDiplomacyResponseNotification(source, accepted, presence.IsUserOnline(source.Actor.UserId), source.DecisionAtUtc!.Value);
        return ledger.Append(notice).Event;
    }

    internal AuthoritativeEvent? FindResponse(AuthoritativeEvent source)
    {
        if (!HasDecision(source)) return null;
        bool accepted = source.TargetDecision == TargetEventDecision.Accepted;
        var notice = ledger.FindByIdempotencyKey(ResponseNotificationKey(source.EventId, accepted));
        if (notice is not null) ValidateResponse(notice, source, accepted);
        return notice;
    }

    private static void ValidateResponse(AuthoritativeEvent notice, AuthoritativeEvent source, bool accepted)
    {
        string key = ResponseNotificationKey(source.EventId, accepted);
        if (notice.Type != ServerEventType.ServerNotification || notice.IdempotencyKey != key
            || !AuthoritativeEventFactory.IsKnownEventId(notice.Type, key, notice.EventId)
            || notice.Actor != new EventParty("server") || notice.Target != source.Actor
            || notice.Payload is not ServerNotificationEventPayload payload || payload.NotificationId != key
            || payload.RelatedEventId != source.EventId || payload.RelatedEventType != source.Type
            || payload.RelatedUserId != source.Target.UserId || payload.RelatedColonyId != source.Target.ColonyId
            || payload.RelatedAccepted != accepted)
            throw new InvalidDataException("Diplomacy response receipt does not match its decision: " + source.EventId);
    }

    private static bool HasDecision(AuthoritativeEvent source) => IsDiplomacyEventType(source.Type) && source.DecisionAtUtc is not null
        && (source.Status == ServerEventStatus.AppliedToSnapshot && source.TargetDecision == TargetEventDecision.Accepted
            || source.Status == ServerEventStatus.RejectedByTarget && source.TargetDecision == TargetEventDecision.Rejected);

    private static DateTimeOffset? RelationTime(AuthoritativeEvent source) => source.Type switch
    {
        ServerEventType.WarDeclaration or ServerEventType.AllianceCancellation
            when source.Status is not (ServerEventStatus.Cancelled or ServerEventStatus.Failed) => source.CreatedAtUtc,
        ServerEventType.AllianceRequest or ServerEventType.PeaceRequest
            when HasDecision(source) && source.TargetDecision == TargetEventDecision.Accepted => source.DecisionAtUtc,
        _ => null
    };

    private static bool NeedsWakeup(AuthoritativeEvent item) => item.Status is ServerEventStatus.Recorded
        or ServerEventStatus.PendingOfflineDelivery or ServerEventStatus.ReadyForImmediateDelivery or ServerEventStatus.DeliveredToClient;

    private static string PairKey(AuthoritativeEvent source) => DiplomacyRelationRegistry.RelationKey(
        source.Actor.UserId, source.Actor.ColonyId, source.Target.UserId, source.Target.ColonyId);
}
