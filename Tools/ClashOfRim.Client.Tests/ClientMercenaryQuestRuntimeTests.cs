using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using HarmonyLib;
using RimWorld;
using Verse;

internal static class ClientMercenaryQuestRuntimeTests
{
    private static Pawn pawn = null!;
    private static ThingWithComps shuttle = null!;
    private static TransportShip ship = null!;
    private static LetterStack letters = null!;
    private static bool pawnGone, shuttleDestroyed, shuttleSpawned, shipDisposed, waits;
    private static int recalls, releases;
    private static Action? recalling;
    private static readonly List<Task> work = new();
    private static readonly List<(string Label, string Body)> notices = new();
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var binding = ClientMercenaryQuestCallbacks.Current;
        using var runtime = new ClientQuestTestRuntime("ClashOfRim.Tests.MercenaryQuest");
        using var gui = new ClientOverlayTestRuntime("ClashOfRim.Tests.MercenaryQuest");
        var harmony = new Harmony("ClashOfRim.Tests.MercenaryQuest.Engine");
        void Prefix(System.Reflection.MethodBase method, string name) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientMercenaryQuestRuntimeTests), name));
        Prefix(AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Destroyed)), nameof(Destroyed));
        Prefix(AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Spawned)), nameof(Spawned));
        Prefix(AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.LabelShort)), nameof(Label));
        Prefix(AccessTools.PropertyGetter(typeof(TransportShip), nameof(TransportShip.Disposed)), nameof(Disposed));
        Prefix(AccessTools.PropertyGetter(typeof(Find), nameof(Find.LetterStack)), nameof(Letters));
        Prefix(AccessTools.Method(typeof(ClashMercenaryQuestUtility), nameof(ClashMercenaryQuestUtility.TryRecallMercenary)), nameof(Recall));
        Prefix(AccessTools.Method(typeof(ClashMercenaryQuestUtility), nameof(ClashMercenaryQuestUtility.ReleaseMercenaryAfterRecallShuttleDestroyed)), nameof(Release));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(LetterStack)).Where(m => m.Name == nameof(LetterStack.ReceiveLetter) && m.GetParameters().Length > 2 && m.GetParameters()[2].ParameterType == typeof(LetterDef)))
            Prefix(method, nameof(Letter));
        foreach (string name in new[] { "TickAsync", "SignalAsync", "HostileAsync", "KilledAsync" })
            harmony.Patch(AccessTools.Method(typeof(ClientMercenaryQuestCallbacks), name), postfix: new HarmonyMethod(typeof(ClientMercenaryQuestRuntimeTests), nameof(Observe)));
        harmony.Patch(AccessTools.Method(typeof(ClientMercenaryQuestLifecycle), nameof(ClientMercenaryQuestLifecycle.TryStartRecall)), postfix: new HarmonyMethod(typeof(ClientMercenaryQuestRuntimeTests), nameof(ObserveRecall)));
        try
        {
            foreach (string mode in new[] { "due", "manual", "immediate", "missing", "hostile", "death", "other-death", "satisfied", "unsatisfied", "destroyed", "foreign-signal", "loaded-gone", "saved-loaded-gone", "loaded-destroyed", "disposed", "empty-destroyed" })
            {
                using var f = new ClientMercenaryQuestLifecycleTests.Fixture(); var part = Setup(f, runtime, gui);
                if (mode is "satisfied" or "unsatisfied" or "destroyed" or "foreign-signal" || mode.Contains("loaded") || mode is "disposed" or "empty-destroyed")
                { part.RecallStarted = part.RecallShuttleWaiting = true; part.RecallShuttle = shuttle; part.RecallShip = ship; }
                if (mode.StartsWith("loaded", StringComparison.Ordinal) || mode == "disposed")
                {
                    var container = (ThingOwner<Thing>)shuttle.TryGetComp<CompTransporter>()!.innerContainer;
                    container.InnerListForReading.Add(pawn); pawn.holdingOwner = container;
                    Check(container.Contains(pawn), "Loaded test pawn is not owned by its transporter.");
                }
                if (mode == "saved-loaded-gone") part.RecallShuttleLoaded = true;
                if (mode is "loaded-gone" or "saved-loaded-gone") shuttleSpawned = false;
                if (mode is "loaded-destroyed" or "empty-destroyed") shuttleDestroyed = true;
                if (mode == "disposed") shipDisposed = true;
                if (mode == "missing") pawnGone = true;
                if (mode == "immediate") waits = false;
                runtime.Ticks = mode == "due" ? 100 : 99;
                void Fire()
                {
                    if (mode is "manual" or "immediate") part.TryStartRecallNow();
                    else if (mode == "hostile") part.ReportHarmfulSurgery();
                    else if (mode is "death" or "other-death") part.Notify_PawnKilled(mode == "death" ? pawn : New<Pawn>(), null);
                    else if (mode is "satisfied" or "unsatisfied" or "destroyed" or "foreign-signal") part.Notify_QuestSignalReceived(new Signal(mode switch
                    { "satisfied" => "task.SentSatisfied", "unsatisfied" => "task.SentUnsatisfied", "destroyed" => "task.Destroyed", _ => "other.Destroyed" }));
                    else part.QuestPartTick();
                }
                Fire(); await Pump(f); Fire(); await Pump(f);
                bool success = mode is "immediate" or "satisfied" or "loaded-gone" or "saved-loaded-gone" or "loaded-destroyed" or "disposed";
                bool failure = mode is "missing" or "death" or "destroyed" or "unsatisfied" or "empty-destroyed";
                Check(part.Completed == (success || failure), "Engine completion changed: " + mode);
                Check(recalls == (mode is "due" or "manual" or "immediate" ? 1 : 0), "Engine recall repeated or selected another branch: " + mode);
                string? incident = success ? "Completed" : mode == "hostile" ? "HostileBehavior" : mode == "death" ? "Death"
                    : mode is "destroyed" or "unsatisfied" or "empty-destroyed" ? "ShuttleDestroyed" : null;
                Check(f.Core.Commands.Count == (incident is null ? 0 : 1) && (incident is null || f.Core.Commands[0].Kind == incident), "Engine callback sent wrong incident: " + mode);
                if (success || failure)
                {
                    var ending = runtime.Endings.Single();
                    Check(ending.Part == part && ending.Outcome == (success ? QuestEndOutcome.Success : QuestEndOutcome.Fail)
                        && ending.Letter == (mode == "missing") && ending.Sound == failure, "Quest ending notifications changed: " + mode);
                }
                Check(releases == (incident == "ShuttleDestroyed" ? 1 : 0), "Recall failure released the pawn incorrectly: " + mode);
                if (mode == "hostile") Check(part.HostileBehaviorReported && notices.Single().Label.EndsWith("HostileBehaviorLetterLabel"), "Harmful surgery alias lost hostility notice.");
                if (mode is "manual" or "due") Check(part.RecallShuttleWaiting && part.RecallShuttle == shuttle && part.RecallShip == ship && f.Core.Uploads == 0,
                    "Native shuttle references were not preserved while waiting.");
            }
            foreach (string change in new[] { "unbound", "old-game", "removed-quest", "removed-part", "rebound", "translation-user", "translation-game", "recall-user", "relogin", "end-removes-quest" })
            {
                using var f = new ClientMercenaryQuestLifecycleTests.Fixture(); var part = Setup(f, runtime, gui); var callbacks = ClientMercenaryQuestCallbacks.Current!;
                part.QuestPartTick(); await Pump(f); notices.Clear();
                switch (change)
                {
                    case "unbound": ClientMercenaryQuestCallbacks.Attach(null); break;
                    case "old-game": Current.Game = New<Game>(); f.Core.Ports.GameOwner = Current.Game; break;
                    case "removed-quest": runtime.Manager.QuestsListForReading.Clear(); break;
                    case "removed-part": part.quest.PartsListForReading.Remove(part); break;
                    case "rebound": ClientMercenaryQuestCallbacks.Attach(new(f.Flow)); break;
                    case "translation-user": gui.Translating = _ => { gui.Translating = null; f.Core.Change("user"); }; break;
                    case "translation-game": gui.Translating = _ => { gui.Translating = null; Current.Game = New<Game>(); f.Core.Ports.GameOwner = Current.Game; }; break;
                    case "recall-user": recalling = () => f.Core.Change("user"); break;
                    case "relogin": f.Core.Session.PublishLogin(new() { AuthToken = f.Core.Ports.Auth, SessionId = "again" }); break;
                    case "end-removes-quest": waits = false; runtime.Ending = _ => runtime.Manager.QuestsListForReading.Remove(part.quest); break;
                }
                if (change == "rebound") callbacks.TryStartRecall(part); else part.TryStartRecallNow();
                await Pump(f);
                bool started = change is "translation-user" or "translation-game" or "recall-user" or "relogin" or "end-removes-quest";
                Check(recalls == (started ? 1 : 0), "Retained callback changed source eligibility: " + change);
                Check(part.RecallShuttleWaiting == (change == "relogin") && notices.Count == (change is "relogin" or "end-removes-quest" ? 1 : 0), "Native/translated callback published under replacement identity: " + change);
                Check(f.Core.Commands.Count == (change == "end-removes-quest" ? 1 : 0), "Ending removed the quest before its valid confirmation, or old callback reported: " + change);
                if (change == "recall-user") Check(part.RecallShuttle is null && part.RecallShip is null, "Native recall result crossed ownership after callback.");
            }
        }
        finally { harmony.UnpatchAll(harmony.Id); ClientMercenaryQuestCallbacks.Attach(binding); work.Clear(); }
    }
    private static QuestPart_ClashMercenary Setup(ClientMercenaryQuestLifecycleTests.Fixture f, ClientQuestTestRuntime runtime, ClientOverlayTestRuntime gui)
    {
        gui.Reset(); f.Core.Ports.GameOwner = runtime.NewGame(); work.Clear(); notices.Clear(); recalling = null; recalls = releases = 0;
        pawnGone = shuttleDestroyed = shipDisposed = false; waits = shuttleSpawned = true;
        pawn = New<Pawn>(); pawn.def = new ThingDef { defName = "Human" };
        shuttle = new ThingWithComps { def = new ThingDef { defName = "Shuttle" } }; ship = New<TransportShip>(); letters = New<LetterStack>();
        var transporter = new CompTransporter { parent = shuttle }; transporter.innerContainer = new ThingOwner<Thing>(transporter);
        AccessTools.Field(typeof(ThingWithComps), "comps").SetValue(shuttle, new List<ThingComp> { transporter });
        var part = new QuestPart_ClashMercenary { ContractId = "contract", QuestTag = "task", Pawn = pawn, PawnLabel = "佣兵 İ", ExpiresAtGameTicks = 100, HarmfulSurgeryFineSilver = 99, DeathFineSilver = 199 };
        runtime.Add(part); ClientMercenaryQuestCallbacks.Attach(new(f.Flow)); return part;
    }
    private static async Task Pump(ClientMercenaryQuestLifecycleTests.Fixture f)
    { var task = Task.WhenAll(work.ToArray()); work.Clear(); await f.Pump(task); }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static bool Destroyed(Thing __instance, ref bool __result) { __result = ReferenceEquals(__instance, pawn) ? pawnGone : ReferenceEquals(__instance, shuttle) && shuttleDestroyed; return false; }
    private static bool Spawned(Thing __instance, ref bool __result) { __result = ReferenceEquals(__instance, pawn) || ReferenceEquals(__instance, shuttle) && shuttleSpawned; return false; }
    private static bool Label(ref string __result) { __result = "佣兵 İ"; return false; }
    private static bool Disposed(ref bool __result) { __result = shipDisposed; return false; }
    private static bool Letters(ref LetterStack __result) { __result = letters; return false; }
    private static bool Letter(object[] __args) { notices.Add((__args[0].ToString()!, __args[1].ToString()!)); return false; }
    private static bool Recall(Pawn pawn, string questTag, ref bool waitsForLoading, ref Thing? recallShuttle, ref TransportShip? recallShip, ref bool __result)
    {
        Check(ReferenceEquals(pawn, ClientMercenaryQuestRuntimeTests.pawn) && questTag == "task", "Native recall lost original pawn/tag.");
        recalls++; recalling?.Invoke(); waitsForLoading = waits; recallShuttle = shuttle; recallShip = ship; __result = true; return false;
    }
    private static bool Release(Pawn? pawn) { Check(ReferenceEquals(pawn, ClientMercenaryQuestRuntimeTests.pawn), "Release selected a different pawn."); releases++; return false; }
    private static void Observe(Task __result) => work.Add(__result);
    private static void ObserveRecall(Task work) => ClientMercenaryQuestRuntimeTests.work.Add(work);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
