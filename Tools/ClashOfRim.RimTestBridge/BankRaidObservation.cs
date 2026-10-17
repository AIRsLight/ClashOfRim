using AIRsLight.ClashOfRim.Bank;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ClashOfRim.RimTestBridge;

// Passive diagnostics: observe the original raid worker without replacing its outcome or RNG.
internal static class BankRaidObservation
{
    private static bool installed;
    private static IncidentParms? tracked;
    private static bool? entryResolved;
    private static readonly List<object> attempts = new();
    internal static object[] Copy() => attempts.ToArray();

    internal static void Install()
    {
        if (installed) return;
        var harmony = new Harmony("ClashOfRim.RimTest.BankRaidObservation");
        harmony.Patch(AccessTools.Method(typeof(IncidentWorker_Raid), nameof(IncidentWorker_Raid.TryGenerateRaidInfo)),
            prefix: new HarmonyMethod(typeof(BankRaidObservation), nameof(Before)),
            postfix: new HarmonyMethod(typeof(BankRaidObservation), nameof(After)));
        harmony.Patch(AccessTools.Method(typeof(PawnsArrivalModeWorker_EdgeWalkIn), nameof(PawnsArrivalModeWorker_EdgeWalkIn.TryResolveRaidSpawnCenter)),
            postfix: new HarmonyMethod(typeof(BankRaidObservation), nameof(EntryResolved)));
        installed = true;
    }

    private static void Before(IncidentParms parms)
    {
        if (parms.quest?.PartsListForReading.Any(p => p is QuestPart_ClashBankLoan or QuestPart_ClashBankDebt) != true) return;
        tracked = parms;
        entryResolved = null;
    }

    private static void EntryResolved(IncidentParms parms, bool __result)
    { if (ReferenceEquals(parms, tracked)) entryResolved = __result; }

    private static void After(IncidentParms parms, List<Pawn>? pawns, bool __result)
    {
        if (!ReferenceEquals(parms, tracked)) return;
        attempts.Add(new { succeeded = __result, entryResolved, parms.points, faction = parms.faction?.def.defName,
            arrival = parms.raidArrivalMode?.defName, pawns = pawns?.Count ?? 0, ticks = Find.TickManager.TicksGame });
        if (attempts.Count > 16) attempts.RemoveAt(0);
        tracked = null;
    }
}
