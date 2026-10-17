using AIRsLight.ClashOfRim.Events;

namespace AIRsLight.ClashOfRim.Network.Modules.Events;

internal interface IEventAchievementRecorder
{
    void Record(AuthoritativeEvent item, DateTimeOffset nowUtc);
}
