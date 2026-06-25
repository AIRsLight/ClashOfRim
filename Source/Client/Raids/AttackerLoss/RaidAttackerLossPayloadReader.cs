using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using AIRsLight.ClashOfRim.ClientNetwork;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal static class RaidAttackerLossPayloadReader
{
    public static bool HasAttackerLoss(ModEventDetailDto detail)
    {
        if (detail is null
            || !string.Equals(detail.EventType, "Raid", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(detail.PayloadSummary))
        {
            return false;
        }

        try
        {
            return Read(detail.PayloadSummary).AttackerLoss is not null;
        }
        catch (Exception ex) when (ex is SerializationException or InvalidOperationException or XmlException)
        {
            return false;
        }
    }

    public static bool TryReadSummary(ModEventDetailDto detail, out RaidAttackerLossSummary? summary)
    {
        summary = null;
        if (detail is null
            || !string.Equals(detail.EventType, "Raid", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(detail.PayloadSummary))
        {
            return false;
        }

        try
        {
            summary = Read(detail.PayloadSummary).AttackerLoss;
            return summary is not null;
        }
        catch (Exception ex) when (ex is SerializationException or InvalidOperationException or XmlException)
        {
            return false;
        }
    }

    public static bool TryRead(
        ModEventDetailDto detail,
        string? currentSnapshotId,
        out RaidAttackerLossApplicationRequest? request,
        out string message)
    {
        request = null;
        message = string.Empty;
        if (detail is null
            || !string.Equals(detail.EventType, "Raid", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(detail.PayloadSummary))
        {
            message = ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerLossWrongEvent");
            return false;
        }

        try
        {
            RaidPayloadSummary payload = Read(detail.PayloadSummary);
            RaidAttackerLossSummary? loss = payload.AttackerLoss;
            if (loss is null)
            {
                message = ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerLossMissingRecord");
                return false;
            }

            request = new RaidAttackerLossApplicationRequest(
                loss.SourceRaidEventId,
                loss.AttackerSnapshotId,
                currentSnapshotId,
                loss.LostPawnGlobalKeys,
                (loss.LostThings ?? new List<RaidLostThingSummary>())
                    .Where(thing => !string.IsNullOrWhiteSpace(thing.GlobalKey))
                    .Select(thing => new RaidLostThingReference(
                        thing.GlobalKey,
                        thing.Def,
                        Math.Max(1, thing.StackCount)))
                    .ToList(),
                loss.Reason,
                ParseClientEffect(loss.ClientEffect));
            return true;
        }
        catch (Exception ex) when (ex is SerializationException or InvalidOperationException or XmlException)
        {
            message = ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerLossPayloadFailed", ex.Message.Named("MESSAGE"));
            return false;
        }
    }

    private static RaidPayloadSummary Read(string json)
    {
        var serializer = new DataContractJsonSerializer(typeof(RaidPayloadSummary));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        object? value = serializer.ReadObject(stream);
        return value as RaidPayloadSummary
            ?? throw new InvalidOperationException("Raid payload type mismatch.");
    }

    private static RaidAttackerLossClientEffect ParseClientEffect(int value)
    {
        return Enum.IsDefined(typeof(RaidAttackerLossClientEffect), value)
            ? (RaidAttackerLossClientEffect)value
            : RaidAttackerLossClientEffect.TriggerVanillaCaravanLostEvent;
    }
}

[DataContract]
internal sealed class RaidPayloadSummary
{
    [DataMember(Name = "AttackerLoss")]
    public RaidAttackerLossSummary? AttackerLoss { get; set; }
}

[DataContract]
internal sealed class RaidAttackerLossSummary
{
    [DataMember(Name = "SourceRaidEventId")]
    public string? SourceRaidEventId { get; set; }

    [DataMember(Name = "AttackerSnapshotId")]
    public string? AttackerSnapshotId { get; set; }

    [DataMember(Name = "LostPawnGlobalKeys")]
    public List<string> LostPawnGlobalKeys { get; set; } = new();

    [DataMember(Name = "LostThings")]
    public List<RaidLostThingSummary> LostThings { get; set; } = new();

    [DataMember(Name = "Reason")]
    public string? Reason { get; set; }

    [DataMember(Name = "ClientEffect")]
    public int ClientEffect { get; set; }
}

[DataContract]
internal sealed class RaidLostThingSummary
{
    [DataMember(Name = "GlobalKey")]
    public string GlobalKey { get; set; } = string.Empty;

    [DataMember(Name = "Def")]
    public string? Def { get; set; }

    [DataMember(Name = "StackCount")]
    public int StackCount { get; set; } = 1;

    [DataMember(Name = "DisplayLabel")]
    public string? DisplayLabel { get; set; }
}
