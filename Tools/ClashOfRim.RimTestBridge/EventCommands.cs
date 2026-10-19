using RimTest.Runtime;

namespace ClashOfRim.RimTestBridge;

internal sealed class EventCommands
{
    internal EventCommands(CommandRouter router, ObjectRegistry objects, EventDetailsReader readEvents)
    {
        var eventDetails = new EventDetailsObservation(readEvents);
        router.Register("clash.events", "Read an isolated snapshot of the client event inbox.", "observe", a =>
        {
            return ObjectInspector.Paginate(eventDetails.Read().Select(objects.Record).ToArray(), a);
        });
    }
}
