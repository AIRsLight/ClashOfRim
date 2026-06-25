namespace AIRsLight.ClashOfRim.Save;

public sealed record ThingSummary(
    string LocalId,
    string GlobalKey,
    string? MapUniqueId,
    string? Class,
    string? Def,
    string? Position,
    string? Faction,
    string? StackCount,
    string? HitPoints,
    string? Stuff,
    string? Quality,
    bool IsPawn)
{
    public string? ContainerGlobalKey { get; init; }

    public string? ContainerLocalId { get; init; }

    public string? ClashOfRimOriginalThingId { get; init; }

    public string? SettlementAssetKind { get; init; }

    public bool SettlementDamageOnly { get; init; }
}
