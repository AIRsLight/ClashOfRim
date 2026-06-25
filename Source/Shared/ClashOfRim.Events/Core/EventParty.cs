namespace AIRsLight.ClashOfRim.Events;

public sealed record EventParty(
    string UserId,
    string? ColonyId = null,
    string? FactionId = null);
