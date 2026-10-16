using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Raids;

internal static class RaidSettlementPayloadReader
{
    public static bool HasSettlement(ModEventDetailDto detail)
    {
        if (detail.EventType != ServerEventType.Raid || string.IsNullOrWhiteSpace(detail.PayloadSummary)) return false;
        try { return Read(detail.PayloadSummary)?.Settlement is not null; }
        catch (Exception ex) when (ex is SerializationException or InvalidOperationException or XmlException) { return false; }
    }

    public static RaidSettlementPayloadSummary? Read(string json)
    {
        var serializer = new DataContractJsonSerializer(typeof(RaidSettlementPayloadSummary));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return serializer.ReadObject(stream) as RaidSettlementPayloadSummary;
    }
}

[DataContract]
internal sealed class RaidSettlementPayloadSummary
{
    [DataMember(Name = "Settlement")]
    public RaidSettlementPayloadDetail? Settlement { get; set; }
}

[DataContract]
internal sealed class RaidSettlementPayloadDetail
{
    [DataMember(Name = "Losses")]
    public List<RaidSettlementLossPayloadSummary> Losses { get; set; } = new();
}

[DataContract]
internal sealed class RaidSettlementLossPayloadSummary
{
    [DataMember(Name = "Def")]
    public string? Def { get; set; }

    [DataMember(Name = "Position")]
    public string? Position { get; set; }

    [DataMember(Name = "MapUniqueId")]
    public string? MapUniqueId { get; set; }

    [DataMember(Name = "WholeThingMissing")]
    public bool WholeThingMissing { get; set; }

    [DataMember(Name = "LossCount")]
    public int LossCount { get; set; }

    [DataMember(Name = "RemainingHitPointsAfterDamage")]
    public int? RemainingHitPointsAfterDamage { get; set; }
}
