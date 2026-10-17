using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Bank;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using HarmonyLib;
using Verse;

internal static class ClientMercenaryRuntimeTests
{
    private static readonly List<Map> maps = new();
    private static readonly List<string> trace = new();
    private static Map home = null!, displayed = null!;
    private static Pawn pawn = null!;
    private static string fault = "";
    private static int consumed, generated, quests, arrivals;
    private static Map? destination;
    private static ModMercenaryContractDto? contract;
    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.MercenaryRuntime");
        void Patch(MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientMercenaryRuntimeTests), prefix));
        Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.Maps)), nameof(Maps));
        Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.CurrentMap)), nameof(CurrentMap));
        Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.TickManager)), nameof(Ticks));
        Patch(AccessTools.PropertyGetter(typeof(Map), nameof(Map.IsPlayerHome)), nameof(IsHome));
        Patch(AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.Dead)), nameof(IsDead));
        Patch(AccessTools.Method(typeof(ClientBankRuntime), nameof(ClientBankRuntime.CountPlayerHomeSilver)), nameof(Silver));
        Patch(AccessTools.Method(typeof(ClientBankRuntime), nameof(ClientBankRuntime.TryConsumePlayerHomeSilver)), nameof(Consume));
        Patch(AccessTools.Method(typeof(MercenarySkillUtility), nameof(MercenarySkillUtility.GenerateMercenaryPawn)), nameof(Generate));
        Patch(AccessTools.Method(typeof(ClashMercenaryQuestUtility), nameof(ClashMercenaryQuestUtility.CreateMercenaryQuest)), nameof(Quest));
        Patch(AccessTools.Method(typeof(ClashMercenaryQuestUtility), nameof(ClashMercenaryQuestUtility.TrySendArrivalShuttleToMap)), nameof(Arrive));
        Patch(AccessTools.Method(typeof(ClashOfRimText), nameof(ClashOfRimText.Key), new[] { typeof(string) }), nameof(Text));
        Patch(AccessTools.Method(typeof(ClashOfRimText), nameof(ClashOfRimText.Key), new[] { typeof(string), typeof(NamedArgument[]) }), nameof(Text));
        try
        {
            foreach (bool guard in new[] { false, true })
            foreach (string failure in new[] { "", "consume", "generate", "quest", "arrival" })
            {
                if (guard && failure is "generate" or "quest" or "arrival") continue;
                Reset(); fault = failure;
                var source = new ClientMercenaryRuntime().Capture();
                Require(source.Validate(50) is null, "Valid home/balance was rejected.");
                ClientMercenaryLocalResult? result = null;
                try { result = source.Prepare(Command(guard), () => trace.Add("mutation")); }
                catch (InvalidOperationException) when (failure is "generate" or "quest" or "arrival") { }
                Require(trace[0] == "mutation" && trace[1] == "consume" && consumed == 50, "Mutation marker followed debit.");
                if (failure == "consume") Require(result?.State == ClientMercenaryPreparation.Failed && generated == 0 && quests == 0, "Partial debit continued hiring.");
                if (failure == "")
                {
                    Require(result?.State == ClientMercenaryPreparation.Prepared && generated == (guard ? 0 : 1) && quests == generated && arrivals == generated, "Wrong normal/guard local effects.");
                    if (!guard) Require(ReferenceEquals(destination, home) && contract!.ContractId == "contract" && contract.ExpiresAtGameTicks == 420300, "Arrival changed its captured map or contract.");
                }
                if (failure == "arrival")
                    Require(source.ResumeArrival().State == ClientMercenaryPreparation.Failed && arrivals == 1, "Uncertain ship creation was repeated.");
            }
            Reset(); fault = "map-removed-in-quest";
            var pending = new ClientMercenaryRuntime().Capture();
            var waiting = pending.Prepare(Command(false), () => trace.Add("mutation"));
            Require(waiting.State == ClientMercenaryPreparation.AwaitingArrival && arrivals == 0 && pending.Validate(0) is not null, "Unloaded original map silently switched to displayed map.");
            maps.Add(home); fault = "";
            Require(pending.ResumeArrival().State == ClientMercenaryPreparation.Prepared && consumed == 50 && generated == 1 && quests == 1 && arrivals == 1 && ReferenceEquals(destination, home), "Arrival continuation recreated pawn/debit or changed destination.");
            Require(pending.ResumeArrival().State == ClientMercenaryPreparation.Failed && arrivals == 1, "Completed arrival ran twice.");
            Reset(); fault = "map-removed-in-quest";
            pending = new ClientMercenaryRuntime().Capture(); pending.Prepare(Command(false), () => trace.Add("mutation"));
            maps.Add(home); fault = "dead";
            Require(pending.ResumeArrival().State == ClientMercenaryPreparation.Failed && arrivals == 0, "Dead pawn was put into arrival shuttle.");
            Reset(); var captured = new ClientMercenaryRuntime().Capture(); maps.Remove(home);
            Require(captured.Validate(0) is not null, "Source accepted an unloaded home.");
            Reset(); fault = "no-silver";
            Require(new ClientMercenaryRuntime().Capture().Validate(50)!.Contains("StatusInsufficientSilver"), "Hire skipped balance revalidation.");
        }
        finally { harmony.UnpatchAll(harmony.Id); maps.Clear(); trace.Clear(); }
    }
    private static void Reset()
    {
        home = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map)); displayed = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
        pawn = (Pawn)RuntimeHelpers.GetUninitializedObject(typeof(Pawn));
        maps.Clear(); maps.Add(home); maps.Add(displayed); trace.Clear(); consumed = generated = quests = arrivals = 0; fault = ""; destination = null; contract = null;
    }
    private static ClientMercenaryCommand Command(bool guard) => new("key", "contract", new("Shooting", 12, 7, guard ? "Elite" : null), new(50, 20, 100, "Elite", 1.5f), 300, 420300);
    private static bool Maps(ref List<Map> __result) { __result = maps; return false; }
    private static bool CurrentMap(ref Map __result) { __result = displayed; return false; }
    private static bool Ticks(ref TickManager __result) { __result = null!; return false; }
    private static bool IsHome(Map __instance, ref bool __result) { __result = ReferenceEquals(__instance, home); return false; }
    private static bool IsDead(ref bool __result) { __result = fault == "dead"; return false; }
    private static bool Silver(ref int __result) { __result = fault == "no-silver" ? 0 : 100; return false; }
    private static bool Consume(int amount, out string message, ref bool __result)
    { consumed += amount; trace.Add("consume"); message = "consume failed"; __result = fault != "consume"; return false; }
    private static bool Generate(ref Pawn __result)
    { generated++; if (fault == "generate") throw new InvalidOperationException("generate"); __result = pawn; return false; }
    private static bool Quest(ModMercenaryContractDto __0, Pawn __1)
    {
        quests++; contract = __0; Require(ReferenceEquals(__1, pawn), "Quest changed pawn.");
        if (fault == "quest") throw new InvalidOperationException("quest");
        if (fault == "map-removed-in-quest") maps.Remove(home);
        return false;
    }
    private static bool Arrive(Pawn __0, Map? map, out string message, ref bool __result)
    {
        arrivals++; destination = map; Require(ReferenceEquals(__0, pawn), "Arrival changed original pawn.");
        if (fault == "arrival") throw new InvalidOperationException("arrival");
        message = ""; __result = true; return false;
    }
    private static bool Text(string __0, ref string __result) { __result = __0; return false; }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
