using System.Collections.Generic;
using System.Runtime.Serialization;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

[DataContract]
public sealed class ModWorldIdeoSummaryDto
{
    [DataMember(Name = "globalKey")]
    public string GlobalKey { get; set; } = string.Empty;

    [DataMember(Name = "ownerUserId")]
    public string OwnerUserId { get; set; } = string.Empty;

    [DataMember(Name = "ownerColonyId")]
    public string? OwnerColonyId { get; set; }

    [DataMember(Name = "sourceSnapshotId")]
    public string? SourceSnapshotId { get; set; }

    [DataMember(Name = "localId")]
    public string? LocalId { get; set; }

    [DataMember(Name = "name")]
    public string? Name { get; set; }

    [DataMember(Name = "culture")]
    public string? Culture { get; set; }

    [DataMember(Name = "cultureLabel")]
    public string? CultureLabel { get; set; }

    [DataMember(Name = "cultureIconPath")]
    public string? CultureIconPath { get; set; }

    [DataMember(Name = "primaryFactionColor")]
    public string? PrimaryFactionColor { get; set; }

    [DataMember(Name = "primaryFactionColorHex")]
    public string? PrimaryFactionColorHex { get; set; }

    [DataMember(Name = "foundationDefName")]
    public string? FoundationDefName { get; set; }

    [DataMember(Name = "factionDefName")]
    public string? FactionDefName { get; set; }

    [DataMember(Name = "iconDefName")]
    public string? IconDefName { get; set; }

    [DataMember(Name = "iconPath")]
    public string? IconPath { get; set; }

    [DataMember(Name = "colorDefName")]
    public string? ColorDefName { get; set; }

    [DataMember(Name = "colorHex")]
    public string? ColorHex { get; set; }

    [DataMember(Name = "savedIdeoPackageXml")]
    public string? SavedIdeoPackageXml { get; set; }

    [DataMember(Name = "savedIdeoPackageSha256")]
    public string? SavedIdeoPackageSha256 { get; set; }

    [DataMember(Name = "updatedAtGameTicks")]
    public long? UpdatedAtGameTicks { get; set; }

    [DataMember(Name = "memeDefNames")]
    public List<string> MemeDefNames { get; set; } = new();

    [DataMember(Name = "preceptDefNames")]
    public List<string> PreceptDefNames { get; set; } = new();

    [DataMember(Name = "styleCategoryDefNames")]
    public List<string> StyleCategoryDefNames { get; set; } = new();

    [DataMember(Name = "hidden")]
    public bool Hidden { get; set; }

    [DataMember(Name = "initialPlayerIdeo")]
    public bool InitialPlayerIdeo { get; set; }

    [DataMember(Name = "memeCount")]
    public int MemeCount { get; set; }

    [DataMember(Name = "preceptCount")]
    public int PreceptCount { get; set; }
}
