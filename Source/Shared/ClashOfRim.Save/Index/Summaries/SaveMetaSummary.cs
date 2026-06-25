namespace AIRsLight.ClashOfRim.Save;

public sealed record SaveMetaSummary(
    string? GameVersion,
    IReadOnlyList<string> ModIds,
    IReadOnlyList<string> ModSteamIds,
    IReadOnlyList<string> ModNames);
