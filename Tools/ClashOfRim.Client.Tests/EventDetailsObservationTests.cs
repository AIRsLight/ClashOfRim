using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Events;
using ClashOfRim.RimTestBridge;

internal static class EventDetailsObservationTests
{
    internal static void Run()
    {
        var inbox = new ClientEventInbox();
        var observation = new EventDetailsObservation(inbox.CopyDetails);
        Require(observation.Read().Count == 0, "Initial empty observation failed.");
        inbox.ReplaceDetails(new[] { new ModEventDetailDto { EventId = "a" }, new ModEventDetailDto { EventId = "b" } });
        var first = observation.Read();
        var handles = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (int poll = 0; poll < 10000; poll++)
            foreach (var detail in observation.Read()) handles.Add(detail);
        Require(handles.Count == 2 && ReferenceEquals(first, observation.Read()), "Unchanged polling allocated new object handles.");
        inbox.ReplaceQueue(new() { DirectlyProcessable = new() { new() { EventId = "a" } } });
        inbox.TryMarkEffect("a", ClientEventEffect.Letter);
        inbox.TryTrackPendingConfirmation("a");
        Require(ReferenceEquals(first, observation.Read()), "Unrelated inbox state invalidated detail observations.");
        inbox.ReplaceDetails(new[] { new ModEventDetailDto { EventId = "a", Status = "confirmed" } });
        var changed = observation.Read();
        Require(changed.Single().Status == "confirmed" && !ReferenceEquals(changed, first) && first.Count == 2,
            "A detail update was missed or mutated an earlier observation.");
        changed[0].Status = "observer-only";
        Require(inbox.FindDetail("a")!.Status == "confirmed", "An observation handle changed product state.");
        inbox.Reset();
        Require(observation.Read().Count == 0 && changed.Count == 1, "Reset retained old detail handles as current data.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
