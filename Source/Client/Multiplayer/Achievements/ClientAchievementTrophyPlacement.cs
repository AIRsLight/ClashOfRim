using AIRsLight.ClashOfRim.Achievements;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Achievements;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer.Achievements;

internal sealed class ClientAchievementTrophyPlacement : IClientAchievementTrophyPlacement
{
    public bool TryCreate(ModAchievementSummaryDto achievement, ClientAchievementTarget owner, string ownerDisplayName)
    {
        var data = AchievementTrophyData.FromAchievement(achievement, owner.UserId, owner.ColonyId, ownerDisplayName);
        bool created = AchievementTrophyUtility.TryCreatePlanOnCurrentMap(data, out string message);
        Messages.Message(message, created ? MessageTypeDefOf.TaskCompletion : MessageTypeDefOf.RejectInput, historical: false);
        return created;
    }
}
