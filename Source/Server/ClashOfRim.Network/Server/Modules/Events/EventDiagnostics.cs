using AIRsLight.ClashOfRim.Events;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network.Modules.Events;

internal static class EventDiagnostics
{
    internal static void LogAppend(
        ILogger logger,
        LedgerAppendResult append,
        string source)
    {
        AuthoritativeEvent evt = append.Event;
        logger.LogInformation(
            "Event {Action}: source={Source} type={Type} id={EventId} status={Status} delivery={DeliveryMode} actor={ActorUser}/{ActorColony} target={TargetUser}/{TargetColony} rejection={RejectionPolicy} result={Result} payload={PayloadType}",
            append.Created ? "created" : "duplicate",
            source,
            evt.Type,
            evt.EventId,
            evt.Status,
            evt.DeliveryMode,
            evt.Actor.UserId,
            evt.Actor.ColonyId,
            evt.Target.UserId,
            evt.Target.ColonyId,
            evt.RejectionPolicy,
            evt.LastApplicationResult,
            evt.Payload.GetType().Name);
    }

}
