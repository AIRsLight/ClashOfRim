namespace AIRsLight.ClashOfRim.Save;

public sealed record WorldObjectSummary(
    string? Id,
    string? UniqueLoadId,
    string? Class,
    string? Def,
    string? Tile,
    string? Faction,
    string? Name,
    bool Destroyed)
{
    public string? ClashOfRimRelatedEventId { get; init; }
}
