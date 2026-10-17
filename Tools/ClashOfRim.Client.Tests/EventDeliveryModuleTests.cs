using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.EventLetters;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Protocol;
using RimWorld;
using Verse;
using HarmonyLib;

internal static class EventDeliveryModuleTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "fr-FR", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                PostingAndDedupe();
                RelationSemantics();
                FailureRetry();
                MapIdentities();
            }
            await ExpiredPublication();
            Projection();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); throw; }
        finally { CultureInfo.CurrentCulture = original; ClientSessionWork.Reset(); }
    }

    private static void PostingAndDedupe()
    {
        var f = new Fixture();
        var a = Detail("a", ServerEventType.AllianceRequest);
        var b = Detail("b", ServerEventType.PeaceRequest);
        var war = Detail("war", ServerEventType.WarDeclaration);
        var response = Notification("response", ServerEventType.AllianceRequest, true);
        f.Inbox.ReplaceQueue(new() { WaitingForUserChoice = Refs(a, b), DirectlyProcessable = Refs(war, response) });
        f.Runtime.Open.Add("b");
        f.Delivery.Post(new[] { a, b, response, war }, "read");
        Require(f.Runtime.Calls.SequenceEqual(new[] { "relation:war", "relation:response", "pending:a", "letter:a", "letter:response", "letter:war" }), "Batch order or open-letter suppression changed.");
        Require(f.Runtime.Shown.All(x => x.Source == "read") && f.Runtime.Shown[0].Group == "WaitingForUserChoice", "Rendering lost source/group.");
        int calls = f.Runtime.Calls.Count;
        Parallel.For(0, 32, _ => f.Delivery.Post(new[] { a, b, response, war }, "duplicate"));
        Require(f.Runtime.Calls.Count == calls, "Concurrent/duplicate refresh repeated successful effects.");
        f.Runtime.Open.Clear();
        f.Delivery.Post(new[] { b }, "reopen");
        Require(f.Runtime.Shown.Last().Detail.EventId == "b", "Already-open letter was prematurely recorded as newly posted.");
        f.Inbox.Reset();
        f.Inbox.ReplaceQueue(new() { WaitingForUserChoice = Refs(a) });
        f.Delivery.Post(new[] { a }, "new-session");
        Require(f.Runtime.Shown.Last().Source == "new-session", "Session reset retained delivery dedupe.");
    }

    private static void RelationSemantics()
    {
        foreach (ServerEventType kind in Enum.GetValues<ServerEventType>().Append((ServerEventType)999))
        foreach (bool? accepted in new bool?[] { null, false, true })
        {
            var f = new Fixture();
            var notification = Notification("notice", kind, accepted);
            f.Delivery.Post(new[] { notification }, "test");
            // Server acknowledgements also support non-rejectable war/cancellation events.
            bool expected = accepted == true && kind is ServerEventType.AllianceRequest or ServerEventType.PeaceRequest
                or ServerEventType.WarDeclaration or ServerEventType.AllianceCancellation;
            Require(f.Runtime.Relations.Count == (expected ? 1 : 0)
                && f.Inbox.HasEffect("notice", ClientEventEffect.ServerNotification) == expected,
                "Unrelated or rejected notification changed/consumed a diplomacy relation: " + kind);
        }
        var missing = new Fixture();
        var emptyActor = Detail("missing", ServerEventType.WarDeclaration);
        emptyActor.Actor = null;
        var noEventId = Detail("", ServerEventType.WarDeclaration);
        missing.Delivery.Post(new[] { emptyActor, noEventId }, "test");
        Require(missing.Runtime.Relations.Count == 0, "Incomplete identity applied a relation.");
        foreach (var (kind, expected) in new[] {
            (ServerEventType.AllianceRequest, FactionRelationKind.Ally), (ServerEventType.PeaceRequest, FactionRelationKind.Neutral),
            (ServerEventType.WarDeclaration, FactionRelationKind.Hostile), (ServerEventType.AllianceCancellation, FactionRelationKind.Neutral) })
        {
            Require(ClientEventDiplomacyPolicy.IsDiplomacyEvent(Detail("id", kind))
                && ClientDiplomacyPresentation.ResolveDiplomacyRelationKind(kind) == expected, "Shared relation projection changed.");
        }
        var blankUser = Notification("blank", ServerEventType.PeaceRequest, true);
        blankUser.PayloadSummary = "{\"RelatedAccepted\":true,\"RelatedEventType\":" + (int)ServerEventType.PeaceRequest + ",\"RelatedUserId\":\" \"}";
        missing.Delivery.Post(new[] { blankUser }, "test");
        Require(missing.Runtime.Relations.Count == 0, "Blank related user reached game effects.");
    }

    private static void FailureRetry()
    {
        foreach (bool response in new[] { false, true })
        foreach (bool throws in new[] { false, true })
        {
            var f = new Fixture();
            var detail = response ? Notification("event", ServerEventType.PeaceRequest, true) : Detail("event", ServerEventType.WarDeclaration);
            var effect = response ? ClientEventEffect.ServerNotification : ClientEventEffect.Diplomacy;
            f.Runtime.RelationSucceeds = false;
            f.Runtime.ThrowRelation = throws;
            try { f.Delivery.Post(new[] { detail }, "first"); Require(!throws, "Expected game failure did not propagate."); }
            catch (InvalidOperationException ex) when (ex.Message == "injected relation failure") { }
            Require(!f.Inbox.HasEffect("event", effect), "Unavailable/failed relation was consumed before success.");
            f.Runtime.RelationSucceeds = true;
            f.Runtime.ThrowRelation = false;
            f.Delivery.Post(new[] { detail }, "retry");
            int calls = f.Runtime.Relations.Count;
            f.Delivery.Post(new[] { detail }, "repeat");
            Require(f.Inbox.HasEffect("event", effect) && f.Runtime.Relations.Count == calls, "Successful retry did not deduplicate.");
        }
        var letter = new Fixture();
        var gift = Detail("gift", ServerEventType.ItemDelivery, "{\"Purpose\":0}");
        letter.Inbox.ReplaceQueue(new() { WaitingForUserChoice = Refs(gift) });
        letter.Runtime.ThrowShow = true;
        try { letter.Delivery.Post(new[] { gift }, "first"); throw new Exception("Expected rendering failure."); }
        catch (InvalidOperationException ex) when (ex.Message == "injected letter failure") { }
        Require(!letter.Inbox.HasEffect("gift", ClientEventEffect.Letter), "Failed rendering consumed a letter.");
        letter.Runtime.ThrowShow = false;
        letter.Delivery.Post(new[] { gift }, "retry");
        Require(letter.Inbox.HasEffect("gift", ClientEventEffect.Letter) && letter.Runtime.Shown.Count == 1, "Letter retry did not publish once.");
    }

    private static void MapIdentities()
    {
        Require(ClientMapIdentity.MapIdsMatch("Map_12", "12") && ClientMapIdentity.MapIdsMatch("Map_12", "Map_12"), "Historical numeric map ID no longer resolves.");
        Require(!ClientMapIdentity.MapIdsMatch("", "12") && !ClientMapIdentity.MapIdsMatch("", ""), "Empty current map acquired an identity.");
        foreach (string? target in new[] { null, "", " ", "map_12", "MAP_12", "012", "12 ", "Map_13" })
            Require(!ClientMapIdentity.MapIdsMatch("Map_12", target), "Map identity unexpectedly folded case/whitespace/digits.");
    }

    private static async Task ExpiredPublication()
    {
        var f = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task old = ClientSessionWork.Run(async () =>
        {
            started.SetResult();
            await release.Task;
            f.Delivery.Post(new[] { Detail("old", ServerEventType.WarDeclaration) }, "old");
        });
        await started.Task;
        ClientSessionWork.Reset();
        release.SetResult();
        await old.WaitAsync(TimeSpan.FromSeconds(5));
        Require(f.Runtime.Calls.Count == 0, "Expired session reached the engine port.");
    }

    private static void Projection()
    {
        // The console host has no active game language; stub that environment boundary,
        // while executing the real projection and its shared action/payload policies.
        var harmony = new Harmony("ClashOfRim.Tests.EventDelivery.Projection");
        foreach (var method in typeof(ClashOfRimText).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Where(m => m.Name == "Key"))
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(EventDeliveryModuleTests), nameof(TextKey)));
        var previous = (LetterDefOf.PositiveEvent, LetterDefOf.NeutralEvent, LetterDefOf.NegativeEvent, LetterDefOf.ThreatBig);
        try
        {
            LetterDefOf.PositiveEvent = new LetterDef { defName = "positive" };
            LetterDefOf.NeutralEvent = new LetterDef { defName = "neutral" };
            LetterDefOf.NegativeEvent = new LetterDef { defName = "negative" };
            LetterDefOf.ThreatBig = new LetterDef { defName = "threat" };
            foreach (var kind in new[] { ServerEventType.ItemDelivery, ServerEventType.SupportPawn, ServerEventType.AllianceRequest, ServerEventType.PeaceRequest })
            {
                var detail = Detail("projection", kind, kind == ServerEventType.SupportPawn ? "{\"temporaryControl\":true}" : "{\"Purpose\":0}");
                var projection = ClientEventLetterRenderer.BuildEventLetterProjection(detail, "WaitingForUserChoice");
                Require(projection.EventId == "projection" && projection.HasServerAction
                    && projection.Actions.SequenceEqual(ClientEventLetterPolicy.BuildEventLetterActions(detail, false, "WaitingForUserChoice")), "Real projection diverged from action policy.");
            }
            var server = Detail("notice", ServerEventType.ServerNotification, "{\"Title\":\"维护 à\",\"Message\":\"保留正文\",\"Severity\":2}");
            var notice = ClientEventLetterRenderer.BuildEventLetterProjection(server, "DirectlyProcessable");
            Require(notice.Label == "维护 à" && notice.Text.Contains("保留正文") && notice.LetterDef == LetterDefOf.ThreatBig
                && !notice.HasServerAction, "Server notification projection lost content or severity.");
            foreach (string group in new[] { "DeliveredUnconfirmed", "Conflicts", "Rejected" })
            {
                var readOnly = ClientEventLetterRenderer.BuildEventLetterProjection(Detail("read-only", ServerEventType.ItemDelivery, "{\"Purpose\":0,\"Items\":[]}"), group);
                Require(!readOnly.HasServerAction && readOnly.Text.Contains("ClashOfRim.EventLetter.ReadOnly"), "Read-only queue group regained server action buttons.");
            }
            var settlement = ClientEventLetterRenderer.BuildEventLetterProjection(Detail("settled", ServerEventType.Raid, "{\"Settlement\":{\"Losses\":[]}}"), "Unknown");
            Require(!settlement.HasServerAction && settlement.LetterDef == LetterDefOf.ThreatBig
                && settlement.Text.Contains("ClashOfRim.EventLetter.RaidNoDefenderLoss"), "Detached raid settlement lost its dedicated display.");
            foreach (string payload in new[] { "{\"Purpose\":0}", "{\"Purpose\":0,\"Items\":null}", "{\"Purpose\":0,\"Items\":[]}" })
            {
                Require(ItemDeliveryPayloadReader.Read(payload).Items.Count == 0, "Shared reader did not restore the declared collection default.");
                var empty = ClientEventLetterRenderer.BuildEventLetterProjection(Detail("empty", ServerEventType.ItemDelivery, payload), "WaitingForUserChoice");
                Require(empty.Text.Contains("ClashOfRim.EventLetter.GiftItemsEmpty"), "Empty gift display did not use the existing empty-list message.");
            }
            var populated = ItemDeliveryPayloadReader.Read("{\"Purpose\":0,\"Items\":[{\"Def\":\"Steel\",\"StackCount\":7}]}");
            Require(populated.Items.Single().Def == "Steel" && populated.Items[0].StackCount == 7, "Default restoration altered populated items.");
        }
        finally
        {
            (LetterDefOf.PositiveEvent, LetterDefOf.NeutralEvent, LetterDefOf.NegativeEvent, LetterDefOf.ThreatBig) = previous;
            harmony.UnpatchAll(harmony.Id);
        }
    }

    private static bool TextKey(string key, ref string __result) { __result = key; return false; }

    private sealed class Fixture
    {
        internal readonly ClientEventInbox Inbox = new();
        internal readonly Runtime Runtime = new();
        internal readonly ClientEventDelivery Delivery;
        internal Fixture() => Delivery = new(Inbox, Runtime);
    }
    private sealed class Runtime : IClientEventLetterRuntime
    {
        internal readonly HashSet<string> Open = new(StringComparer.Ordinal);
        internal readonly List<string> Calls = new();
        internal readonly List<ClientEventRelationChange> Relations = new();
        internal readonly List<(ModEventDetailDto Detail, string Group, string Source)> Shown = new();
        internal bool RelationSucceeds = true, ThrowRelation, ThrowShow;
        public bool ShouldPost(ModEventDetailDto detail, string? group) => ClientEventLetterPolicy.ShouldPostEventLetter(detail, group);
        public bool HasOpenLetter(string id) => Open.Contains(id);
        public void RegisterPendingManualEvent(string id) => Calls.Add("pending:" + id);
        public void ShowLetter(ModEventDetailDto detail, string group, string source)
        {
            if (ThrowShow) throw new InvalidOperationException("injected letter failure");
            Calls.Add("letter:" + detail.EventId); Shown.Add((detail, group, source));
        }
        public ServerNotificationPayloadSummary? ReadNotification(ModEventDetailDto detail) => ClientEventLetterPolicy.ReadServerNotificationPayloadForDisplay(detail);
        public bool ApplyRelation(ClientEventRelationChange change)
        {
            if (ThrowRelation) throw new InvalidOperationException("injected relation failure");
            Calls.Add("relation:" + change.EventId); Relations.Add(change); return RelationSucceeds;
        }
    }
    private static List<ModEventReferenceDto> Refs(params ModEventDetailDto[] details) => details.Select(d => new ModEventReferenceDto { EventId = d.EventId }).ToList();
    private static ModEventDetailDto Detail(string id, ServerEventType kind, string? payload = null) => new()
    {
        EventId = id, EventType = kind, Actor = new() { UserId = "actor" }, PayloadSummary = payload,
        PayloadType = Enum.TryParse(kind.ToString(), out EventPayloadType type) ? type : EventPayloadType.Unknown
    };
    private static ModEventDetailDto Notification(string id, ServerEventType related, bool? accepted) => Detail(id, ServerEventType.ServerNotification,
        "{\"RelatedUserId\":\"peer\",\"RelatedEventType\":" + ((int)related).ToString(CultureInfo.InvariantCulture)
        + ",\"RelatedAccepted\":" + (accepted.HasValue ? accepted.Value ? "true" : "false" : "null") + "}");
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
