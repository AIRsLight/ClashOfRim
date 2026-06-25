using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Gifts;

internal static class GiftPayloadReader
{
    private static readonly DataContractJsonSerializerSettings JsonSerializerSettings = new()
    {
        UseSimpleDictionaryFormat = true
    };

    internal static GiftPayloadSummary Read(string json)
    {
        var serializer = new DataContractJsonSerializer(typeof(GiftPayloadSummary), JsonSerializerSettings);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        object? value = serializer.ReadObject(stream);
        return value as GiftPayloadSummary
            ?? throw new InvalidOperationException("Gift payload type mismatch.");
    }
}

[DataContract]
internal sealed class GiftPayloadSummary
{
    [DataMember(Name = "Items")]
    public List<GiftItemSummary> Items { get; set; } = new();

    [DataMember(Name = "Message")]
    public string? Message { get; set; }

    [DataMember(Name = "DeliveryKind")]
    public string? DeliveryKind { get; set; }

    public bool IsForcedDelivery =>
        string.Equals(DeliveryKind, "Forced", StringComparison.OrdinalIgnoreCase);
}

[DataContract]
internal sealed partial class GiftItemSummary
{
    [DataMember(Name = "GlobalKey")]
    public string GlobalKey { get; set; } = string.Empty;

    [DataMember(Name = "Def")]
    public string? Def { get; set; }

    [DataMember(Name = "StackCount")]
    public int StackCount { get; set; }

    [DataMember(Name = "SourceSnapshotId")]
    public string? SourceSnapshotId { get; set; }

    [DataMember(Name = "Quality")]
    public string? Quality { get; set; }

    [DataMember(Name = "HitPoints")]
    public int? HitPoints { get; set; }

    [DataMember(Name = "StuffDefName")]
    public string? StuffDefName { get; set; }

    [DataMember(Name = "MaxHitPoints")]
    public int? MaxHitPoints { get; set; }

    [DataMember(Name = "MinifiedInnerDefName")]
    public string? MinifiedInnerDefName { get; set; }

    [DataMember(Name = "MinifiedInnerStuffDefName")]
    public string? MinifiedInnerStuffDefName { get; set; }

    [DataMember(Name = "MinifiedInnerQuality")]
    public string? MinifiedInnerQuality { get; set; }

    [DataMember(Name = "MinifiedInnerHitPoints")]
    public int? MinifiedInnerHitPoints { get; set; }

    [DataMember(Name = "MinifiedInnerMaxHitPoints")]
    public int? MinifiedInnerMaxHitPoints { get; set; }

    [DataMember(Name = "WornByCorpse")]
    public bool? WornByCorpse { get; set; }

    [DataMember(Name = "Biocoded")]
    public bool? Biocoded { get; set; }

    [DataMember(Name = "BiocodedPawnLabel")]
    public string? BiocodedPawnLabel { get; set; }

    [DataMember(Name = "BiocodedPawnGlobalId")]
    public string? BiocodedPawnGlobalId { get; set; }

    [DataMember(Name = "DisplayLabel")]
    public string? DisplayLabel { get; set; }

    [DataMember(Name = "MarketValue")]
    public float? MarketValue { get; set; }

    [DataMember(Name = "UniqueWeapon")]
    public bool? UniqueWeapon { get; set; }

    [DataMember(Name = "UniqueWeaponTraits")]
    public List<string> UniqueWeaponTraits { get; set; } = new();

    [DataMember(Name = "Metadata")]
    public Dictionary<string, string?> Metadata { get; set; } = new();

    [DataMember(Name = "PawnPackage")]
    public GiftPawnExchangePackageSummary? PawnPackage { get; set; }

    [DataMember(Name = "PawnPackageId")]
    public string? PawnPackageId { get; set; }
}

[DataContract]
internal sealed class GiftPawnExchangePackageSummary
{
    [DataMember(Name = "PackageVersion")]
    public int PackageVersion { get; set; } = 1;

    [DataMember(Name = "Reference")]
    public GiftCrossMapPawnReferenceSummary? Reference { get; set; }

    [DataMember(Name = "Identity")]
    public GiftPawnExchangeIdentitySummary? Identity { get; set; }

    [DataMember(Name = "Appearance")]
    public GiftPawnExchangeAppearanceSummary? Appearance { get; set; }

    [DataMember(Name = "Status")]
    public GiftPawnExchangeStatusSummary? Status { get; set; }

    [DataMember(Name = "Apparel")]
    public List<GiftPawnExchangeEquipmentItemSummary> Apparel { get; set; } = new();

    [DataMember(Name = "Equipment")]
    public List<GiftPawnExchangeEquipmentItemSummary> Equipment { get; set; } = new();

    [DataMember(Name = "Relationships")]
    public List<GiftPawnExchangeRelationshipStubSummary> Relationships { get; set; } = new();

    [DataMember(Name = "Extensions")]
    public List<GiftPawnExchangeExtensionPackageSummary> Extensions { get; set; } = new();

    [DataMember(Name = "Scribe")]
    public GiftPawnScribePayloadSummary? Scribe { get; set; }
}

[DataContract]
internal sealed class GiftPawnExchangeExtensionPackageSummary
{
    [DataMember(Name = "ProviderId")]
    public string ProviderId { get; set; } = string.Empty;

    [DataMember(Name = "Kind")]
    public string Kind { get; set; } = string.Empty;

