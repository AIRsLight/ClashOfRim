using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Collections.Generic;
using System.Text;

namespace AIRsLight.ClashOfRim.Support;

internal static class SupportPawnPayloadReader
{
    private static readonly DataContractJsonSerializerSettings JsonSerializerSettings = new()
    {
        UseSimpleDictionaryFormat = true
    };

    public static SupportPawnPayloadSummary Read(string payloadSummary)
    {
        if (string.IsNullOrWhiteSpace(payloadSummary))
        {
            throw new InvalidOperationException(ClashOfRimText.Key("ClashOfRim.Support.PayloadEmpty"));
        }

        var serializer = new DataContractJsonSerializer(typeof(SupportPawnPayloadSummary), JsonSerializerSettings);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(NormalizeNestedPayloadMemberNames(payloadSummary)));
        object? value = serializer.ReadObject(stream);
        if (value is not SupportPawnPayloadSummary summary)
        {
            throw new InvalidOperationException(ClashOfRimText.Key("ClashOfRim.Support.PayloadParseFailed"));
        }

        return summary;
    }

    private static string NormalizeNestedPayloadMemberNames(string payloadSummary)
    {
        return payloadSummary
            .Replace("\"faction\"", "\"Faction\"")
            .Replace("\"metadata\"", "\"Metadata\"")
            .Replace("\"reference\"", "\"Reference\"")
            .Replace("\"globalId\"", "\"GlobalId\"")
            .Replace("\"name\"", "\"Name\"")
            .Replace("\"dead\"", "\"Dead\"")
            .Replace("\"packageVersion\"", "\"PackageVersion\"")
            .Replace("\"identity\"", "\"Identity\"")
            .Replace("\"appearance\"", "\"Appearance\"")
            .Replace("\"extensions\"", "\"Extensions\"")
            .Replace("\"relationships\"", "\"Relationships\"")
            .Replace("\"scribe\"", "\"Scribe\"")
            .Replace("\"providerId\"", "\"ProviderId\"")
            .Replace("\"kind\"", "\"Kind\"")
            .Replace("\"payloadJson\"", "\"PayloadJson\"")
            .Replace("\"thingDef\"", "\"ThingDef\"")
            .Replace("\"pawnKindDef\"", "\"PawnKindDef\"")
            .Replace("\"factionDef\"", "\"FactionDef\"")
            .Replace("\"gender\"", "\"Gender\"")
            .Replace("\"displayName\"", "\"DisplayName\"")
            .Replace("\"otherPawnGlobalId\"", "\"OtherPawnGlobalId\"")
            .Replace("\"otherPawnName\"", "\"OtherPawnName\"")
            .Replace("\"otherPawnDead\"", "\"OtherPawnDead\"")
            .Replace("\"relationDef\"", "\"RelationDef\"")
            .Replace("\"xml\"", "\"Xml\"")
            .Replace("\"xmlSha256\"", "\"XmlSha256\"")
            .Replace("\"pawnReferenceReplacements\"", "\"PawnReferenceReplacements\"")
            .Replace("\"sourceLoadId\"", "\"SourceLoadId\"")
            .Replace("\"placeholderLoadId\"", "\"PlaceholderLoadId\"");
    }
}

[DataContract]
internal sealed class SupportPawnPayloadSummary
{
    [DataMember(Name = "pawnGlobalKey")]
    public string PawnGlobalKey { get; set; } = string.Empty;

    [DataMember(Name = "sourceSnapshotId")]
    public string SourceSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "pawnName")]
    public string? PawnName { get; set; }

    [DataMember(Name = "temporaryControl")]
    public bool TemporaryControl { get; set; }

    [DataMember(Name = "sourceTile")]
    public int? SourceTile { get; set; }

    [DataMember(Name = "sourceCaravanLoadId")]
    public string? SourceCaravanLoadId { get; set; }

    [DataMember(Name = "returnToSender")]
    public bool ReturnToSender { get; set; }

    [DataMember(Name = "rejectionReason")]
    public string? RejectionReason { get; set; }

    [DataMember(Name = "permanentSupport")]
    public bool PermanentSupport { get; set; }

    [DataMember(Name = "supportDurationDays")]
    public int? SupportDurationDays { get; set; }

    [DataMember(Name = "expiresAtGameTicks")]
    public long? ExpiresAtGameTicks { get; set; }

    [DataMember(Name = "autoReturnOnSettlement")]
    public bool AutoReturnOnSettlement { get; set; }

    [DataMember(Name = "sourceEventId")]
    public string? SourceEventId { get; set; }

    [DataMember(Name = "returnReason")]
    public string? ReturnReason { get; set; }

    [DataMember(Name = "pawnReference")]
    public SupportPawnReferenceSummary? PawnReference { get; set; }

    [DataMember(Name = "pawnPackage")]
    public SupportPawnExchangePackageSummary? PawnPackage { get; set; }
}

[DataContract]
internal sealed class SupportPawnReferenceSummary
{
    [DataMember(Name = "GlobalId")]
    public string? GlobalId { get; set; }

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
internal sealed class SupportPawnExchangePackageSummary
{
    [DataMember(Name = "PackageVersion")]
    public int PackageVersion { get; set; }

    [DataMember(Name = "Reference")]
    public SupportPawnReferenceSummary? Reference { get; set; }

    [DataMember(Name = "Identity")]
    public SupportPawnExchangeIdentitySummary? Identity { get; set; }

    [DataMember(Name = "Appearance")]
    public SupportPawnExchangeAppearanceSummary? Appearance { get; set; }

    [DataMember(Name = "Extensions")]
    public List<SupportPawnExchangeExtensionPackageSummary> Extensions { get; set; } = new();

    [DataMember(Name = "Relationships")]
    public List<SupportPawnExchangeRelationshipStubSummary> Relationships { get; set; } = new();

    [DataMember(Name = "Scribe")]
    public SupportPawnScribePayloadSummary? Scribe { get; set; }
}

[DataContract]
internal sealed class SupportPawnExchangeExtensionPackageSummary
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
internal sealed class SupportPawnExchangeIdentitySummary
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
internal sealed class SupportPawnExchangeAppearanceSummary
{
    [DataMember(Name = "DisplayName")]
    public string? DisplayName { get; set; }
}

[DataContract]
internal sealed class SupportPawnExchangeRelationshipStubSummary
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
internal sealed class SupportPawnScribePayloadSummary
{
    [DataMember(Name = "Xml")]
    public string Xml { get; set; } = string.Empty;

    [DataMember(Name = "XmlSha256")]
    public string? XmlSha256 { get; set; }

    [DataMember(Name = "PawnReferenceReplacements")]
    public List<SupportPawnScribePawnReferenceReplacementSummary> PawnReferenceReplacements { get; set; } = new();
}

[DataContract]
internal sealed class SupportPawnScribePawnReferenceReplacementSummary
{
    [DataMember(Name = "SourceLoadId")]
    public string SourceLoadId { get; set; } = string.Empty;

    [DataMember(Name = "PlaceholderLoadId")]
    public string PlaceholderLoadId { get; set; } = string.Empty;
}
