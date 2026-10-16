using System;
using System.Reflection;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.RemoteMaps;
using RimWorld;
using Verse;

internal static class AddictionLoadStateTests
{
    public static void Run()
    {
        var prefix = typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.RemoteMaps.RemoteMapProjectionAddictionLoadPatch", true)!
            .GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)!;
        var pawn = new Pawn(); var addiction = new Hediff_Addiction { pawn = pawn };
        bool Original() => (bool)prefix.Invoke(null, new object?[] { addiction, null })!;
        var originalMode = Scribe.mode;
        try
        {
            Scribe.mode = LoadSaveMode.Inactive;
            if (!Original()) throw new Exception("Normal runtime failures were hidden.");
            using (RemoteMapProjectionLoadScope.Begin())
                if (Original()) throw new Exception("Projection load lost its missing-needs guard.");
            Scribe.mode = LoadSaveMode.LoadingVars;
            if (Original()) throw new Exception("Ordinary battle reload still dereferences missing needs.");
            pawn.needs = (Pawn_NeedsTracker)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Pawn_NeedsTracker));
            if (!Original()) throw new Exception("A restored tracker bypassed original addiction behavior.");
        }
        finally { Scribe.mode = originalMode; }
    }
}
