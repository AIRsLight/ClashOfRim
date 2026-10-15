using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public enum RaidSettlementOrigin
{
    OnlineEvidence,
    OfflineTimeout
}

public sealed record RaidSettlementPostUploadData(
    string RaidEventId,
    byte[] EvidencePayload,
    RaidSettlementOrigin Origin,
    string? ClientApplicationResult,
    string? RequestHash = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal RaidEvidenceSchedulePreparation? PreparedSchedule { get; init; }
}

public sealed record RaidSettlementDeferredPayload(
    string RaidEventId,
    string AttackerUserId,
    string AttackerColonyId,
    string DefenderUserId,
    string DefenderColonyId,
    string EvidenceSnapshotId,
    string DefenderSnapshotId,
    string EvidenceArtifactId,
    RaidSettlementOrigin Origin,
    string? ClientApplicationResult,
    RaidEvidencePublication? Publication = null,
    string? RequestHash = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Serialize()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public static RaidSettlementDeferredPayload Deserialize(string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        return JsonSerializer.Deserialize<RaidSettlementDeferredPayload>(payloadJson, JsonOptions)
            ?? throw new InvalidOperationException("Raid settlement deferred payload is empty.");
    }
}

// Null Publication identifies legacy jobs, whose original destination was not recorded.
public sealed record RaidEvidencePublication(
    Save.SaveSnapshotEnvelope? Previous,
    Save.SaveSnapshotEnvelope Evidence);

public sealed record RaidSettlementScheduleRequest(
    string RaidEventId,
    Plugins.SnapshotPostUploadContext Context,
    byte[] EvidencePayload,
    RaidSettlementOrigin Origin,
    string? ClientApplicationResult,
    string? RequestHash = null);
