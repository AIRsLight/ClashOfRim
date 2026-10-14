using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

/// <summary>The original context for an ordinary upload, retained until every post-processor finishes.</summary>
public sealed record SnapshotUploadMutation(SaveSnapshotEnvelope? Previous, string? ConfirmationOperation,
    IReadOnlyList<SnapshotAchievementCandidateDto> AchievementCandidates)
{
    internal void Validate(SnapshotCommitIntent intent)
    {
        var envelope = intent.Package.Envelope;
        var identity = envelope.Identity;
        if (string.IsNullOrWhiteSpace(identity.OwnerId) || string.IsNullOrWhiteSpace(identity.ColonyId)
            || string.IsNullOrWhiteSpace(identity.SnapshotId) || string.IsNullOrWhiteSpace(envelope.NextLineageToken)
            || AchievementCandidates is null || AchievementCandidates.Any(candidate => candidate is null)
            || intent.PendingOperations is null || intent.SnapshotUploadKind == SnapshotUploadKinds.RaidSettlementEvidence
            || Previous?.Identity.SnapshotId != intent.PreviousSnapshotId || Previous?.NextLineageToken != intent.PreviousLineageToken
            || Previous is not null && (Previous.Identity.OwnerId != identity.OwnerId || Previous.Identity.ColonyId != identity.ColonyId
                || Previous.Identity.SnapshotId == identity.SnapshotId))
            throw new InvalidDataException("Ordinary upload journal has an invalid baseline or post-processing context.");
    }
}
