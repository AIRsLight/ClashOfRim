using System;
using System.Collections.Generic;
using System.Reflection;
using AIRsLight.ClashOfRim;
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
    }
}
