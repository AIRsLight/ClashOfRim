using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using ClashOfRim.GameTests;
using RimTest.Protocol;
using RimTest.Testing;

internal static class GameSocialScenarioTests
{
    internal static void Chat() => ChatAsync().GetAwaiter().GetResult();
    internal static void Diplomacy() => DiplomacyAsync().GetAwaiter().GetResult();
    internal static void Appearance() => AppearanceAsync().GetAwaiter().GetResult();
    private static async Task ChatAsync()
    {
        var culture = CultureInfo.CurrentCulture; var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                using var f = await ChatFixture.Create();
                await f.Scenario.Public(); await f.Scenario.Private(); await f.Scenario.UnicodeAndBlankRejection();
                Check(f.Sent.Count == 4 && f.Sent[1] == "@rimtest-B RimTest private exchange" && f.Sent[3] == " \t ", "Chat changed its original commands or blank-input test.");
                Check(f.DecoyReads == 3 && f.Marked == 1, "Chat selected an incidental text match or omitted the read marker.");
                Check(f.Runtime.Calls.Where(c => c.Method == "clash.records").All(c => c.Client == "B"), "Chat read another client's feed.");
            }
        }
        finally { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = uiCulture; }
        foreach (var (fault, scenario) in new (string, Func<ChatScenario, Task>)[] {
            ("public-channel", s => s.Public()), ("public-target", s => s.Public()), ("private-channel", s => s.Private()),
            ("private-target", s => s.Private()), ("unread-missing", s => s.Private()), ("read-not-cleared", s => s.Private()),
            ("duplicate", s => s.UnicodeAndBlankRejection()), ("blank", s => s.UnicodeAndBlankRejection()), ("snapshot", s => s.UnicodeAndBlankRejection()) })
        {
            using var f = await ChatFixture.Create(fault); await Reject(() => scenario(f.Scenario), fault);
        }
        foreach (string client in new[] { "A", "B" })
        {
            using var f = await ChatFixture.Create(); f.Message = "RimTest private exchange";
            await f.Scenario.VerifyPrivateHistory(client);
            Check(f.Sent.Count == 0 && f.Marked == 0 && f.Runtime.Calls.All(c => c.Client == client), "History verification sent a new message or changed another client.");
        }
        const string text = "中文 Français İı 😀";
        Check(ChatScenario.HasText(Wire.Element(new { data = new { text } }), text), "Chat did not decode escaped text.");
        Check(!ChatScenario.HasText(Wire.Element(new { data = new { text = "prefix " + text, displayName = text } }), text), "Chat matched JSON metadata/substrings instead of message text.");
    }
    private static async Task DiplomacyAsync()
    {
        foreach (string generation in new[] { "first", "rebuilt" })
        {
            using var f = await DiplomacyFixture.Create(generation);
            await f.Scenario.Alliance();
            Check(f.Created == 1 && f.Accepted == 1 && f.PlayerDecoys == 2 && f.EventDecoys == 1, "Diplomacy skipped delayed current-colony/player/event observations.");
            Check(f.Runtime.Calls.Where(c => c.Method == "clash.events").All(c => c.Client == "B" && c.Args.Int("limit") == 200), "Diplomacy changed recipient or observation limit.");
            Check(f.Runtime.Calls.Count(c => c.Method == "clash.invoke" && c.Args.String("action") == "StartRefreshPlayers") == 2, "Diplomacy skipped post-decision player refresh.");
        }
        using (var f = await DiplomacyFixture.Create("rebuilt", stopBeforeCurrent: true))
        {
            await Interrupted(f.Scenario.Alliance);
            Check(f.Accepted == 0 && f.Created == 1, "An unrelated/historical request was accepted before a current request appeared.");
        }
        using (var f = await DiplomacyFixture.Create("rebuilt", interruptDecision: true))
        {
            await Interrupted(f.Scenario.Alliance);
            Check(f.Created == 1 && f.Accepted == 1, "Interrupted diplomacy action was retried.");
        }
        var current = DiplomacyFixture.Event("test", ServerEventType.AllianceRequest, "rimtest-A", "actor", "rimtest-B", "target");
        Check(DiplomacyScenario.IsAllianceRequest(Wire.Element(current), "actor", "target"), "Typed alliance request was rejected.");
        foreach (var (key, value) in new[] { ("eventType", "warDeclaration"), ("actor.userId", "other"), ("actor.colonyId", "old"), ("target.userId", "other"), ("target.colonyId", "old") })
        {
            var node = JsonSerializer.SerializeToNode(current, Wire.Json)!; var path = key.Split('.');
            if (path.Length == 1) node["data"]![path[0]] = value; else node["data"]![path[0]]![path[1]] = value;
            Check(!DiplomacyScenario.IsAllianceRequest(Wire.Element(node), "actor", "target"), "Diplomacy ignored typed identity: " + key);
        }
    }
    private static async Task AppearanceAsync()
    {
        foreach (var original in new ColonyAppearanceDto?[] { null, new("Icon", "Town", "Blue", null), new("Color", colorHex: "A1B2C3") })
        {
            using var f = await AppearanceFixture.Create(original, delay: true);
            await f.Scenario.Run();
            Check(f.Saves == 2 && f.MarkerReads == 2 && ColonyAppearanceScenario.Matches(f.Current(), original), "Appearance did not restore the current colony or rejected the original metadata.");
            Check(f.World.Current!.PlayerColonySites.Single(s => s.ColonyId == "old").Appearance!.IconDefName == "OldIcon", "Appearance changed a previous colony generation.");
            Check(f.Runtime.Calls.Where(c => c.Method == "clash.records").All(c => c.Client == "B"), "Appearance did not verify the other client's marker.");
        }
        foreach (string fault in new[] { "save-response", "marker-command", "marker-observation", "restore-response" })
        {
            using var f = await AppearanceFixture.Create(new("Icon", "Town", "Blue", "102030"), fault);
            await Interrupted(f.Scenario.Run);
            Check(f.Saves == 2 && ColonyAppearanceScenario.Matches(f.Current(), f.Original), "Appearance failure left temporary settings active: " + fault);
        }
        var expected = new ColonyAppearanceDto("Icon", "Town", "Blue", "102030");
        foreach (var changed in new[] { new ColonyAppearanceDto("Color", "Town", "Blue", "102030"), new("Icon", "Other", "Blue", "102030"), new("Icon", "Town", "Other", "102030"), new("Icon", "Town", "Blue", "FFFFFF") })
            Check(!ColonyAppearanceScenario.Matches(changed, expected), "Appearance ignored one of the four fields.");
        Check(ColonyAppearanceScenario.Matches(null, new()), "Empty appearance was not equivalent to the product's null representation.");
    }
    private sealed class ChatFixture : IDisposable
    {
        internal readonly GameScenarioTestRuntime Runtime;
        internal readonly ChatScenario Scenario;
        internal readonly List<string> Sent = [];
        internal string Message = "";
        internal int DecoyReads, Marked;
        private readonly string fault;
        private bool privateMessage, blank, stale;
        private int busy;
        private ChatFixture(GameScenarioTestRuntime runtime, string fault)
        { Runtime = runtime; this.fault = fault; Scenario = new(runtime.Run, runtime.Polling); runtime.Script = Reply; }
        internal static async Task<ChatFixture> Create(string fault = "none") => new(await GameScenarioTestRuntime.Create(), fault);
        private object? Reply(string client, string method, JsonElement args)
        {
            if (method == "clash.state") return new { chatInProgress = busy-- > 0, unreadPrivateChatCount = fault == "unread-missing" ? 0 : Marked == 0 || fault == "read-not-cleared" ? 1 : 0, currentSnapshotId = blank && fault == "snapshot" ? "changed" : "before" };
            if (method == "clash.records")
            {
                Check(args.String("kind") == "chat", "Chat queried another record kind.");
                object decoy = new { data = new { text = "prefix " + Message, displayName = Message, channel = "Private", targetUserId = "rimtest-B" } };
                if (stale) { stale = false; DecoyReads++; return List(decoy); }
                var records = new List<object> { decoy, new { data = new { text = Message,
                    channel = privateMessage ? fault == "private-channel" ? "Public" : "Private" : fault == "public-channel" ? "Private" : "Public",
                    targetUserId = privateMessage ? fault == "private-target" ? "other" : "rimtest-B" : fault == "public-target" ? "rimtest-B" : null } } };
                if (fault == "duplicate") records.Add(records[1]);
                if (blank && fault == "blank") records.Add(new { data = new { text = " \t " } });
                return List(records.ToArray());
            }
            if (method != "clash.invoke") return null;
            switch (args.String("action"))
            {
                case "StartSendChatMessage":
                    Check(client == "A", "Chat used the wrong sender.");
                    string raw = args.GetProperty("arguments").String("rawText"); Sent.Add(raw); busy = 1;
                    blank = string.IsNullOrWhiteSpace(raw);
                    if (!blank) { privateMessage = raw.StartsWith("@rimtest-B "); Message = privateMessage ? raw[11..] : raw; }
                    break;
                case "StartRefreshChatMessages": stale = !blank; break;
                case "MarkPrivateChatRead": Check(client == "B", "Read marker used sender state."); Marked++; break;
                default: throw new InvalidOperationException("Unexpected chat action.");
            }
            return new { returnValue = true };
        }
        public void Dispose() => Runtime.Dispose();
    }
    private sealed class DiplomacyFixture : IDisposable
    {
        internal readonly GameScenarioTestRuntime Runtime;
        internal readonly DiplomacyScenario Scenario;
        internal int Created, Accepted, PlayerDecoys, EventDecoys;
        private readonly string actor, target;
        private readonly bool stopBeforeCurrent, interruptDecision;
        private bool stalePlayer, staleEvent;
        private int serial;
        private string lease = "";
        private DiplomacyFixture(GameScenarioTestRuntime runtime, string generation, bool stopBeforeCurrent, bool interruptDecision)
        {
            Runtime = runtime; actor = generation + "-owner"; target = generation + "-guest"; this.stopBeforeCurrent = stopBeforeCurrent; this.interruptDecision = interruptDecision;
            Scenario = new(runtime.Run, runtime.Polling, new(runtime.Run, runtime.Polling)); runtime.Script = Reply;
        }
        internal static async Task<DiplomacyFixture> Create(string generation, bool stopBeforeCurrent = false, bool interruptDecision = false)
            => new(await GameScenarioTestRuntime.Create(), generation, stopBeforeCurrent, interruptDecision);
        private object? Reply(string client, string method, JsonElement args)
        {
            if (method == "clash.state") return new { colonyId = client == "A" ? actor : target };
            if (method == "clash.records")
            {
                Check(client == "A" && args.String("kind") == "players", "Diplomacy read players on the wrong client.");
                var old = new { reference = new { handle = "old-player" }, data = new { userId = "rimtest-B", colonyId = "old-guest", relationKind = "Ally" } };
                if (stalePlayer) { stalePlayer = false; PlayerDecoys++; return List(old); }
                lease = "current-player-" + serial++;
                return List(old, new { reference = new { handle = lease }, data = new { userId = "rimtest-B", colonyId = target, relationKind = Accepted > 0 ? "Ally" : "Neutral" } });
            }
            if (method == "clash.events")
            {
                var history = Event("historical", ServerEventType.AllianceRequest, "rimtest-A", actor, "rimtest-B", target);
                if (Created == 0) return List(history);
                var events = new List<object> { history, Event("wrong-type", ServerEventType.ServerNotification, "rimtest-A", actor, "rimtest-B", target),
                    Event("foreign-actor", ServerEventType.AllianceRequest, "other", actor, "rimtest-B", target),
                    Event("old-actor", ServerEventType.AllianceRequest, "rimtest-A", "old-owner", "rimtest-B", target),
                    Event("foreign-target", ServerEventType.AllianceRequest, "rimtest-A", actor, "other", target),
                    Event("old-target", ServerEventType.AllianceRequest, "rimtest-A", actor, "rimtest-B", "old-guest") };
                if (staleEvent) { staleEvent = false; EventDecoys++; return List(events.ToArray()); }
                if (stopBeforeCurrent) throw new IOException("Stopped before current request appeared.");
                events.Add(Event("current-request", ServerEventType.AllianceRequest, "rimtest-A", actor, "rimtest-B", target));
                return List(events.ToArray());
            }
            if (method != "clash.invoke") return null;
            var input = args.GetProperty("arguments");
            switch (args.String("action"))
            {
                case "StartRefreshPlayers": Check(client == "A", "Diplomacy refreshed another player's perspective."); stalePlayer = true; break;
                case "StartCreateDiplomacyEvent":
                    Check(client == "A" && input.GetProperty("target").String("handle") == lease && input.String("kind") == "AllianceRequest" && input.String("customMessage") == "RimTest alliance", "Diplomacy used another generation, stale reference or changed request.");
                    Created++; staleEvent = true; break;
                case "HandleEventLetterAction":
                    Check(client == "B" && input.String("eventId") == "current-request" && input.String("actionKind") == "Accept", "Diplomacy accepted a historical/foreign event.");
                    Accepted++; if (interruptDecision) throw new IOException("Interrupted accept response."); break;
                default: throw new InvalidOperationException("Unexpected diplomacy action.");
            }
            return new { returnValue = true };
        }
        internal static object Event(string id, ServerEventType type, string actor, string actorColony, string target, string targetColony) => new { data = new
        { eventId = id, eventType = type, actor = new { userId = actor, colonyId = actorColony }, target = new { userId = target, colonyId = targetColony }, payloadSummary = "Localized text: AllianceRequest" } };
        public void Dispose() => Runtime.Dispose();
    }
    private sealed class AppearanceFixture : IDisposable
    {
        internal readonly GameScenarioTestRuntime Runtime;
        internal readonly ColonyAppearanceScenario Scenario;
        internal readonly WorldConfigurationRegistry World = GameWorldTestFixture.ReadyRegistry();
        internal readonly ColonyAppearanceDto? Original;
        internal int Saves, MarkerReads;
        private readonly string fault;
        private readonly bool delay;
        private Task? pending;
        private AppearanceFixture(GameScenarioTestRuntime runtime, ColonyAppearanceDto? original, string fault, bool delay)
        {
            Runtime = runtime; Original = original; this.fault = fault; this.delay = delay;
            World.RegisterPlayerColonySites("rimtest-A", "old", [new("rimtest-A", "old", "old-world", "old-map", 5, "Old colony", appearance: new("Icon", "OldIcon"))]);
            Publish(original); Scenario = new(runtime.Run, runtime.Polling, World); runtime.Script = Reply;
        }
        internal static async Task<AppearanceFixture> Create(ColonyAppearanceDto? original, string fault = "none", bool delay = false)
            => new(await GameScenarioTestRuntime.Create(), original, fault, delay);
        internal ColonyAppearanceDto? Current() => World.Current!.PlayerColonySites.Single(s => s.ColonyId == "rebuilt").Appearance;
        private void Publish(ColonyAppearanceDto? appearance) => World.RegisterPlayerColonySites("rimtest-A", "rebuilt", [new("rimtest-A", "rebuilt", "new-world", "new-map", 10, "Rebuilt colony", appearance: appearance)]);
        private object? Reply(string client, string method, JsonElement args)
        {
            if (method == "clash.state") return new { colonyId = "rebuilt" };
            if (method == "clash.records")
            {
                Check(client == "B" && args.String("kind") == "markers", "Appearance observed another domain/client.");
                if (fault == "marker-observation") throw new IOException("Interrupted marker observation.");
                MarkerReads++;
                object Marker(string user, string colony, ColonyAppearanceDto? appearance) => new { data = new { ownerUserId = user, ownerColonyId = colony, appearance } };
                var changed = new ColonyAppearanceDto("Color", colorHex: "38A4E8");
                var wrong = new[] { Marker("rimtest-A", "old", changed), Marker("other", "rebuilt", changed), Marker("rimtest-A", "rebuilt", new("Icon", colorHex: "38A4E8")) };
                return MarkerReads == 1 ? List(wrong) : List(wrong.Append(Marker("rimtest-A", "rebuilt", Current())).ToArray());
            }
            if (method != "clash.invoke") return null;
            switch (args.String("action"))
            {
                case "SaveColonyAppearance":
                    Check(client == "A", "Appearance saved the observer's settings."); Saves++;
                    var input = args.GetProperty("arguments");
                    var appearance = input.Deserialize<ColonyAppearanceDto>(Wire.Json)!;
                    if (delay && Saves == 1) pending = Task.Run(async () => { await Task.Delay(120); Publish(appearance); });
                    else Publish(appearance);
                    if (fault == "save-response" && Saves == 1 || fault == "restore-response" && Saves == 2) throw new IOException("Interrupted appearance response.");
                    break;
                case "StartSyncWorldMapMarkers":
                    Check(client == "B" && Current()?.ColorHex == "38A4E8", "Appearance skipped authoritative registration before refreshing markers.");
                    if (fault == "marker-command") throw new IOException("Interrupted marker request."); break;
                default: throw new InvalidOperationException("Unexpected appearance action.");
            }
            return new { returnValue = true };
        }
        public void Dispose() { pending?.GetAwaiter().GetResult(); Runtime.Dispose(); }
    }
    private static object List(params object[] items) => new { items };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action, string context)
    { try { await action(); } catch (AssertionException) { return; } throw new InvalidOperationException("Expected social assertion: " + context); }
    private static async Task Interrupted(Func<Task> action)
    { try { await action(); } catch (IOException) { return; } throw new InvalidOperationException("Expected injected observation/response interruption."); }
}
