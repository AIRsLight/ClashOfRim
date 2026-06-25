namespace AIRsLight.ClashOfRim.Save;

public sealed record MapSummary(
    string? UniqueId,
    string? GeneratedId,
    string? ParentWorldObjectId,
    string? Size,
    bool HasCompressedThingMap,
    bool HasTerrainGrid,
    bool HasRoofGrid,
    bool HasFogGrid,
    int ThingCount,
    int PawnCount,
    IReadOnlyList<string>? GrowingZoneCells = null,
    float? WealthTotal = null,
    bool WasSpawnedViaGravshipLanding = false,
    int PlayerColonistCount = 0);