    [DataMember(Name = "Metadata")]
    public Dictionary<string, string?> Metadata { get; set; } = new();

    [DataMember(Name = "PayloadJson")]
    public string? PayloadJson { get; set; }
}

[DataContract]
internal sealed class GiftCrossMapPawnReferenceSummary
{
    [DataMember(Name = "GlobalId")]
    public string GlobalId { get; set; } = string.Empty;

    [DataMember(Name = "SourceSnapshotId")]
    public string? SourceSnapshotId { get; set; }

    [DataMember(Name = "Name")]
    public string? Name { get; set; }

    [DataMember(Name = "Dead")]
    public bool? Dead { get; set; }

    [DataMember(Name = "Faction")]
    public string? Faction { get; set; }

    [DataMember(Name = "Metadata")]
    public Dictionary<string, string?> Metadata { get; set; } = new();
}

[DataContract]
internal sealed class GiftPawnExchangeIdentitySummary
{
    [DataMember(Name = "ThingDef")]
    public string? ThingDef { get; set; }

    [DataMember(Name = "PawnKindDef")]
    public string? PawnKindDef { get; set; }

    [DataMember(Name = "FactionDef")]
    public string? FactionDef { get; set; }

    [DataMember(Name = "Gender")]
    public string? Gender { get; set; }
}

[DataContract]
internal sealed class GiftPawnExchangeAppearanceSummary
{
    [DataMember(Name = "DisplayName")]
    public string? DisplayName { get; set; }

    [DataMember(Name = "BodyTypeDef")]
    public string? BodyTypeDef { get; set; }

    [DataMember(Name = "HeadTypeDef")]
    public string? HeadTypeDef { get; set; }

    [DataMember(Name = "HairDef")]
    public string? HairDef { get; set; }

    [DataMember(Name = "BeardDef")]
    public string? BeardDef { get; set; }

    [DataMember(Name = "SkinColor")]
    public string? SkinColor { get; set; }

    [DataMember(Name = "HairColor")]
    public string? HairColor { get; set; }
}

[DataContract]
internal sealed class GiftPawnExchangeStatusSummary
{
    [DataMember(Name = "Dead")]
    public bool Dead { get; set; }

    [DataMember(Name = "BiologicalAgeTicks")]
    public long? BiologicalAgeTicks { get; set; }

    [DataMember(Name = "ChronologicalAgeTicks")]
    public long? ChronologicalAgeTicks { get; set; }

    [DataMember(Name = "DeathCauseDef")]
    public string? DeathCauseDef { get; set; }

    [DataMember(Name = "HealthState")]
    public string? HealthState { get; set; }
}

[DataContract]
internal sealed class GiftPawnExchangeEquipmentItemSummary
{
    [DataMember(Name = "GlobalId")]
    public string GlobalId { get; set; } = string.Empty;

    [DataMember(Name = "Def")]
    public string? Def { get; set; }

    [DataMember(Name = "Label")]
    public string? Label { get; set; }

    [DataMember(Name = "StackCount")]
    public int StackCount { get; set; }

    [DataMember(Name = "Quality")]
    public string? Quality { get; set; }

    [DataMember(Name = "HitPoints")]
    public int? HitPoints { get; set; }

    [DataMember(Name = "WornByCorpse")]
    public bool? WornByCorpse { get; set; }

    [DataMember(Name = "Biocoded")]
    public bool? Biocoded { get; set; }

    [DataMember(Name = "BiocodedPawnGlobalId")]
    public string? BiocodedPawnGlobalId { get; set; }

    [DataMember(Name = "UniqueWeapon")]
    public bool? UniqueWeapon { get; set; }

    [DataMember(Name = "UniqueWeaponName")]
    public string? UniqueWeaponName { get; set; }

    [DataMember(Name = "UniqueWeaponTraits")]
    public List<string> UniqueWeaponTraits { get; set; } = new();
}

[DataContract]
internal sealed class GiftPawnExchangeRelationshipStubSummary
{
    [DataMember(Name = "OtherPawnGlobalId")]
    public string OtherPawnGlobalId { get; set; } = string.Empty;

    [DataMember(Name = "OtherPawnName")]
    public string? OtherPawnName { get; set; }

    [DataMember(Name = "OtherPawnDead")]
    public bool OtherPawnDead { get; set; }

    [DataMember(Name = "RelationDef")]
    public string? RelationDef { get; set; }
}

[DataContract]
internal sealed class GiftPawnScribePayloadSummary
{
    [DataMember(Name = "Xml")]
    public string Xml { get; set; } = string.Empty;

    [DataMember(Name = "XmlSha256")]
    public string? XmlSha256 { get; set; }

    [DataMember(Name = "PawnReferenceReplacements")]
    public List<GiftPawnScribePawnReferenceReplacementSummary> PawnReferenceReplacements { get; set; } = new();
}

[DataContract]
internal sealed class GiftPawnScribePawnReferenceReplacementSummary
{
    [DataMember(Name = "SourceLoadId")]
    public string SourceLoadId { get; set; } = string.Empty;

    [DataMember(Name = "PlaceholderLoadId")]
    public string PlaceholderLoadId { get; set; } = string.Empty;

    [DataMember(Name = "Reference")]
    public GiftCrossMapPawnReferenceSummary? Reference { get; set; }
}
