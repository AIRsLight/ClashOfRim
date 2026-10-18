using AIRsLight.ClashOfRim.Modules.Achievements;

namespace AIRsLight.ClashOfRim.Achievements;

internal sealed class ClientEndgameRuntime : IClientEndgameRuntime
{
    public bool IsObserving => ClashOfRimGameComponent.HasActiveObservationSession;
    public ClientAchievementCandidates? Pending => ClashOfRimGameComponent.CurrentAchievementCandidates;
}
