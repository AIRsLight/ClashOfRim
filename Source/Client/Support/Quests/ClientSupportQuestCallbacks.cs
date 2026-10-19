using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Support;
using AIRsLight.ClashOfRim.Quests;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Support;

internal sealed class ClientSupportQuestCallbacks
{
    internal static ClientSupportQuestCallbacks? Current { get; private set; }
    internal static void Attach(ClientSupportQuestCallbacks? value) => Current = value;
    private readonly ClientSupportQuestLifecycle lifecycle;
    private readonly ConditionalWeakTable<QuestPart_ClashSupportPawn, Bound> quests = new();
    internal ClientSupportQuestCallbacks(ClientSupportQuestLifecycle lifecycle) => this.lifecycle = lifecycle;
    internal Task TickAsync(QuestPart_ClashSupportPawn part) => lifecycle.TickAsync(Bind(part));
    internal bool TryStartEarlyReturn(QuestPart_ClashSupportPawn part) => lifecycle.TryStartEarlyReturn(Bind(part));
    private Bound Bind(QuestPart_ClashSupportPawn part)
    {
        if (quests.TryGetValue(part, out var bound)) return bound;
        return quests.GetValue(part, value => new(this, Verse.Current.Game, value));
    }

    private sealed record Bound(ClientSupportQuestCallbacks Owner, Game? Game, QuestPart_ClashSupportPawn Part) : IClientSupportQuest
    {
        public bool IsCurrent => ReferenceEquals(Current, Owner) && Game is not null && ReferenceEquals(Game, Verse.Current.Game)
            && Part.quest is { } quest && quest.PartsListForReading.Contains(Part) && Find.QuestManager?.QuestsListForReading.Contains(quest) == true;
        public string EventId => Part.EventId;
        public bool Completed => Part.Completed;
        public bool Permanent => Part.PermanentSupport;
        public bool HasAssignment => Game?.GetComponent<ClashOfRimGameComponent>()?.HasSupportAssignment(Part.EventId) == true;
        public bool Expired => ClashManagedQuestTimingUtility.IsExpired(Part.ExpiresAtGameTicks);
        public bool FinishRequested => Part.FinishRequested;
        public string FinishReason => Part.FinishReason;
        public void Complete()
        {
            Part.Completed = true;
            ClashManagedQuestUtility.EndManagedQuest(Part, QuestEndOutcome.Success, sendLetter: false, playSound: false);
        }
        public void MarkDeparting(string reason) { Part.FinishRequested = true; Part.FinishReason = reason; }
    }
}
