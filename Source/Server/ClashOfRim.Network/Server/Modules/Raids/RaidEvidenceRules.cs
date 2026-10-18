using System.Security.Cryptography;
using System.Text;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal static class RaidEvidenceRules
{
    internal static AuthoritativeEvent Source(IAuthoritativeEventLedger ledger, RaidEvidenceSchedule request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RaidEventId);
        ArgumentNullException.ThrowIfNull(request.Job);
        ArgumentNullException.ThrowIfNull(request.Snapshot);
        ArgumentNullException.ThrowIfNull(request.EvidencePayload);
        if (request.Job.Kind != SnapshotPostUploadKind.RaidSettlementEvidence)
            throw new InvalidOperationException("Raid settlement can only be scheduled from raid settlement evidence.");
        var raid = ledger.Find(request.RaidEventId) ?? throw new InvalidOperationException($"Raid event '{request.RaidEventId}' was not found.");
        if (raid.Type != ServerEventType.Raid || raid.Payload is not RaidEventPayload { OpponentKind: RaidOpponentKind.Player, RequiresSettlement: true })
            throw new InvalidOperationException($"Event '{request.RaidEventId}' is not a player raid requiring settlement.");
        if (raid.Actor.ColonyId is null || raid.Target.ColonyId is null || request.Snapshot.Identity.SnapshotId is null
            || request.Job.UserId != raid.Actor.UserId || request.Job.ColonyId != raid.Actor.ColonyId
            || request.Job.SnapshotId != request.Snapshot.Identity.SnapshotId
            || request.Snapshot.Identity != request.Snapshot.Envelope.Identity
            || request.Snapshot.Identity.OwnerId != raid.Actor.UserId || request.Snapshot.Identity.ColonyId != raid.Actor.ColonyId)
            throw new InvalidOperationException("Raid evidence does not match its source.");
        return raid;
    }

    internal static void ValidateBinding(IAuthoritativeEventLedger ledger, SnapshotPostUploadJobRecord job,
        RaidSettlementDeferredPayload payload, SaveSnapshotPackage evidence)
    {
        AuthoritativeEvent? raid = ledger.Find(payload.RaidEventId);
        if (raid?.Type != ServerEventType.Raid || raid.Payload is not RaidEventPayload raidPayload
            || raid.Actor.UserId != payload.AttackerUserId || raid.Actor.ColonyId != payload.AttackerColonyId
            || raid.Target.UserId != payload.DefenderUserId || raid.Target.ColonyId != payload.DefenderColonyId
            || raidPayload.DefenderSnapshotId != payload.DefenderSnapshotId
            || job.ProcessorId != RaidSettlementWork.ProcessorId || job.Kind != SnapshotPostUploadKind.RaidSettlementEvidence
            || job.JobId != "raid-settlement:" + payload.RaidEventId
            || job.UserId != payload.AttackerUserId || job.ColonyId != payload.AttackerColonyId
            || job.SnapshotId != payload.EvidenceSnapshotId
            || payload.EvidenceArtifactId != BuildArtifactId(payload.RaidEventId, payload.EvidenceSnapshotId)
            || evidence.Envelope.Identity != new SnapshotIdentity(payload.AttackerUserId, payload.AttackerColonyId, payload.EvidenceSnapshotId)
            || (payload.Publication is not null && evidence.Envelope != payload.Publication.Evidence))
            throw new InvalidDataException("Raid evidence artifact does not match its prepared job.");
    }

    internal static void ValidatePayload(SaveSnapshotPackage evidence)
    {
        if (evidence.Payload.LongLength != evidence.Envelope.PayloadBytes
            || !string.Equals(Convert.ToHexString(SHA256.HashData(evidence.Payload)), evidence.Envelope.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Raid evidence payload does not match its envelope.");
    }

    internal static void ValidateReplay(RaidEvidenceSchedule request, RaidSettlementDeferredPayload payload, SaveSnapshotPackage original)
    {
        if (payload.RequestHash != request.RequestHash || payload.Origin != request.Origin
            || payload.ClientApplicationResult != request.ClientApplicationResult
            || original.Envelope != (request.Snapshot.Envelope with
                { CreatedAtUtc = original.Envelope.CreatedAtUtc, NextLineageToken = original.Envelope.NextLineageToken })
            || !original.Payload.AsSpan().SequenceEqual(request.EvidencePayload))
            throw new InvalidOperationException("Raid evidence retry differs from its original request.");
    }

    internal static bool SameVersion(SaveSnapshotEnvelope? left, SaveSnapshotEnvelope? right) =>
        right is null ? left is null : SnapshotCommitService.SameVersion(left, right);

    internal static string BuildArtifactId(string raidEventId, string evidenceSnapshotId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raidEventId + "\u001f" + evidenceSnapshotId));
        return "raid-settlement-" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
