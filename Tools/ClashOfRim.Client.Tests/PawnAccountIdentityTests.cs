using System;
using System.Collections.Generic;
using System.Globalization;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Pawns;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

internal static class PawnAccountIdentityTests
{
    private static WorldPawns world = null!;
    internal static void Run()
    {
        var harmony = new Harmony(nameof(PawnAccountIdentityTests));
        var culture = CultureInfo.CurrentCulture;
        var previous = (ClientSessionLifecycle?)AccessTools.Field(typeof(PawnExchangeScribeRestorer), "session").GetValue(null);
        void Patch(Type type, string property, string prefix) => harmony.Patch(AccessTools.PropertyGetter(type, property), prefix: new HarmonyMethod(typeof(PawnAccountIdentityTests), prefix));
        try
        {
            Patch(typeof(Find), nameof(Find.Maps), nameof(Maps));
            Patch(typeof(Find), nameof(Find.WorldObjects), nameof(Objects));
            Patch(typeof(Find), nameof(Find.WorldPawns), nameof(Pawns));
            var resolve = (Func<string, Pawn?>)AccessTools.Method(typeof(PawnExchangeScribeRestorer), "FindExistingPawnByGlobalId").CreateDelegate(typeof(Func<string, Pawn?>));
            foreach (string language in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language); ClientSessionWork.Reset();
                world = new WorldPawns();
                var local = Pawn(42); var tagged = Pawn(43); var placeholder = Pawn(44);
                tagged.questTags = new() { "ClashOfRim.PawnGlobalId:owner:foreign/pawn:Human42" };
                placeholder.questTags = new() { "ClashOfRim.RelationshipPlaceholder:owner:original/pawn:Human44" };
                ((HashSet<Pawn>)AccessTools.Field(typeof(WorldPawns), "pawnsAlive").GetValue(world)!).UnionWith(new[] { local, tagged, placeholder });
                var ports = new EventModuleTestPorts();
                var session = new ClientSessionLifecycle(ports, ports, new(ports));
                PawnExchangeScribeRestorer.Attach(null);
                Check(resolve("owner:alice/pawn:Human42") is null && ReferenceEquals(resolve("owner:foreign/pawn:Human42"), tagged)
                    && ReferenceEquals(resolve("owner:original/pawn:Human44"), placeholder), "Unbound account adopted local IDs or lost explicit identities.");
                PawnExchangeScribeRestorer.Attach(session);
                foreach (string id in new[] { "owner:alice/pawn:Human42", "owner:alice/pawn:Thing_Human42", " owner:alice/pawn:Human42 " })
                    Check(ReferenceEquals(resolve(id), local), "Own returning pawn was not resolved: " + id);
                foreach (string id in new[] { "", "Human42", "owner:unknown/pawn:Human42", "owner:bob/pawn:Human42", "owner:ALICE/pawn:Human42", "owner:alice/pawn:", "owner:alice/pawn:Human999" })
                    Check(resolve(id) is null, "Unowned or invalid ID aliased a local pawn: " + id);
                Check(ReferenceEquals(resolve("owner:foreign/pawn:Human42"), tagged), "Local ThingID took precedence over a foreign global tag.");
                ports.User = "bob";
                Check(resolve("owner:alice/pawn:Human42") is null && ReferenceEquals(resolve("owner:bob/pawn:Human42"), local), "Account edit retained the previous owner.");
                session.PublishLogin(new() { AuthenticatedUserId = "玩家İ/a", AuthToken = "new", SessionId = "new" });
                Check(ReferenceEquals(resolve("owner:玩家İ_a/pawn:Human42"), local) && resolve("owner:bob/pawn:Human42") is null, "Login identity was not normalized through the shared global-ID rule.");
                ports.User = ""; Check(resolve("owner:unknown/pawn:Human42") is null, "Empty account made unknown IDs local.");
                var replacement = new EventModuleTestPorts { User = "replacement" };
                PawnExchangeScribeRestorer.Attach(new(replacement, replacement, new(replacement)));
                Check(ReferenceEquals(resolve("owner:replacement/pawn:Human42"), local), "Rebinding retained the previous session.");
            }
        }
        finally { harmony.UnpatchAll(harmony.Id); PawnExchangeScribeRestorer.Attach(previous); CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); world = null!; }
    }
    private static Pawn Pawn(int id) => new() { def = new ThingDef { defName = "Human", race = new RaceProperties() }, thingIDNumber = id };
    private static bool Maps(ref List<Map> __result) { __result = new(); return false; }
    private static bool Objects(ref WorldObjectsHolder? __result) { __result = null; return false; }
    private static bool Pawns(ref WorldPawns __result) { __result = world; return false; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
