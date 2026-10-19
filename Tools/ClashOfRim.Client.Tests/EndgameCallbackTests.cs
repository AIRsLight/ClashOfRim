using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Achievements;
using HarmonyLib;
using RimWorld;
using RimWorld.Utility;
using Verse;

internal static class EndgameCallbackTests
{
    private static readonly List<string> continued = new();
    private static readonly Pawn pawn = (Pawn)RuntimeHelpers.GetUninitializedObject(typeof(Pawn));
    private static readonly Building ship = (Building)RuntimeHelpers.GetUninitializedObject(typeof(Building));
    private static readonly Building_ArchonexusCore archonexus = (Building_ArchonexusCore)RuntimeHelpers.GetUninitializedObject(typeof(Building_ArchonexusCore));
    private static readonly CompCerebrexCore core = (CompCerebrexCore)RuntimeHelpers.GetUninitializedObject(typeof(CompCerebrexCore));
    private static readonly string[] routes = { "ship-building", "ship-string", "archonexus", "void-embrace", "void-disrupt", "cerebrex-True", "cerebrex-False" };
    internal static void Run() => RunAsync().WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var harmony = new Harmony(nameof(EndgameCallbackTests));
        var previous = (ClientEndgameAchievements?)AccessTools.Field(typeof(EndgameAchievementRuntime), "achievements").GetValue(null);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            // Native ending effects are replaced; actual patches, confirmation,
            // ownership, candidate queue and snapshot submission remain real.
            foreach (var method in new[]
            {
                AccessTools.Method(typeof(ShipCountdown), nameof(ShipCountdown.InitiateCountdown), new[] { typeof(Building) }),
                AccessTools.Method(typeof(ShipCountdown), nameof(ShipCountdown.InitiateCountdown), new[] { typeof(string) }),
                AccessTools.Method(typeof(ArchonexusCountdown), nameof(ArchonexusCountdown.InitiateCountdown)),
                AccessTools.Method(typeof(VoidAwakeningUtility), nameof(VoidAwakeningUtility.EmbraceTheVoid)),
                AccessTools.Method(typeof(VoidAwakeningUtility), nameof(VoidAwakeningUtility.DisruptTheLink)),
                AccessTools.Method(typeof(CompCerebrexCore), "StartCoreDeactivation")
            }) harmony.Patch(method, prefix: new HarmonyMethod(typeof(EndgameCallbackTests), nameof(Continue)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(GenTicks), nameof(GenTicks.TicksGame)), prefix: new HarmonyMethod(typeof(EndgameCallbackTests), nameof(Ticks)));
            foreach (string language in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                foreach (string route in routes) Decisions(route);
                await Awards();
            }
            foreach (string change in new[] { "user", "auth", "server", "colony", "login", "generation", "game", "rebind", "unbind", "snapshot" })
            foreach (string route in routes) OldDialog(route, change);
            EndgameAchievementRuntime.Attach(null);
            foreach (string route in routes) Check(Enter(route), "Unbound ending was blocked: " + route);
            EndgameAchievementRuntime.Confirm(ClientEndgameKind.Ship, "ignored");
        }
        finally { harmony.UnpatchAll(harmony.Id); EndgameAchievementRuntime.Attach(previous); CultureInfo.CurrentCulture = culture; continued.Clear(); ClientSessionWork.Reset(); }
    }
    private static void Decisions(string route)
    {
        using var f = new ClientEndgameAchievementTests.Fixture(); EndgameAchievementRuntime.Attach(f.Module); continued.Clear();
        Check(!Enter(route) && f.Dialogs.Count == 1 && continued.Count == 0, "Ending did not defer for approval: " + route);
        f.Dialogs[0](); f.Dialogs[0]();
        Check(continued.SequenceEqual(new[] { route }) && f.Dialogs.Count == 1, "Continuation duplicated, changed route or reopened approval: " + route);
        Check(!Enter(route) && f.Dialogs.Count == 2, "Approval escaped its original continuation: " + route);
    }
    private static void OldDialog(string route, string change)
    {
        using var f = new ClientEndgameAchievementTests.Fixture(); EndgameAchievementRuntime.Attach(f.Module); continued.Clear();
        Check(!Enter(route), "Active ending did not defer.");
        using var replacement = change == "rebind" ? new ClientEndgameAchievementTests.Fixture(reset: false) : null;
        if (replacement is not null) EndgameAchievementRuntime.Attach(replacement.Module);
        else if (change == "unbind") EndgameAchievementRuntime.Attach(null);
        else if (change == "snapshot") f.Ports.Snapshot = "advanced";
        else f.Change(change);
        f.Dialogs[0]();
        Check(continued.Count == (change == "snapshot" ? 1 : 0) && (replacement?.Dialogs.Count ?? 0) == 0,
            "Old ending continuation crossed " + change + ": " + route);
    }
    private static async Task Awards()
    {
        using var f = new ClientEndgameAchievementTests.Fixture(); EndgameAchievementRuntime.Attach(f.Module);
        f.Sync.TryBeginManual(out var held);
        ClashOfRimShipCountdownEndedPatch.Postfix(false, "ship:123:Ship17");
        ClashOfRimArchonexusEndGamePatch.Postfix(false); ClashOfRimEmbraceTheVoidPatch.Postfix(false);
        ClashOfRimDisruptTheLinkPatch.Postfix(false); ClashOfRimCerebrexDeactivateCorePatch.Postfix(true, false);
        EndgameAchievementRuntime.Confirm(ClientEndgameKind.Ship, " ");
        Check(f.Pending.Count == 0, "Skipped original or blank event created an award.");
        ClashOfRimShipCountdownEndedPatch.Postfix(true, "ship:123:Ship17");
        ClashOfRimArchonexusEndGamePatch.Postfix(true); ClashOfRimEmbraceTheVoidPatch.Postfix(true);
        ClashOfRimDisruptTheLinkPatch.Postfix(true); ClashOfRimCerebrexDeactivateCorePatch.Postfix(true, true);
        ClashOfRimCerebrexDeactivateCorePatch.Postfix(false, true);
        var events = f.Pending.Snapshot().Select(a => (a.AchievementId, a.EventKey)).ToArray();
        Check(events.SequenceEqual(new[]
        {
            ("endgame_ship_launch", "ship:123:Ship17:launch"), ("endgame_archonexus", "archonexus:123"),
            ("endgame_void_embrace", "void-embrace:123"), ("endgame_void_disrupt", "void-disrupt:123"),
            ("endgame_cerebrex_scavenged", "cerebrex:scavenged:123"), ("endgame_cerebrex_destroyed", "cerebrex:destroyed:123")
        }) && f.Uploads == 0, "Native ending routes lost awards while snapshot synchronization was busy.");
        held!.Dispose();
        ClashOfRimCerebrexDeactivateCorePatch.Postfix(false, true);
        await f.Ports.PumpUntil(f.Done.Task);
        Check(f.Uploads == 1 && f.Confirmed == 1 && f.Pending.Count == 0 && f.Sent.Count == 6
            && f.WireOperation == "EndgameAchievement" && f.WireKind == ModSnapshotUploadKinds.EndgameAchievement,
            "Adapter bypassed queued deduplication or the existing endgame snapshot workflow.");
    }
    private static bool Enter(string route) => route switch
    {
        "ship-building" => ClashOfRimShipCountdownBuildingPatch.Prefix(ship),
        "ship-string" => ClashOfRimShipCountdownStringPatch.Prefix("launch 文化İ"),
        "archonexus" => ClashOfRimArchonexusCountdownPatch.Prefix(archonexus),
        "void-embrace" => ClashOfRimEmbraceTheVoidPatch.Prefix(pawn),
        "void-disrupt" => ClashOfRimDisruptTheLinkPatch.Prefix(pawn),
        _ => ClashOfRimCerebrexStartCoreDeactivationPatch.Prefix(core, route == "cerebrex-True")
    };
    private static bool Continue(MethodBase __originalMethod, object? __instance, object[] __args)
    {
        string route;
        if (__originalMethod.DeclaringType == typeof(ShipCountdown))
        {
            route = __args[0] is string ? "ship-string" : "ship-building";
            Check(route == "ship-string" ? (string)__args[0] == "launch 文化İ" : ReferenceEquals(__args[0], ship), "Ship continuation changed its target.");
        }
        else if (__originalMethod.DeclaringType == typeof(ArchonexusCountdown))
        { route = "archonexus"; Check(ReferenceEquals(__args[0], archonexus), "Archonexus continuation changed its target."); }
        else if (__originalMethod.DeclaringType == typeof(CompCerebrexCore))
        { route = "cerebrex-" + (bool)__args[0]; Check(ReferenceEquals(__instance, core), "Cerebrex continuation changed its target."); }
        else
        { route = __originalMethod.Name == nameof(VoidAwakeningUtility.EmbraceTheVoid) ? "void-embrace" : "void-disrupt"; Check(ReferenceEquals(__args[0], pawn), "Void continuation changed its target."); }
        if (Enter(route)) continued.Add(route);
        return false;
    }
    private static bool Ticks(ref int __result) { __result = 123; return false; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
