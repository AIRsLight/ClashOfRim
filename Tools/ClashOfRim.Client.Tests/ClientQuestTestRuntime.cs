using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim.Quests;
using HarmonyLib;
using RimWorld;
using Verse;

// Shared native quest boundary for lifecycle tests; domain transitions, engine
// part fields and membership checks are real. End() records rather than renders.
internal sealed class ClientQuestTestRuntime : IDisposable
{
    private static ClientQuestTestRuntime? current;
    private readonly ClientQuestTestRuntime? previous = current;
    private readonly Game? oldGame = Current.Game;
    private readonly Harmony harmony;
    internal QuestManager Manager = new();
    internal long Ticks;
    internal readonly List<(QuestPart? Part, QuestEndOutcome Outcome, bool Letter, bool Sound)> Endings = new();
    internal Action<QuestPart?>? Ending;
    internal ClientQuestTestRuntime(string id)
    {
        current = this; harmony = new(id + ".Quests");
        void Prefix(System.Reflection.MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientQuestTestRuntime), prefix));
        Prefix(AccessTools.PropertyGetter(typeof(Find), nameof(Find.QuestManager)), nameof(GetManager));
        Prefix(AccessTools.PropertyGetter(typeof(ClashManagedQuestTimingUtility), nameof(ClashManagedQuestTimingUtility.CurrentGameTicks)), nameof(GetTicks));
        Prefix(AccessTools.Method(typeof(ClashManagedQuestUtility), nameof(ClashManagedQuestUtility.EndManagedQuest)), nameof(End));
    }
    internal Game NewGame()
    {
        Manager = new(); Endings.Clear(); Ending = null; Ticks = 0;
        return Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
    }
    internal Quest Add(QuestPart part)
    { var quest = new Quest(); quest.AddPart(part); Manager.QuestsListForReading.Add(quest); return quest; }
    public void Dispose() { harmony.UnpatchAll(harmony.Id); Current.Game = oldGame; current = previous; }
    private static bool GetManager(ref QuestManager __result) { __result = current!.Manager; return false; }
    private static bool GetTicks(ref long __result) { __result = current!.Ticks; return false; }
    private static bool End(QuestPart? part, QuestEndOutcome outcome, bool sendLetter, bool playSound)
    { current!.Endings.Add((part, outcome, sendLetter, playSound)); current.Ending?.Invoke(part); return false; }
}
