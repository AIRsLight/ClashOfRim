using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;
using Verse.Sound;

internal static class RemoteMechClusterTests
{
    private static readonly FieldInfo Defeated = AccessTools.Field(typeof(LordJob_MechanoidDefendBase), "mechClusterDefeated");
    private static readonly List<string> effects = new();
    private static readonly List<Pawn> colonists = new();
    private static SignalManager signals = null!;

    internal static void Run()
    {
        ProjectionPreservesDefeat();
        var harmony = new Harmony("ClashOfRim.Tests.RemoteMechCluster");
        try
        {
            // Native Tick/Notify_LordDestroyed/OnDefeat and quest-signal dispatch
            // execute unchanged. Record only their external/UI side effects.
            Patch(AccessTools.Method(typeof(Lord), nameof(Lord.Notify_MechClusterDefeated)), nameof(Effect));
            Patch(AccessTools.Method(typeof(IdeoUtility), nameof(IdeoUtility.Notify_PlayerRaidedSomeone)), nameof(Effect));
            Patch(AccessTools.Method(typeof(MemoryThoughtHandler), nameof(MemoryThoughtHandler.TryGainMemory), new[] { typeof(ThoughtDef), typeof(Pawn), typeof(Precept) }), nameof(Effect));
            Patch(AccessTools.Method(typeof(Messages), nameof(Messages.Message), new[] { typeof(string), typeof(LookTargets), typeof(MessageTypeDef), typeof(bool) }), nameof(Effect));
            Patch(AccessTools.Method(typeof(SoundStarter), nameof(SoundStarter.PlayOneShotOnCamera)), nameof(Effect));
            Patch(AccessTools.Method(typeof(Translator), nameof(Translator.Translate), new[] { typeof(string) }), nameof(Translate));
            Patch(AccessTools.PropertyGetter(typeof(Map), nameof(Map.IsPlayerHome)), nameof(Home));
            Patch(AccessTools.PropertyGetter(typeof(MapPawns), nameof(MapPawns.FreeColonistsSpawned)), nameof(Colonists));
            Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.SignalManager)), nameof(Signals));
            signals = new SignalManager(); signals.RegisterReceiver(new Receiver());
            var pawn = New<Pawn>(); pawn.needs = new Pawn_NeedsTracker { mood = New<Need_Mood>() };
            pawn.needs.mood.thoughts = new ThoughtHandler(pawn); colonists.Add(pawn);
            var local = CreateMap(new MapParent());
            // Positive control: this fixture reaches all native completion effects.
            Completion(local, false, expected: true);
            harmony.CreateClassProcessor(typeof(RemoteMechClusterDefeatPatch)).Patch();

            foreach (string mode in new[] { RemoteSessionMapParent.ScoutMode, RemoteSessionMapParent.FriendlyObservationMode,
                RemoteSessionMapParent.RaidObservationMode, RemoteSessionMapParent.RaidBattleMode })
            {
                var root = CreateMap(new RemoteSessionMapParent { Mode = mode });
                var child = CreateMap(new PocketMapParent { sourceMap = root });
                var nested = CreateMap(new PocketMapParent { sourceMap = child });
                foreach (var map in new[] { root, child, nested })
                foreach (bool destroying in new[] { false, true })
                {
                    Completion(map, destroying, expected: mode == RemoteSessionMapParent.RaidBattleMode);
                    using (RemoteSessionGlobalStateGuard.BeginSuppressRemoteMapRemovalGlobalEffects())
                        Completion(map, destroying, expected: false);
                }
            }
            // Remote cleanup is scoped by the event's map, never CurrentMap or
            // the presence of a remote session: a simultaneous local win survives.
            foreach (bool destroying in new[] { false, true })
            using (RemoteSessionGlobalStateGuard.BeginSuppressRemoteMapRemovalGlobalEffects())
            {
                Completion(local, destroying, expected: true);
                Completion(CreateMap(new PocketMapParent { sourceMap = local }), destroying, expected: true);
            }
            CleanupBeforeMapRemoval(harmony);
        }
        finally { harmony.UnpatchAll(harmony.Id); effects.Clear(); colonists.Clear(); signals = null!; }
        void Patch(MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(RemoteMechClusterTests), prefix));
    }

    private static void CleanupBeforeMapRemoval(Harmony harmony)
    {
        var token = ClashOfRimCompatibilityApi.BeginRegistrationCycle("mech-cleanup-test");
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(MapPawns), nameof(MapPawns.AllPawns)), prefix: new HarmonyMethod(typeof(RemoteMechClusterTests), nameof(Colonists)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.WorldPawns)), prefix: new HarmonyMethod(typeof(RemoteMechClusterTests), nameof(WorldPawns)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(WorldPawns), nameof(RimWorld.Planet.WorldPawns.AllPawnsAliveOrDead)), prefix: new HarmonyMethod(typeof(RemoteMechClusterTests), nameof(References)));
            harmony.Patch(AccessTools.Method(typeof(ClashLog), nameof(ClashLog.Message)), prefix: new HarmonyMethod(typeof(RemoteMechClusterTests), nameof(Skip)));
            var parent = new RemoteSessionMapParent { Mode = RemoteSessionMapParent.RaidBattleMode, SessionId = "cleanup" };
            var map = CreateMap(parent);
            var pawn = colonists.Single(); pawn.def = new ThingDef { defName = "MechCleanupPawn" }; pawn.thingIDNumber = 1;
            pawn.questTags = new() { "ClashOfRim.RemoteSessionCleanup:cleanup" };
            int calls = 0;
            ClashOfRimCompatibilityApi.RegisterRemoteSessionPawnCleanupHandler((actual, _) =>
            {
                Check(ReferenceEquals(actual, pawn) && RemoteSessionGlobalStateGuard.SuppressRemoteMapRemovalGlobalEffects,
                    "Pawn cleanup reached its callbacks before global-effect suppression.");
                Completion(map, destroying: true, expected: false); calls++; return true;
            });
            Check(RemoteSessionThingCleanup.DiscardMarkedMapThings(map, parent, "test") == 1
                && RemoteSessionThingCleanup.DiscardMarkedReferencePawns(parent, "test") == 1 && calls == 2
                && !RemoteSessionGlobalStateGuard.SuppressRemoteMapRemovalGlobalEffects,
                "Early map/reference-pawn cleanup escaped suppression or leaked it.");
        }
        finally { ClashOfRimCompatibilityApi.RevokeRegistrationToken(token); }
    }

    private static void ProjectionPreservesDefeat()
    {
        foreach (string kind in new[] { nameof(LordJob_MechanoidsDefend), nameof(LordJob_SleepThenMechanoidsDefend) })
        foreach (string? defeated in new[] { null, "False", "True" })
        {
            var xml = XElement.Parse("<lordJob Class='RimWorld." + kind + "'><isMechCluster>True</isMechCluster>"
                + "<things><li>Thing_Mech1</li></things><thingsToNotifyOnDefeat><li>Thing_Listener2</li></thingsToNotifyOnDefeat>"
                + (defeated is null ? "" : "<mechClusterDefeated>" + defeated + "</mechClusterDefeated>") + "</lordJob>");
            object?[] args = { xml, null };
            Check((bool)AccessTools.Method(typeof(RemoteMapSnapshotProjector), "TryCreateRemoteNpcLordSnapshot").Invoke(null, args)!, "Mech lord was not captured.");
            var captured = (RemoteNpcLordSnapshot)args[1]!;
            var clone = captured.CloneWithProjectedThingIds(new Dictionary<string, string> { ["Thing_Mech1"] = "Thing_Mech10", ["Thing_Listener2"] = "Thing_Listener20" });
            // Deferred pockets persist exactly this field-based snapshot format.
            var options = new JsonSerializerOptions { IncludeFields = true };
            clone = JsonSerializer.Deserialize<RemoteNpcLordSnapshot>(JsonSerializer.Serialize(clone, options), options)!;
            var map = CreateMap(new RemoteSessionMapParent { Mode = RemoteSessionMapParent.RaidBattleMode });
            var listener = new DefeatListener();
            var things = new List<Thing> { new Thing() };
            var job = (LordJob_MechanoidDefendBase)AccessTools.Method(typeof(RemoteNpcLordRestorer), "CreateLordJob").Invoke(null,
                new object[] { clone, New<Faction>(), map, things, new Dictionary<string, Thing> { ["Thing_Listener20"] = listener } })!;
            Check((bool)Defeated.GetValue(job)! == (defeated == "True"), "Projection reset an already defeated mech cluster and can replay its rewards.");
            Check(job.things.Count == 1 && ReferenceEquals(job.things[0], things[0]) && clone.ThingLoadIds.Single() == "Thing_Mech10"
                && ((List<Thing>)AccessTools.Field(typeof(LordJob_MechanoidDefendBase), "thingsToNotifyOnDefeat").GetValue(job)!).Single() == listener,
                "Restoring defeat state lost native cluster objects or defeat listeners.");
            if (defeated == "True")
            {
                // No lord/runtime managers required: a completed cluster must not
                // enter OnDefeat through either of the native lifecycle paths.
                job.LordJobTick(); job.Notify_LordDestroyed();
            }
        }
    }

    private static void Completion(Map map, bool destroying, bool expected)
    {
        foreach (bool sleeping in new[] { false, true })
        {
            effects.Clear(); signals.SignalManagerUpdate();
            LordJob_MechanoidDefendBase job = sleeping
                ? new LordJob_SleepThenMechanoidsDefend(new(), null!, 10, IntVec3.Zero, false, true)
                : new LordJob_MechanoidsDefend(new(), null!, 10, IntVec3.Zero, false, true);
            var manager = New<LordManager>(); manager.map = map;
            job.lord = new Lord { lordManager = manager, questTags = new() { "Quest42.MechCluster" } };
            job.AddThingToNotifyOnDefeat(new DefeatListener());
            if (destroying) job.Notify_LordDestroyed(); else job.LordJobTick();
            Check((bool)Defeated.GetValue(job)!, "Completion did not latch; ticks can repeat the same event.");
            var expectedEffects = new[] { "Notify_MechClusterDefeated", "DefeatListener", "Notify_PlayerRaidedSomeone", "TryGainMemory",
                "Quest42.MechCluster.AllEnemiesDefeated", "Message", "PlayOneShotOnCamera" };
            Check(effects.SequenceEqual(expected ? expectedEffects : Array.Empty<string>()),
                "Unexpected mech cluster effects on " + (RemoteSessionPocketMapUtility.FindSessionParent(map)?.Mode ?? "local") + ": " + string.Join(",", effects));
            int count = effects.Count; job.LordJobTick(); job.Notify_LordDestroyed();
            Check(effects.Count == count, "Completion effects repeated on a later tick or lord removal.");
        }
    }

    private sealed class DefeatListener : Thing { public override void Notify_LordDestroyed() => effects.Add("DefeatListener"); }
    private sealed class Receiver : ISignalReceiver { public void Notify_SignalReceived(Signal signal) => effects.Add(signal.tag); }
    private static bool Effect(MethodBase __originalMethod) { effects.Add(__originalMethod.Name); return false; }
    private static bool Translate(string key, ref TaggedString __result) { __result = key; return false; }
    private static bool Home(ref bool __result) { __result = false; return false; }
    private static bool Colonists(ref List<Pawn> __result) { __result = colonists; return false; }
    private static bool Signals(ref SignalManager __result) { __result = signals; return false; }
    private static bool WorldPawns(ref WorldPawns __result) { __result = New<WorldPawns>(); return false; }
    private static bool References(ref IEnumerable<Pawn> __result) { __result = colonists; return false; }
    private static bool Skip() => false;
    private static Map CreateMap(MapParent parent) { var map = New<Map>(); map.info = new MapInfo { parent = parent, Size = new IntVec3(10, 1, 10) }; map.mapPawns = new MapPawns(map); return map; }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
