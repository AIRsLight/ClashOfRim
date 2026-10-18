using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal static class RaidPolicies
{
    internal static RaidCooldownPolicy BuildCooldown(ClashOfRimServerConfiguration configuration) => new(
        configuration.RaidProtectionDuration, RaidCooldownPolicy.Default.TimeoutCooldown, RaidCooldownPolicy.Default.CancelledCooldown);

    internal static RaidDefenseLockPolicy BuildDefenseLock(ClashOfRimServerConfiguration configuration) =>
        new(configuration.RaidMaxDuration, configuration.RaidTimeoutGracePeriod);

    internal static RaidEligibilityPolicy BuildEligibility(ClashOfRimServerConfiguration configuration)
    {
        return new RaidEligibilityPolicy(
            configuration.RaidMinimumDefenderWealth,
            RequireHostileRelation: true,
            RequireDefenderOffline: true);
    }

}
