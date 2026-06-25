namespace AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;

public sealed record IdeoSummary(
    string? Id,
    string? GlobalKey,
    string? Name,
    string? Culture,
    string? CultureLabel,
    string? CultureIconPath,
    string? PrimaryFactionColor,
    string? PrimaryFactionColorHex,
    string? FoundationDefName,
    string? FactionDefName,
    string? IconDefName,
    string? IconPath,
    string? ColorDefName,
    string? ColorHex,
    IReadOnlyList<string> MemeDefNames,
    IReadOnlyList<string> PreceptDefNames,
    IReadOnlyList<IdeoPreceptSummary> Precepts,
    IReadOnlyList<string> StyleCategoryDefNames,
    bool Hidden,
    bool InitialPlayerIdeo,
    int MemeCount,
    int PreceptCount);

public sealed record IdeoPreceptSummary(
    string? DefName,
    string? PreceptClass,
    string? ApparelDefName,
    string? NobleWeaponClassDefName,
    string? DespisedWeaponClassDefName,
    string? TargetGender,
    string? OverrideGender);
