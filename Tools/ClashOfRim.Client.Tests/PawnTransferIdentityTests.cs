using System;
using System.Collections.Generic;
using System.Reflection;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Pawns;
using RimWorld.Planet;
using Verse;

internal static class PawnTransferIdentityTests
{
    public static void Run()
    {
        var type = typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.Pawns.PawnGlobalIdUtility", true)!;
        var method = type.GetMethod("Build", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(Pawn) }, null)!;
        var pawn = new Pawn { def = new ThingDef { defName = "Human" }, thingIDNumber = 42 };
        string Build(string owner) => (string)method.Invoke(null, new object[] { owner, pawn })!;
        if (Build("alice") != "owner:alice/pawn:Human42") throw new Exception("Unmarked local pawn identity changed.");
        pawn.questTags = new List<string> { "quest.real", "ClashOfRim.PawnGlobalId:owner:alice/pawn:Human7" };
        foreach (string owner in new[] { "bob", "alice", "third-colony" })
            if (Build(owner) != "owner:alice/pawn:Human7") throw new Exception("Repeated transfer re-keyed a globally identified pawn.");
        pawn.questTags = new List<string> { "ClashOfRim.PawnGlobalId: " };
        if (Build("alice") != "owner:alice/pawn:Human42") throw new Exception("Empty tag replaced local identity.");

        VerifyRelationshipDeath();
        PawnRelationshipSpeciesTests.Run();
    }

    private static void VerifyRelationshipDeath()
    {
        foreach (string pool in new[] { "pawnsAlive", "pawnsMothballed" })
        {
            var pawn = new Pawn
            {
                def = new ThingDef { defName = "Human", race = new RaceProperties() },
                thingIDNumber = 42,
                questTags = new List<string> { "ClashOfRim.PawnGlobalId:owner:alice/pawn:Human7" }
            };
            pawn.health = new Pawn_HealthTracker(pawn);
            var world = new WorldPawns();
            // Isolated engine fixture: seed either active or mothballed world storage without a Game.
            ((HashSet<Pawn>)typeof(WorldPawns).GetField(pool, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!).Add(pawn);
            world.ForcefullyKeptPawns.Add(pawn);
            void Restore(bool dead) => PawnExchangeScribeRestorer.RestoreRelationshipDeath(pawn, dead, world);
            Restore(true);
            if (pawn.Dead || !world.AllPawnsAlive.Contains(pawn)) throw new Exception("Relationship metadata killed a real local pawn.");
            pawn.questTags.Add("ClashOfRim.RelationshipPlaceholder:owner:alice/pawn:Human7");
            Restore(false);
            if (pawn.Dead) throw new Exception("Living relationship placeholder became dead.");
            Restore(true);
            if (!pawn.Dead || world.AllPawnsAlive.Contains(pawn) || !world.AllPawnsDead.Contains(pawn))
                throw new Exception("Deceased relationship placeholder lost its death state or remained in the living world pool.");
            Restore(true); Restore(false);
            if (!pawn.Dead || pawn.Corpse is not null || world.AllPawnsDead.Count != 1 || !world.ForcefullyKeptPawns.Contains(pawn))
                throw new Exception("Repeated relationship metadata resurrected a placeholder, created a corpse or lost world retention.");
        }
    }
}
