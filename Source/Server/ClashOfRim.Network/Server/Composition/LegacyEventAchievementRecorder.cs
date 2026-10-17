using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Events;

namespace AIRsLight.ClashOfRim.Network;

// Composition bridge to the existing achievement pipeline; retired with M8.
internal sealed class LegacyEventAchievementRecorder(ClashOfRimNetworkState state) : IEventAchievementRecorder
{
    public void Record(AuthoritativeEvent item, DateTimeOffset nowUtc) =>
        ClashOfRimNetworkServer.RecordAuthoritativeEventAchievements(state, item, nowUtc);
}
