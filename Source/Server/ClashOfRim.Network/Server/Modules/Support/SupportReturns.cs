using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Support;

// One payload contract for voluntary return, rejection and loss (including raid cleanup).
internal static class SupportReturns
{
    internal static AuthoritativeEvent CreateSupportPawnFinishReturnEvent(AuthoritativeEvent source,
        SupportPawnEventPayload payload, FinishSupportPawnRequest request, PawnExchangePackage? package,
        bool online, DateTimeOffset at) => Create(source, payload, request.PawnName,
            payload.PawnReference, package ?? payload.PawnPackage, request.FinishReason, null,
            SupportClosureKeys.Finish(source.EventId, request.FinishReason), online, at);

    internal static AuthoritativeEvent CreateSupportPawnLossReturnEvent(AuthoritativeEvent source,
        SupportPawnEventPayload payload, string? pawnName, string reason, bool online, DateTimeOffset at) =>
        Create(source, payload, pawnName,
            (payload.PawnReference ?? new CrossMapPawnReference(payload.PawnGlobalKey, payload.SourceSnapshotId,
                pawnName ?? payload.PawnName, null, null)) with { Dead = true },
            null, reason, null, SupportClosureKeys.Loss(source.EventId, reason), online, at);

    internal static AuthoritativeEvent CreateRejectedSupportPawnReturnEvent(AuthoritativeEvent source,
        SupportPawnEventPayload payload, string? rejection, bool online, DateTimeOffset at) =>
        Create(source, payload, payload.PawnName, payload.PawnReference, payload.PawnPackage,
            "Rejected", rejection, source.IdempotencyKey + ":rejected-return", online, at);

    private static AuthoritativeEvent Create(AuthoritativeEvent source, SupportPawnEventPayload payload,
        string? pawnName, CrossMapPawnReference? reference, PawnExchangePackage? package, string reason,
        string? rejection, string key, bool online, DateTimeOffset at) =>
        AuthoritativeEventFactory.Create(ServerEventType.SupportPawn, source.Target, source.Actor, key, online,
            new SupportPawnEventPayload(payload.PawnGlobalKey, payload.SourceSnapshotId, pawnName ?? payload.PawnName,
                TemporaryControl: false, ExpectedReturnAtUtc: null, reference, package,
                payload.SourceTile, payload.SourceCaravanLoadId, ReturnToSender: true, rejection,
                payload.PermanentSupport, payload.SupportDurationDays, payload.ExpiresAtGameTicks,
                payload.AutoReturnOnSettlement, source.EventId, reason),
            at, payload.SourceTile is null ? null : new EventTargetContext(null, null, payload.SourceTile, EventLandingMode.MapEdge));
}
