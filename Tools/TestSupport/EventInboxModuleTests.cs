using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Protocol;

internal static class EventInboxModuleTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        try
        {
            ClientSessionWork.Reset();
            QueueAndDetailOwnership();
            ConfirmationAndReset();
            await ConcurrentEffects();
            await ExpiredPublication();
        }
        finally { ClientSessionWork.Reset(); }
    }

    private static void QueueAndDetailOwnership()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var inbox = new ClientEventInbox();
                var queue = new ModEventQueueSummaryDto
                {
                    DirectlyProcessable = new() { Reference("I"), Reference(" "), Reference("I"), Reference("i") },
                    WaitingForUserChoice = new() { Reference("choice") },
                    DeliveredUnconfirmed = new() { Reference("delivered") },
                    Conflicts = new() { Reference("I") },
                    Rejected = new() { Reference("rejected") }
                };
                inbox.ReplaceQueue(queue);
                var ids = inbox.CopyQueuedIds();
                Require(ids.SequenceEqual(new[] { "I", "i", "choice", "delivered", "rejected" }), "Queue order, de-duplication or ordinal identity changed.");
                Require(inbox.GroupOf("I") == "Conflicts" && inbox.IsInGroup("i", "DirectlyProcessable")
                    && inbox.IsInGroup("choice", "WaitingForUserChoice") && inbox.IsInGroup("delivered", "DeliveredUnconfirmed")
                    && inbox.IsInGroup("rejected", "Rejected") && !inbox.IsInGroup(" ", "Rejected") && inbox.GroupOf("absent") is null,
                    "Reported groups or missing-event semantics changed.");
                queue.DirectlyProcessable[0].EventId = "changed";
                queue.WaitingForUserChoice.Clear();
                Require(inbox.CopyQueuedIds().SequenceEqual(ids) && inbox.IsInGroup("choice", "WaitingForUserChoice"), "Input queue remains mutable through its caller.");

                var original = Detail("choice");
                var incoming = new List<ModEventDetailDto> { original, Detail("choice", "second"), Detail("I") };
                inbox.ReplaceDetails(incoming);
                original.Status = "mutated";
                original.Actor!.UserId = "foreign";
                original.Target!.ColonyId = "foreign-colony";
                original.TargetContext!.MapUniqueId = "foreign-map";
                incoming.Clear();
                var copied = inbox.CopyDetails();
                Require(copied.Count == 3 && copied[0].Status == "first" && copied[0].Actor!.UserId == "sender"
                    && copied[0].Target!.ColonyId == "colony" && copied[0].TargetContext!.MapUniqueId == "Map_5",
                    "Input details or nested identities leaked into the stored inbox.");
                copied[0].PayloadSummary = "changed";
                copied[0].Actor!.SnapshotId = "changed";
                copied[0].Target!.UserId = "changed";
                copied[0].TargetContext!.Tile = 99;
                var found = inbox.FindDetail("choice")!;
                Require(found.Status == "first" && found.EventType == ServerEventType.ItemDelivery
                    && found.PayloadType == EventPayloadType.ItemDelivery && found.PayloadSummary == "payload 中文 à"
                    && found.Actor!.SnapshotId == "snapshot" && found.Target!.UserId == "recipient"
                    && found.TargetContext!.Tile == 5 && found.TargetContext.WorldObjectId == "world"
                    && found.TargetContext.LandingMode == "MapEdge" && inbox.FindDetail("Choice") is null,
                    "A returned detail changed the cache, a field was lost, or first-match lookup changed.");
                found.TargetContext!.LandingMode = "changed";
                Require(inbox.FindDetail("choice")!.TargetContext!.LandingMode == "MapEdge", "Lookup returned internal mutable state.");
                inbox.ForgetGroup("choice");
                Require(inbox.GroupOf("choice") is null && inbox.FindDetail("choice") is not null && inbox.CopyQueuedIds().Contains("choice"),
                    "Confirmation removed diagnostic data instead of only its actionable group.");
                inbox.ReplaceQueue(null);
                Require(inbox.CopyQueuedIds().Count == 0 && inbox.GroupOf("I") is null && inbox.CopyDetails().Count == 3 && ids.Count == 5,
                    "A queue refresh changed independent details or a prior read.");
                inbox.ReplaceDetails(null);
                Require(inbox.CopyDetails().Count == 0 && copied.Count == 3, "Null details did not clear the cache or mutated an earlier read.");
                inbox.ReplaceQueue(new ModEventQueueSummaryDto { DirectlyProcessable = null!, Rejected = new() { Reference("legacy") } });
                Require(inbox.CopyQueuedIds().SequenceEqual(new[] { "legacy" }), "A missing legacy queue group became required.");
                inbox.ReplaceDetails(new[] { new ModEventDetailDto { EventId = "minimal" } });
                Require(inbox.FindDetail("minimal")!.Actor is null && inbox.FindDetail("minimal")!.TargetContext is null, "Optional detail fields became required.");
            }
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    private static void ConfirmationAndReset()
    {
        var inbox = new ClientEventInbox();
        inbox.ReplaceQueue(new() { WaitingForUserChoice = new() { Reference("same") } });
        inbox.ReplaceDetails(new[] { Detail("same") });
        Require(inbox.TryTrackPendingConfirmation("same") && !inbox.TryTrackPendingConfirmation("same")
            && inbox.TryTrackPendingConfirmation("Same"), "Duplicate delivery acquired confirmation ownership twice.");
        Require(inbox.PendingConfirmationCount == 2 && inbox.FirstPendingConfirmation == "same" && inbox.HasPendingConfirmation("Same"),
            "Pending confirmation order or ordinal de-duplication changed.");
        inbox.ForgetPendingConfirmation("same");
        Require(inbox.PendingConfirmationCount == 1 && inbox.FirstPendingConfirmation == "Same" && !inbox.HasPendingConfirmation("same"), "Confirmation removed the wrong event.");
        foreach (ClientEventEffect effect in Enum.GetValues(typeof(ClientEventEffect)))
            Require(inbox.TryMarkEffect("same", effect) && !inbox.TryMarkEffect("same", effect), "Effect kinds collided or duplicate execution was allowed.");
        var generation = ClientSessionWork.Token;
        inbox.Reset();
        Require(generation == ClientSessionWork.Token && inbox.GroupOf("same") is null && inbox.CopyQueuedIds().Count == 0
            && inbox.CopyDetails().Count == 0 && inbox.PendingConfirmationCount == 0 && inbox.FirstPendingConfirmation is null,
            "Reset left part of the event state or created a second session generation.");
        foreach (ClientEventEffect effect in Enum.GetValues(typeof(ClientEventEffect)))
            Require(!inbox.HasEffect("same", effect) && inbox.TryMarkEffect("same", effect), "A previous session suppressed a new effect.");
    }

    private static async Task ConcurrentEffects()
    {
        var inbox = new ClientEventInbox();
        int applied = 0;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            if (inbox.TryMarkEffect("event", ClientEventEffect.Diplomacy)) Interlocked.Increment(ref applied);
            inbox.TryTrackPendingConfirmation("event");
        })));
        Require(applied == 1 && inbox.PendingConfirmationCount == 1, "Concurrent publication duplicated an effect or confirmation.");
    }

    private static async Task ExpiredPublication()
    {
        var inbox = new ClientEventInbox();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int rejected = 0;
        Action[] late = {
            () => inbox.ReplaceQueue(null), () => inbox.ReplaceDetails(null), () => inbox.ForgetGroup("new"),
            () => inbox.TryMarkEffect("old", ClientEventEffect.Letter), () => inbox.TryTrackPendingConfirmation("old"),
            () => inbox.ForgetPendingConfirmation("new"), inbox.Reset
        };
        Task old = ClientSessionWork.Run(async () =>
        {
            started.SetResult(true); await release.Task;
            foreach (Action action in late)
            {
                try { action(); }
                catch (OperationCanceledException) { rejected++; }
            }
        });
        await started.Task;
        ClientSessionWork.Reset(); inbox.Reset();
        inbox.ReplaceQueue(new() { WaitingForUserChoice = new() { Reference("new") } });
        inbox.ReplaceDetails(new[] { Detail("new") }); inbox.TryTrackPendingConfirmation("new");
        release.SetResult(true); await old;
        Require(rejected == late.Length && inbox.IsInGroup("new", "WaitingForUserChoice") && inbox.FindDetail("new") is not null
            && inbox.FirstPendingConfirmation == "new" && inbox.PendingConfirmationCount == 1 && !inbox.HasEffect("old", ClientEventEffect.Letter),
            "Expired response/completion mutated the replacement session inbox.");
    }

    private static ModEventReferenceDto Reference(string id) => new() { EventId = id };
    private static ModEventDetailDto Detail(string id, string status = "first") => new()
    {
        EventId = id, Status = status, EventType = ServerEventType.ItemDelivery, PayloadType = EventPayloadType.ItemDelivery,
        PayloadSummary = "payload 中文 à", Actor = new() { UserId = "sender", ColonyId = "colony", SnapshotId = "snapshot" },
        Target = new() { UserId = "recipient", ColonyId = "colony", SnapshotId = "snapshot" },
        TargetContext = new() { MapUniqueId = "Map_5", WorldObjectId = "world", Tile = 5, LandingMode = "MapEdge" }
    };
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
