namespace AIRsLight.ClashOfRim.Network;

// Public console/plugin compatibility facade. RaidProtection owns the rules.
public static class RaidCooldownRuntimeService
{
    public static DateTimeOffset? CurrentUntil(ClashOfRimNetworkState state, string defenderUserId, string defenderColonyId, DateTimeOffset nowUtc) =>
        (state ?? throw new ArgumentNullException(nameof(state))).RaidProtection.CurrentUntil(defenderUserId, defenderColonyId, nowUtc);

    public static RaidCooldownOverrideRecord SetCurrent(ClashOfRimNetworkState state, string defenderUserId, string defenderColonyId, double hours, DateTimeOffset nowUtc) =>
        (state ?? throw new ArgumentNullException(nameof(state))).RaidProtection.SetCurrent(defenderUserId, defenderColonyId, hours, nowUtc);
}
