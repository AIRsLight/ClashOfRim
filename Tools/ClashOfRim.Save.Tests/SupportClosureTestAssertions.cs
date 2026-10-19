using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using static SupportClosureTestData;
using static SnapshotTestAssertions;

internal static class SupportClosureTestAssertions
{
    public static void Closed(IReadOnlyList<AuthoritativeEvent> events, string kind)
    {
        var source = events.Single(item => item.EventId == "source");
        Check(events.Count == 2 && source.Status == (kind == "reject" ? ServerEventStatus.RejectedByTarget
            : kind == "loss" ? ServerEventStatus.Failed : ServerEventStatus.AppliedToSnapshot), "closure source status missing");
        var result = events.Single(item => item.EventId != "source");
        var payload = (SupportPawnEventPayload)result.Payload;
        Check(result.Actor == source.Target && result.Target == source.Actor && payload.SourceEventId == source.EventId
            && payload.ReturnToSender && payload.PawnGlobalKey == PawnKey, "return not bound to original support");
        if (kind == "reject") Check(source.TargetDecision == TargetEventDecision.Rejected
            && source.LastApplicationResult == EventApplicationResultKind.Rejected && source.DecisionAtUtc == result.CreatedAtUtc
            && source.DecisionReason == payload.RejectionReason, "rejection metadata not committed with return");
    }
}
