using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Support;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Support;
using HarmonyLib;
using Verse;

internal static class ClientSupportReturnRuntimeTests
{
    private static Game game = null!;
    private static ClashOfRimGameComponent component = null!;
    private static Pawn? pawn;
    private static ActiveSupportPawnAssignment assignment = null!;
    private static readonly List<string> trace = new();
    private static bool dead, spawned, destroyed, removeFails, emptyScribe;
    private static readonly ClashOfRimClientNetworkContext context = new("http://server", "recipient", "colony", "base", null, null, "auth");
    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture;
        var harmony = new Harmony("ClashOfRim.Tests.SupportReturnRuntime");
        void Patch(MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientSupportReturnRuntimeTests), prefix));
        Patch(AccessTools.PropertyGetter(typeof(Current), nameof(Current.Game)), nameof(Game));
        Patch(AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.Dead)), nameof(Dead));
        Patch(AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Spawned)), nameof(Spawned));
        Patch(AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Destroyed)), nameof(Destroyed));
        Patch(AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.LabelShort)), nameof(Label));
        Patch(AccessTools.Method(typeof(SupportPawnLookup), nameof(SupportPawnLookup.Find)), nameof(Lookup));
        Patch(AccessTools.Method(typeof(PawnExchangeCapture), nameof(PawnExchangeCapture.Capture)), nameof(Capture));
        Patch(AccessTools.Method(typeof(PawnExchangeLifecycleService), nameof(PawnExchangeLifecycleService.RemoveFromLocalWorld)), nameof(Remove));
        Patch(AccessTools.Method(typeof(ClashSupportPawnQuestUtility), nameof(ClashSupportPawnQuestUtility.CompleteSupportQuest)), nameof(Complete));
        Patch(AccessTools.Method(typeof(ClashOfRimText), nameof(ClashOfRimText.Key), new[] { typeof(string) }), nameof(Text));
        Patch(AccessTools.Method(typeof(ClashOfRimText), nameof(ClashOfRimText.Key), new[] { typeof(string), typeof(NamedArgument[]) }), nameof(Text));
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            foreach (bool pawnDead in new[] { false, true })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); var runtime = Reset(); dead = pawnDead;
                var source = runtime.Find("event")!;
                if (dead)
                {
                    assignment.FinishInProgress = false;
                    Check(source.Depart("EarlyReturn", () => trace.Add("departure")) && assignment.FinishInProgress, "Dead return did not retain departure progress.");
                    trace.Clear();
                }
                var payload = source.Prepare(context, "QuestExpired", () => trace.Add("mutation"));
                Check(trace.SequenceEqual(dead ? new[] { "mutation", "remove" } : new[] { "capture", "mutation", "remove" })
                    && component.HasSupportAssignment("event") && assignment.ReturnPrepared && assignment.ReturnPawnDead == dead,
                    "Local return removed assignment before confirmation or changed effect ordering.");
                Check(payload.FinishReason == "EarlyReturn" && payload.Dead == dead && (payload.Package is null) == dead, "Persisted return identity/reason changed.");
                string saved = Scribe.saver.DebugOutputFor(assignment);
                var restored = Read(saved);
                Check(restored.ReturnPrepared && restored.ReturnPawnDead == dead && restored.ReturnPackageJson == assignment.ReturnPackageJson
                    && restored.PawnReferenceMetadata["original"] == "原始 <&>", "Scribe lost prepared return state.");
                SetAssignment(restored); pawn = null; trace.Clear();
                var reloaded = runtime.Find("event")!;
                Check(reloaded.View is { Departing: true, HasPawn: false, CanFinish: true }, "Reload requires removed pawn.");
                var replay = reloaded.Prepare(context, "WrongRetryReason", () => trace.Add("unexpected"));
                Check(trace.Count == 0 && replay.FinishReason == payload.FinishReason && replay.Package?.Scribe?.Xml == payload.Package?.Scribe?.Xml,
                    "Reload recaptured/removed pawn or changed final report.");
                reloaded.Complete(); Check(!component.HasSupportAssignment("event") && trace.SequenceEqual(new[] { "quest" }), "Accepted return did not finish exactly its assignment/quest.");
            }
            var legacy = Read("<save><eventId>old</eventId><finishInProgress>True</finishInProgress></save>");
            Check(!legacy.ReturnPrepared && !legacy.ReturnPawnDead && legacy.ReturnPackageJson is null && legacy.ReturnFinishReason == "", "Old saves acquired a fabricated return outbox.");
            foreach (string invalid in new[] { "game", "assignment", "walking", "scribe", "remove" })
            {
                var runtime = Reset(); var source = runtime.Find("event")!;
                switch (invalid)
                {
                    case "game": game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game)); break;
                    case "assignment": SetAssignment(new() { EventId = "event" }); break;
                    case "walking": spawned = true; break;
                    case "scribe": emptyScribe = true; break;
                    case "remove": removeFails = true; break;
                }
                bool failed = false;
                try { source.Prepare(context, "EarlyReturn", () => trace.Add("mutation")); } catch (InvalidOperationException) { failed = true; }
                Check(failed && !assignment.ReturnPrepared && !trace.Contains("quest"), "Invalid return became prepared: " + invalid);
                if (invalid != "remove") Check(!trace.Contains("mutation") && !trace.Contains("remove"), "Invalid return mutated local game: " + invalid);
            }
        }
        catch (Exception ex) { Console.WriteLine(ex.ToString()); throw; }
        finally { harmony.UnpatchAll(harmony.Id); CultureInfo.CurrentCulture = culture; Scribe.ForceStop(); trace.Clear(); }
    }
    private static ClientSupportReturnRuntime Reset()
    {
        game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game)); component = new ClashOfRimGameComponent(game);
        AccessTools.Field(typeof(Game), "components").SetValue(game, new List<GameComponent> { component });
        pawn = new Pawn { def = new ThingDef { defName = "Human" }, thingIDNumber = 42 };
        SetAssignment(new() { EventId = "event", PawnThingId = pawn.ThingID, PawnGlobalKey = "owner:original/pawn:Human42", PawnLabel = "支援者",
            FinishInProgress = true, ReturnFinishReason = "EarlyReturn", PawnReferenceMetadata = new() { ["original"] = "原始 <&>" } });
        dead = spawned = destroyed = removeFails = emptyScribe = false; trace.Clear(); return new(new Presentation());
    }
    private static void SetAssignment(ActiveSupportPawnAssignment value)
    { assignment = value; AccessTools.Field(typeof(ClashOfRimGameComponent), "activeSupportAssignments").SetValue(component, new List<ActiveSupportPawnAssignment> { assignment }); }
    private static ActiveSupportPawnAssignment Read(string xml)
    {
        var document = new XmlDocument(); document.LoadXml(xml);
        try
        {
            Scribe.mode = LoadSaveMode.LoadingVars; Scribe.loader.curXmlParent = document.DocumentElement;
            var result = new ActiveSupportPawnAssignment(); result.ExposeData(); return result;
        }
        finally { Scribe.ForceStop(); }
    }
    private static bool Game(ref Game __result) { __result = game; return false; }
    private static bool Dead(ref bool __result) { __result = dead; return false; }
    private static bool Spawned(ref bool __result) { __result = spawned; return false; }
    private static bool Destroyed(ref bool __result) { __result = destroyed; return false; }
    private static bool Label(ref string __result) { __result = "支援者 <&>"; return false; }
    private static bool Lookup(ref Pawn? __result) { __result = pawn; return false; }
    private static bool Capture(Pawn __0, string __1, ref ModPawnExchangePackageDto __result)
    {
        Check(ReferenceEquals(__0, pawn) && __1 == assignment.PawnGlobalKey, "Return changed original owner identity."); trace.Add("capture");
        __result = new() { Reference = new() { GlobalId = __1 }, Scribe = emptyScribe ? null : new() { Xml = "<pawn>支援者</pawn>" } }; return false;
    }
    private static bool Remove(Pawn __0, ref bool __result)
    { Check(ReferenceEquals(__0, pawn), "Removed unrelated pawn."); trace.Add("remove"); destroyed = !removeFails; __result = !removeFails; return false; }
    private static bool Complete(string __0)
    { Check(__0 == "event" && !component.HasSupportAssignment(__0), "Completed wrong quest or retained accepted assignment."); trace.Add("quest"); return false; }
    private static bool Text(string __0, ref string __result) { __result = __0; return false; }
    private sealed class Presentation : IClientSupportPresentation
    {
        public string Text(string key, params (string Name, object? Value)[] arguments) => key;
        public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => admission.ToString();
        public string Busy => "busy";
        public string BaseSnapshotChanged => "base";
        public void Status(string message) { }
        public void Message(string message, bool rejected) { }
        public void Diagnose(Exception exception) { }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
