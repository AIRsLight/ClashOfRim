using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Support;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Support;
using AIRsLight.ClashOfRim.WorldObjects;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

internal static class ClientSupportSourceTests
{
    private static Game game = null!;
    private static Pawn pawn = null!;
    private static readonly List<Pawn> members = new();
    private static readonly List<string> trace = new();
    private static bool destroyed, owner, dead, emptyScribe;
    private static int tile, layer;
    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.SupportSource");
        void Patch(MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientSupportSourceTests), prefix));
        Patch(AccessTools.PropertyGetter(typeof(Current), nameof(Current.Game)), nameof(Game));
        Patch(AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.Dead)), nameof(Dead));
        Patch(AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.LabelShort)), nameof(Label));
        Patch(AccessTools.PropertyGetter(typeof(WorldObject), nameof(WorldObject.Tile)), nameof(Tile));
        Patch(AccessTools.PropertyGetter(typeof(WorldObject), nameof(WorldObject.Destroyed)), nameof(Destroyed));
        Patch(AccessTools.Method(typeof(WorldObject), nameof(WorldObject.GetUniqueLoadID)), nameof(LoadId));
        Patch(AccessTools.PropertyGetter(typeof(Caravan), nameof(Caravan.PawnsListForReading)), nameof(Members));
        Patch(AccessTools.Method(typeof(Caravan), nameof(Caravan.ContainsPawn)), nameof(Contains));
        Patch(AccessTools.Method(typeof(Caravan), nameof(Caravan.IsOwner)), nameof(Owner));
        Patch(AccessTools.Method(typeof(Caravan), nameof(Caravan.RemovePawn)), nameof(Remove));
        Patch(AccessTools.Method(typeof(WorldObject), nameof(WorldObject.Destroy)), nameof(Destroy));
        Patch(AccessTools.Method(typeof(RemoteWorldObjectUiUtility), nameof(RemoteWorldObjectUiUtility.ReadTileLayerId)), nameof(Layer));
        Patch(AccessTools.Method(typeof(PawnExchangeCapture), nameof(PawnExchangeCapture.Capture)), nameof(Capture));
        Patch(AccessTools.Method(typeof(SupportPawnWorldPawnContextUtility), nameof(SupportPawnWorldPawnContextUtility.MarkDepartingTemporarySupportPawn)), nameof(Keep));
        Patch(AccessTools.Method(typeof(ClashOfRimText), nameof(ClashOfRimText.Key), new[] { typeof(string) }), nameof(Text));
        Patch(AccessTools.Method(typeof(ClashOfRimText), nameof(ClashOfRimText.Key), new[] { typeof(string), typeof(NamedArgument[]) }), nameof(Text));
        try
        {
            var context = new ClashOfRimClientNetworkContext("http://server", "user", "colony", "base", null, null, "auth");
            var target = new ClientSupportTarget("target", "colony", "map", "world", 7, 2, "目标", true);
            foreach (bool permanent in new[] { false, true })
            foreach (bool last in new[] { false, true })
            {
                var source = Reset(); if (!last) members.Add(new Pawn());
                var captured = source.Capture(context, target);
                Check(captured.Pawn is { SourceTile: 7, CaravanId: "caravan-original" } && captured.Rejection is null, "Source context was not captured before removal.");
                var command = new ClientSupportCommand("key", target, new(7, permanent), captured.Pawn!);
                Check(source.Remove(command, () => trace.Add("mutation")) is null, "Valid source refused removal.");
                var expected = new List<string> { "capture", "mutation", "remove" };
                if (last) expected.Add("destroy"); if (!permanent) expected.Add("keep");
                Check(trace.SequenceEqual(expected) && destroyed == last && captured.Pawn!.CaravanId == "caravan-original", "Departure ordering or temporary/permanent semantics changed.");
                int before = trace.Count;
                Check(source.Remove(command, () => trace.Add("unexpected")) is not null && trace.Count == before, "Departed pawn was removed twice.");
            }
            foreach (string invalid in new[] { "game", "owner", "missing", "dead", "destroyed", "tile", "layer", "scribe" })
            {
                var source = Reset();
                switch (invalid)
                {
                    case "game": game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game)); break;
                    case "owner": owner = false; break;
                    case "missing": members.Clear(); break;
                    case "dead": dead = true; break;
                    case "destroyed": destroyed = true; break;
                    case "tile": tile = 9; break;
                    case "layer": layer = 5; break;
                    case "scribe": emptyScribe = true; break;
                }
                var captured = source.Capture(context, target);
                Check(captured.Pawn is null && captured.Rejection is not null && !trace.Contains("remove"), "Invalid source captured transferable pawn: " + invalid);
            }
            var changed = Reset(); var original = changed.Capture(context, target); tile = 9;
            Check(changed.Remove(new("key", target, new(7, false), original.Pawn!), () => trace.Add("mutation")) is not null
                && !trace.Contains("mutation"), "Source movement after capture bypassed removal revalidation.");
        }
        catch (Exception ex) { Console.WriteLine(ex.StackTrace); throw; }
        finally { harmony.UnpatchAll(harmony.Id); members.Clear(); trace.Clear(); }
    }
    private static ClientSupportSource Reset()
    {
        game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        pawn = new Pawn { def = new ThingDef { defName = "Human" }, thingIDNumber = 42 };
        members.Clear(); members.Add(pawn); trace.Clear(); owner = true; dead = destroyed = emptyScribe = false; tile = 7; layer = 2;
        return new((Caravan)RuntimeHelpers.GetUninitializedObject(typeof(Caravan)), pawn);
    }
    private static bool Game(ref Game __result) { __result = game; return false; }
    private static bool Dead(ref bool __result) { __result = dead; return false; }
    private static bool Label(ref string __result) { __result = "支援者"; return false; }
    private static bool Tile(ref PlanetTile __result) { Check(!destroyed, "Read tile after destroying the source caravan."); __result = new(tile, layer); return false; }
    private static bool Destroyed(ref bool __result) { __result = destroyed; return false; }
    private static bool LoadId(ref string __result) { Check(!destroyed, "Read ID after destroying the source caravan."); __result = "caravan-original"; return false; }
    private static bool Members(ref List<Pawn> __result) { __result = members; return false; }
    private static bool Contains(Pawn __0, ref bool __result) { __result = members.Any(p => ReferenceEquals(p, __0)); return false; }
    private static bool Owner(ref bool __result) { __result = owner; return false; }
    private static bool Remove(Pawn __0) { Check(ReferenceEquals(__0, pawn), "Removed another pawn."); trace.Add("remove"); members.Remove(__0); return false; }
    private static bool Destroy() { trace.Add("destroy"); destroyed = true; return false; }
    private static bool Layer(ref int __result) { __result = layer; return false; }
    private static bool Capture(Pawn __0, string __1, ref ModPawnExchangePackageDto __result)
    { Check(ReferenceEquals(__0, pawn), "Captured another pawn."); trace.Add("capture"); __result = new() { Reference = new() { GlobalId = __1 }, Scribe = emptyScribe ? null : new() { Xml = "pawn" } }; return false; }
    private static bool Keep(Pawn __0, string __1, out string message, ref bool __result)
    { Check(ReferenceEquals(__0, pawn) && __1 == "owner:user/pawn:Human42", "Preserved another pawn or identity."); trace.Add("keep"); message = ""; __result = true; return false; }
    private static bool Text(string __0, ref string __result) { __result = __0; return false; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
