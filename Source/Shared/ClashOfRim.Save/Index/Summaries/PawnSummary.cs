namespace AIRsLight.ClashOfRim.Save;

public sealed record PawnSummary(
    string LocalId,
    string GlobalKey,
    string? MapUniqueId,
    string? Source,
    string? Def,
    string? KindDef,
    string? Name,
    bool? Dead,
    string? Faction,
    string? HostFaction);
