using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Quests;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Mercenaries;

internal sealed class ClientMercenaryQuestCallbacks
{
    internal static ClientMercenaryQuestCallbacks? Current { get; private set; }
    internal static void Attach(ClientMercenaryQuestCallbacks? value) => Current = value;
    private readonly ClientMercenaryQuestLifecycle lifecycle;
    private readonly ConditionalWeakTable<QuestPart_ClashMercenary, Bound> quests = new();
    internal ClientMercenaryQuestCallbacks(ClientMercenaryQuestLifecycle lifecycle) => this.lifecycle = lifecycle;
    internal Task TickAsync(QuestPart_ClashMercenary part) => lifecycle.TickAsync(Bind(part));
    internal Task SignalAsync(QuestPart_ClashMercenary part, string? tag) => lifecycle.SignalAsync(Bind(part), tag);
    internal Task HostileAsync(QuestPart_ClashMercenary part) => lifecycle.HostileAsync(Bind(part));
    internal Task KilledAsync(QuestPart_ClashMercenary part, Pawn pawn) => lifecycle.KilledAsync(Bind(part), pawn);
    internal bool TryStartRecall(QuestPart_ClashMercenary part) => lifecycle.TryStartRecall(Bind(part), out _);
    private Bound Bind(QuestPart_ClashMercenary part)
    {
        if (quests.TryGetValue(part, out var bound)) return bound;
        return quests.GetValue(part, value => new(this, new ClientManagedQuestOwner(value), value));
    }
    private sealed record Bound(ClientMercenaryQuestCallbacks Owner, ClientManagedQuestOwner Source, QuestPart_ClashMercenary Part) : IClientMercenaryQuest
    {
        public bool IsCurrent => ReferenceEquals(Current, Owner) && Source.IsCurrent;
        public string ContractId => Part.ContractId;
        public string QuestTag => Part.QuestTag;
        public object? PawnIdentity => Part.Pawn;
        public object? ShuttleTarget => Part.RecallShuttle;
        public bool HasPawn => Part.Pawn is { Destroyed: false };
        public bool PawnSpawned => Part.Pawn?.Spawned == true;
        public bool PawnLoaded => Part.Pawn is { } pawn && Part.RecallShuttle?.TryGetComp<CompTransporter>()?.innerContainer?.Contains(pawn) == true;
        public bool ShuttleGone => Part.RecallShip?.Disposed == true || Part.RecallShuttle is null || Part.RecallShuttle.Destroyed || !Part.RecallShuttle.Spawned;
        public bool ShuttleDestroyed => Part.RecallShuttle?.Destroyed == true;
        public long ExpiresAt => Part.ExpiresAtGameTicks;
        public long GameTicks => ClashManagedQuestTimingUtility.CurrentGameTicks;
        public bool Completed { get => Part.Completed; set => Part.Completed = value; }
        public bool RecallStarted { get => Part.RecallStarted; set => Part.RecallStarted = value; }
        public bool ShuttleWaiting { get => Part.RecallShuttleWaiting; set => Part.RecallShuttleWaiting = value; }
        public bool ShuttleLoaded { get => Part.RecallShuttleLoaded; set => Part.RecallShuttleLoaded = value; }
        public bool ArrivalSent { get => Part.ArrivalLetterSent; set => Part.ArrivalLetterSent = value; }
        public bool HostileReported { get => Part.HostileBehaviorReported; set => Part.HostileBehaviorReported = value; }
        public bool DeathReported { get => Part.DeathReported; set => Part.DeathReported = value; }
        public bool ShuttleDestroyedReported { get => Part.RecallShuttleDestroyedReported; set => Part.RecallShuttleDestroyedReported = value; }
        public int ReportedOvertimeDays { get => Part.ReportedOvertimeFineDays; set => Part.ReportedOvertimeFineDays = value; }
        public ClientMercenaryRecallResult Recall(Func<bool> current)
        {
            bool recalled = ClashMercenaryQuestUtility.TryRecallMercenary(Part.Pawn!, Part.QuestTag, out bool waiting, out var shuttle, out var ship);
            if (current()) { Part.RecallShuttle = shuttle; Part.RecallShip = ship; }
            return new(recalled, waiting);
        }
        public void ClearShuttle() { Part.RecallShuttle = null; Part.RecallShip = null; }
        public void ReleasePawn() => ClashMercenaryQuestUtility.ReleaseMercenaryAfterRecallShuttleDestroyed(Part.Pawn);
        public void End(bool success, bool sendLetter, bool playSound) => ClashManagedQuestUtility.EndManagedQuest(Part,
            success ? QuestEndOutcome.Success : QuestEndOutcome.Fail, sendLetter, playSound);
        public void Notify(ClientMercenaryQuestNotice notice, object? target, Func<bool> current)
        {
            string prefix = "ClashOfRim.Mercenary." + notice + "Letter";
            string label = ClashOfRimText.Key(prefix + "Label");
            if (!current()) return;
            string name = notice == ClientMercenaryQuestNotice.Arrival ? Part.Pawn!.LabelShort : Part.PawnLabel;
            string text = notice switch
            {
                ClientMercenaryQuestNotice.HostileBehavior => ClashOfRimText.Key(prefix + "Text", name.Named("PAWN"), Part.HarmfulSurgeryFineSilver.Named("FINE")),
                ClientMercenaryQuestNotice.ShuttleDestroyed => ClashOfRimText.Key(prefix + "Text", name.Named("PAWN"), 6000.Named("FINE")),
                ClientMercenaryQuestNotice.Death => ClashOfRimText.Key(prefix + "Text", name.Named("PAWN"), Part.DeathFineSilver.Named("FINE")),
                _ => ClashOfRimText.Key(prefix + "Text", name.Named("PAWN"))
            };
            if (!current()) return;
            var kind = notice == ClientMercenaryQuestNotice.Arrival ? LetterDefOf.PositiveEvent
                : notice == ClientMercenaryQuestNotice.Recall ? LetterDefOf.NeutralEvent : LetterDefOf.NegativeEvent;
            Find.LetterStack.ReceiveLetter(label, text, kind, target as Thing);
        }
    }
}
