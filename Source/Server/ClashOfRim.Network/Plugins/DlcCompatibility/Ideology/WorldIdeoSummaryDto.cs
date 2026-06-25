using System;

namespace AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;

public sealed class WorldIdeoSummaryDto
{
    public WorldIdeoSummaryDto(
        string globalKey,
        string ownerUserId,
        string? ownerColonyId,
        string? sourceSnapshotId,
        string? localId,
        string? name,
        string? culture,
        string? cultureLabel,
        string? cultureIconPath,
        string? primaryFactionColor,
        string? primaryFactionColorHex,
        string? foundationDefName,
        string? factionDefName,
        string? iconDefName,
        string? iconPath,
        string? colorDefName,
        string? colorHex,
        string? savedIdeoPackageXml,
        string? savedIdeoPackageSha256,
        long? updatedAtGameTicks,
        IReadOnlyList<string>? memeDefNames,
        IReadOnlyList<string>? preceptDefNames,
        IReadOnlyList<string>? styleCategoryDefNames,
        bool hidden,
        bool initialPlayerIdeo,
        int memeCount,
        int preceptCount)
    {
        GlobalKey = globalKey;
        OwnerUserId = ownerUserId;
        OwnerColonyId = ownerColonyId;
        SourceSnapshotId = sourceSnapshotId;
        LocalId = localId;
        Name = name;
        Culture = culture;
        CultureLabel = cultureLabel;
        CultureIconPath = cultureIconPath;
        PrimaryFactionColor = primaryFactionColor;
        PrimaryFactionColorHex = primaryFactionColorHex;
        FoundationDefName = foundationDefName;
        FactionDefName = factionDefName;
        IconDefName = iconDefName;
        IconPath = iconPath;
        ColorDefName = colorDefName;
        ColorHex = colorHex;
        SavedIdeoPackageXml = savedIdeoPackageXml;
        SavedIdeoPackageSha256 = savedIdeoPackageSha256;
        UpdatedAtGameTicks = updatedAtGameTicks;
        MemeDefNames = memeDefNames ?? Array.Empty<string>();
        PreceptDefNames = preceptDefNames ?? Array.Empty<string>();
        StyleCategoryDefNames = styleCategoryDefNames ?? Array.Empty<string>();
        Hidden = hidden;
        InitialPlayerIdeo = initialPlayerIdeo;
        MemeCount = memeCount;
        PreceptCount = preceptCount;
    }

    public string GlobalKey { get; }

    public string OwnerUserId { get; }

    public string? OwnerColonyId { get; }

    public string? SourceSnapshotId { get; }

    public string? LocalId { get; }

    public string? Name { get; }

    public string? Culture { get; }

    public string? CultureLabel { get; }

    public string? CultureIconPath { get; }

    public string? PrimaryFactionColor { get; }

    public string? PrimaryFactionColorHex { get; }

    public string? FoundationDefName { get; }

    public string? FactionDefName { get; }

    public string? IconDefName { get; }

    public string? IconPath { get; }

    public string? ColorDefName { get; }

    public string? ColorHex { get; }

    public string? SavedIdeoPackageXml { get; }

    public string? SavedIdeoPackageSha256 { get; }

    public long? UpdatedAtGameTicks { get; }

    public IReadOnlyList<string> MemeDefNames { get; }

    public IReadOnlyList<string> PreceptDefNames { get; }

    public IReadOnlyList<string> StyleCategoryDefNames { get; }

    public bool Hidden { get; }

    public bool InitialPlayerIdeo { get; }

    public int MemeCount { get; }

    public int PreceptCount { get; }
}
