using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Quests;

// Cached per part by the domain adapter; save/load still owns the engine fields.
internal sealed class ClientManagedQuestOwner
{
    private readonly QuestPart part;
    internal Game? Game { get; } = Current.Game;
    internal ClientManagedQuestOwner(QuestPart part) => this.part = part;
    internal bool IsCurrent => Game is not null && ReferenceEquals(Game, Current.Game)
        && part.quest is { } quest && quest.PartsListForReading.Contains(part)
        && Find.QuestManager?.QuestsListForReading.Contains(quest) == true;
}
