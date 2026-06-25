namespace AIRsLight.ClashOfRim.Save;

public sealed record SnapshotUploadContext(
    string OwnerId,
    string ColonyId,
    string SnapshotId,
    string? ConfirmationOperation = null,
    string? SnapshotUploadKind = null,
    string? RequiredRaidEventId = null)
{
    public SnapshotUploadValidationRules ValidationRules =>
        SnapshotUploadValidationRules.For(SnapshotUploadKind, ConfirmationOperation, RequiredRaidEventId);
}

public readonly record struct SnapshotUploadValidationRules(
    bool ValidateSnapshotTime,
    bool ValidateColonyContinuity,
    bool AllowSingleColonyRelocation,
    string? RequiredRaidEventId)
{
    public bool RequiresRaidSettlementBattleMap => !string.IsNullOrWhiteSpace(RequiredRaidEventId);

    public static SnapshotUploadValidationRules For(
        string? snapshotUploadKind,
        string? confirmationOperation,
        string? requiredRaidEventId)
    {
        bool isRaidSettlementEvidence = string.Equals(snapshotUploadKind, "RaidSettlementEvidence", StringComparison.Ordinal);
        bool isColonyRelocation =
            string.Equals(snapshotUploadKind, "ColonyRelocation", StringComparison.Ordinal)
            || string.Equals(confirmationOperation, "ColonyRelocation", StringComparison.Ordinal);

        return new SnapshotUploadValidationRules(
            ValidateSnapshotTime: true,
            ValidateColonyContinuity: !isRaidSettlementEvidence,
            AllowSingleColonyRelocation: isColonyRelocation,
            RequiredRaidEventId: isRaidSettlementEvidence ? requiredRaidEventId : null);
    }
}
