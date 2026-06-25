using System.Runtime.Serialization;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.ClientNetwork;

[DataContract]
public sealed class ModProtocolIdentityDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string? ColonyId { get; set; }

    [DataMember(Name = "snapshotId")]
    public string? SnapshotId { get; set; }
}

[DataContract]
public sealed partial class ModCrossMapPawnReferenceDto
{
    [DataMember(Name = "globalId")]
    public string GlobalId { get; set; } = string.Empty;

    [DataMember(Name = "sourceSnapshotId")]
    public string? SourceSnapshotId { get; set; }

    [DataMember(Name = "name")]
    public string? Name { get; set; }

    [DataMember(Name = "dead")]
    public bool? Dead { get; set; }

    [DataMember(Name = "faction")]
    public string? Faction { get; set; }

    [DataMember(Name = "metadata")]
    public Dictionary<string, string?> Metadata { get; set; } = new();
}

[DataContract]
public sealed class ModPawnExchangePackageDto
{
    [DataMember(Name = "packageVersion")]
    public int PackageVersion { get; set; } = 1;

    [DataMember(Name = "reference")]
    public ModCrossMapPawnReferenceDto? Reference { get; set; }

    [DataMember(Name = "identity")]
    public ModPawnExchangeIdentityDto? Identity { get; set; }

    [DataMember(Name = "appearance")]
    public ModPawnExchangeAppearanceDto? Appearance { get; set; }

    [DataMember(Name = "status")]
    public ModPawnExchangeStatusDto? Status { get; set; }

    [DataMember(Name = "apparel")]
    public List<ModPawnExchangeEquipmentItemDto> Apparel { get; set; } = new();

    [DataMember(Name = "equipment")]
    public List<ModPawnExchangeEquipmentItemDto> Equipment { get; set; } = new();

    [DataMember(Name = "relationships")]
    public List<ModPawnExchangeRelationshipStubDto> Relationships { get; set; } = new();

    [DataMember(Name = "scribe")]
    public ModPawnScribePayloadDto? Scribe { get; set; }

    [DataMember(Name = "extensions")]
    public List<ModPawnExchangeExtensionPackageDto> Extensions { get; set; } = new();
}

[DataContract]
public sealed class ModPawnExchangeExtensionPackageDto
{
    [DataMember(Name = "providerId")]
    public string ProviderId { get; set; } = string.Empty;

    [DataMember(Name = "kind")]
    public string Kind { get; set; } = string.Empty;

    [DataMember(Name = "metadata")]
    public Dictionary<string, string?> Metadata { get; set; } = new();

    [DataMember(Name = "payloadJson")]
    public string? PayloadJson { get; set; }
}

[DataContract]
public sealed partial class ModPawnExchangeIdentityDto
{
    [DataMember(Name = "thingDef")]
    public string? ThingDef { get; set; }

    [DataMember(Name = "pawnKindDef")]
    public string? PawnKindDef { get; set; }

    [DataMember(Name = "factionDef")]
    public string? FactionDef { get; set; }

    [DataMember(Name = "gender")]
    public string? Gender { get; set; }
}

[DataContract]
public sealed partial class ModPawnExchangeAppearanceDto
{
    [DataMember(Name = "displayName")]
    public string? DisplayName { get; set; }

    [DataMember(Name = "bodyTypeDef")]
    public string? BodyTypeDef { get; set; }

    [DataMember(Name = "headTypeDef")]
    public string? HeadTypeDef { get; set; }

    [DataMember(Name = "hairDef")]
    public string? HairDef { get; set; }

    [DataMember(Name = "beardDef")]
    public string? BeardDef { get; set; }

    [DataMember(Name = "skinColor")]
    public string? SkinColor { get; set; }

    [DataMember(Name = "hairColor")]
    public string? HairColor { get; set; }

}

[DataContract]
public sealed class ModPawnExchangeStatusDto
{
    [DataMember(Name = "dead")]
    public bool Dead { get; set; }

    [DataMember(Name = "biologicalAgeTicks")]
    public long? BiologicalAgeTicks { get; set; }

    [DataMember(Name = "chronologicalAgeTicks")]
    public long? ChronologicalAgeTicks { get; set; }

    [DataMember(Name = "deathCauseDef")]
    public string? DeathCauseDef { get; set; }

    [DataMember(Name = "healthState")]
    public string? HealthState { get; set; }
}

[DataContract]
public sealed class ModPawnExchangeEquipmentItemDto
{
    [DataMember(Name = "globalId")]
    public string GlobalId { get; set; } = string.Empty;

    [DataMember(Name = "def")]
    public string? Def { get; set; }

    [DataMember(Name = "label")]
    public string? Label { get; set; }

    [DataMember(Name = "stackCount")]
    public int StackCount { get; set; }

    [DataMember(Name = "quality")]
    public string? Quality { get; set; }

    [DataMember(Name = "hitPoints")]
    public int? HitPoints { get; set; }

    [DataMember(Name = "wornByCorpse")]
    public bool? WornByCorpse { get; set; }

    [DataMember(Name = "biocoded")]
    public bool? Biocoded { get; set; }

    [DataMember(Name = "biocodedPawnGlobalId")]
    public string? BiocodedPawnGlobalId { get; set; }

    [DataMember(Name = "uniqueWeapon")]
    public bool? UniqueWeapon { get; set; }

    [DataMember(Name = "uniqueWeaponName")]
    public string? UniqueWeaponName { get; set; }

    [DataMember(Name = "uniqueWeaponTraits")]
    public List<string> UniqueWeaponTraits { get; set; } = new();
}

[DataContract]
public sealed class ModPawnExchangeRelationshipStubDto
{
    [DataMember(Name = "otherPawnGlobalId")]
    public string OtherPawnGlobalId { get; set; } = string.Empty;

    [DataMember(Name = "otherPawnName")]
    public string? OtherPawnName { get; set; }

    [DataMember(Name = "otherPawnDead")]
    public bool OtherPawnDead { get; set; }

    [DataMember(Name = "relationDef")]
    public string? RelationDef { get; set; }
}

[DataContract]
public sealed class ModPawnScribePayloadDto
{
    [DataMember(Name = "xml")]
    public string Xml { get; set; } = string.Empty;

    [DataMember(Name = "xmlSha256")]
    public string? XmlSha256 { get; set; }

    [DataMember(Name = "pawnReferenceReplacements")]
    public List<ModPawnScribePawnReferenceReplacementDto> PawnReferenceReplacements { get; set; } = new();
}

[DataContract]
public sealed class ModPawnScribePawnReferenceReplacementDto
{
    [DataMember(Name = "sourceLoadId")]
    public string SourceLoadId { get; set; } = string.Empty;

    [DataMember(Name = "placeholderLoadId")]
    public string PlaceholderLoadId { get; set; } = string.Empty;

    [DataMember(Name = "reference")]
    public ModCrossMapPawnReferenceDto? Reference { get; set; }
}
