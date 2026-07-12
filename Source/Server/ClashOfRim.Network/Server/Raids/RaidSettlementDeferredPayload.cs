using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public enum RaidSettlementOrigin
{
    OnlineEvidence,
    OfflineTimeout
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
    string? ClientApplicationResult)
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

public sealed record RaidSettlementScheduleRequest(
    string RaidEventId,
    Plugins.SnapshotPostUploadContext Context,
    byte[] EvidencePayload,
    RaidSettlementOrigin Origin,
    string? ClientApplicationResult);
