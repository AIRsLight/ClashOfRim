using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal static class RaidPolicies
{
    internal static RaidEligibilityPolicy BuildEligibility(ClashOfRimServerConfiguration configuration)
    {
        return new RaidEligibilityPolicy(
            configuration.RaidMinimumDefenderWealth,
            RequireHostileRelation: true,
            RequireDefenderOffline: true);
    }

}
