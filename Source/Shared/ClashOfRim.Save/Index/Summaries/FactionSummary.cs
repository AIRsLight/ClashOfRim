namespace AIRsLight.ClashOfRim.Save;

public sealed record FactionSummary(
    string? LoadId,
    string? UniqueLoadId,
    string? Def,
    string? Name,
    string? Leader,
    bool Temporary,
    bool Hidden,
    bool Deactivated);
