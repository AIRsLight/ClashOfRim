using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal sealed record RaidEvidenceSchedule(string RaidEventId, LatestSnapshotRecord Snapshot,
    SnapshotPostUploadJobInput Job, byte[] EvidencePayload, RaidSettlementOrigin Origin,
    string? ClientApplicationResult, string? RequestHash);
