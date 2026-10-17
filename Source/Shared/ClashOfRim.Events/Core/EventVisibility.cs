namespace AIRsLight.ClashOfRim.Events;

public static class EventVisibility
{
    // Historical account-wide parties have no colony identity.
    public static bool MatchesParty(EventParty party, string userId, string colonyId)
        => string.Equals(party.UserId, userId, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(party.ColonyId)
                || string.Equals(party.ColonyId, colonyId, StringComparison.Ordinal));
}
