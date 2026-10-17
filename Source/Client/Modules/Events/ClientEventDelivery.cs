using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Events;

internal interface IClientEventLetterRuntime
{
    bool ShouldPost(ModEventDetailDto detail, string? group);
    bool HasOpenLetter(string eventId);
    void RegisterPendingManualEvent(string eventId);
    void ShowLetter(ModEventDetailDto detail, string group, string source);
    ServerNotificationPayloadSummary? ReadNotification(ModEventDetailDto detail);
    bool ApplyRelation(ClientEventRelationChange change);
}

internal sealed record ClientEventRelationChange(string EventId, string UserId, ServerEventType EventType,
    bool IsResponse, string? RelatedEventId = null);

// Owns ordering and successful-effect bookkeeping. The runtime port only adapts
// existing eligibility/rendering rules and game effects, never the full Mod object.
internal sealed class ClientEventDelivery
{
    private readonly ClientEventInbox inbox;
    private readonly IClientEventLetterRuntime runtime;

    internal ClientEventDelivery(ClientEventInbox inbox, IClientEventLetterRuntime runtime)
    { this.inbox = inbox; this.runtime = runtime; }

    internal void Post(IReadOnlyCollection<ModEventDetailDto> details, string source)
    {
        using var publication = ClientSessionWork.EnterPublication();
        // Keep the original batch order: immediate relations, response relations, letters.
        foreach (var detail in details)
        {
            if (!ClientEventDiplomacyPolicy.IsImmediateDiplomacyEvent(detail)
                || string.IsNullOrWhiteSpace(detail.EventId) || string.IsNullOrWhiteSpace(detail.Actor?.UserId)) continue;
            Apply(new(detail.EventId, detail.Actor!.UserId, detail.EventType, false), ClientEventEffect.Diplomacy);
        }
        foreach (var detail in details)
        {
            if (detail.EventType != ServerEventType.ServerNotification || string.IsNullOrWhiteSpace(detail.EventId)
                || inbox.HasEffect(detail.EventId, ClientEventEffect.ServerNotification)) continue;
            var payload = runtime.ReadNotification(detail);
            // Only accepted diplomacy decisions carry a relation. Other related event
            // kinds must not fall through to the renderer's default neutral relation.
            if (payload?.RelatedAccepted != true || string.IsNullOrWhiteSpace(payload.RelatedUserId)
                || payload.RelatedEventType is not { } relatedType || !ClientEventDiplomacyPolicy.IsDiplomacyEvent(relatedType)) continue;
            Apply(new(detail.EventId, payload.RelatedUserId!, payload.RelatedEventType.Value, true, payload.RelatedEventId),
                ClientEventEffect.ServerNotification);
        }
        foreach (var detail in details)
        {
            string eventId = detail.EventId ?? string.Empty;
            string? group = inbox.GroupOf(eventId);
            if (!runtime.ShouldPost(detail, group) || inbox.HasEffect(eventId, ClientEventEffect.Letter)
                || runtime.HasOpenLetter(eventId)) continue;
            if (group == "WaitingForUserChoice") runtime.RegisterPendingManualEvent(eventId);
            runtime.ShowLetter(detail, group ?? "Unknown", source);
            inbox.TryMarkEffect(eventId, ClientEventEffect.Letter);
        }
    }

    private void Apply(ClientEventRelationChange change, ClientEventEffect kind)
    {
        if (!inbox.HasEffect(change.EventId, kind) && runtime.ApplyRelation(change))
            inbox.TryMarkEffect(change.EventId, kind);
    }
}
