namespace AIRsLight.ClashOfRim.Events;

public static class EventVisibility
{
    public static bool IsVisibleTo(AuthoritativeEvent item, string userId, string colonyId)
        => MatchesParty(item.Actor, userId, colonyId) || MatchesParty(item.Target, userId, colonyId);

    // Historical account-wide parties have no colony identity.
    public static bool MatchesParty(EventParty party, string userId, string colonyId)
        => string.Equals(party.UserId, userId, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(party.ColonyId)
                || string.Equals(party.ColonyId, colonyId, StringComparison.Ordinal));
}
