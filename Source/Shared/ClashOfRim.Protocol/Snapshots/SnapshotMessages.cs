using System;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Protocol;

public sealed class UploadSnapshotResponse
{
    public UploadSnapshotResponse(
        ProtocolResponse result,
        string? acceptedSnapshotId,
        string? uploadResult,
        string? nextLineageToken = null)
    {
        Result = result;
        AcceptedSnapshotId = acceptedSnapshotId;
        UploadResult = uploadResult;
        NextLineageToken = nextLineageToken;
    }

    public ProtocolResponse Result { get; }

    public string? AcceptedSnapshotId { get; }

    public string? UploadResult { get; }

    public string? NextLineageToken { get; }
}

public sealed class UploadSnapshotMetadataRequest
{
    public UploadSnapshotMetadataRequest(
        string idempotencyKey,
        string userId,
        string colonyId,
        string snapshotId,
        SnapshotPackageMetadataDto package,
        string? authToken = null,
        string? confirmationOperation = null,
        IReadOnlyList<SnapshotAchievementCandidateDto>? achievementCandidates = null)
    {
        IdempotencyKey = idempotencyKey;
        UserId = userId;
        ColonyId = colonyId;
        SnapshotId = snapshotId;
        Package = package;
        AuthToken = authToken;
        ConfirmationOperation = confirmationOperation;
        AchievementCandidates = achievementCandidates ?? Array.Empty<SnapshotAchievementCandidateDto>();
    }

    public string IdempotencyKey { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string SnapshotId { get; }

    public SnapshotPackageMetadataDto Package { get; }

    public string? AuthToken { get; }

    public string? ConfirmationOperation { get; }

    public IReadOnlyList<SnapshotAchievementCandidateDto> AchievementCandidates { get; }
}

public static class SnapshotConfirmationOperations
{
    public const string ColonyRelocation = "ColonyRelocation";
    public const string EndgameAchievement = "EndgameAchievement";
}

public static class SnapshotUploadKinds
{
    public const string ManualUpload = "ManualUpload";
    public const string MapSessionSynchronization = "MapSessionSynchronization";
    public const string InitialColonySnapshot = "InitialColonySnapshot";
    public const string AutoSave = "AutoSave";
    public const string EventApplicationConfirmation = "EventApplicationConfirmation";
    public const string BatchEventApplicationConfirmation = "BatchEventApplicationConfirmation";
    public const string GiftCreationConfirmation = "GiftCreationConfirmation";
    public const string TradeOrderCreationConfirmation = "TradeOrderCreationConfirmation";
    public const string TradeFulfillmentConfirmation = "TradeFulfillmentConfirmation";
    public const string ServerShopPurchaseConfirmation = "ServerShopPurchaseConfirmation";
    public const string BankLoanCreationConfirmation = "BankLoanCreationConfirmation";
    public const string BankLoanRepaymentConfirmation = "BankLoanRepaymentConfirmation";
    public const string BankDebtRepaymentConfirmation = "BankDebtRepaymentConfirmation";
    public const string MercenaryHireConfirmation = "MercenaryHireConfirmation";
    public const string RaidCreationConfirmation = "RaidCreationConfirmation";
    public const string RaidSettlementEvidence = "RaidSettlementEvidence";
    public const string SupportPawnCreationConfirmation = "SupportPawnCreationConfirmation";
    public const string ColonyRelocation = "ColonyRelocation";
    public const string EndgameAchievement = "EndgameAchievement";
}

public sealed class SnapshotAchievementCandidateDto
{
    public SnapshotAchievementCandidateDto(
        string achievementId,
        string eventKey,
        long value,
        string? category = null,
        string? labelKey = null,
        string? iconId = null,
        string? color = null,
        string? aggregationKind = null,
        string? metadataJson = null)
    {
        AchievementId = achievementId;
        EventKey = eventKey;
        Value = value;
        Category = category;
        LabelKey = labelKey;
        IconId = iconId;
        Color = AchievementColors.Normalize(color);
        AggregationKind = aggregationKind;
        MetadataJson = metadataJson;
    }

    public string AchievementId { get; }

    public string EventKey { get; }

    public long Value { get; }

    public string? Category { get; }

    public string? LabelKey { get; }

    public string? IconId { get; }

    public string Color { get; }

    public string? AggregationKind { get; }

    public string? MetadataJson { get; }
}

public sealed class DownloadLatestSnapshotRequest
{
    public DownloadLatestSnapshotRequest(
        string userId,
        string colonyId,
        string? authToken = null,
        string? authorizationEventId = null,
        string? authorizationScope = null)
    {
        UserId = userId;
        ColonyId = colonyId;
        AuthToken = authToken;
        AuthorizationEventId = authorizationEventId;
        AuthorizationScope = authorizationScope;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? AuthToken { get; }

    public string? AuthorizationEventId { get; }

    public string? AuthorizationScope { get; }
}

public sealed class DownloadLatestSnapshotResponse
{
    public DownloadLatestSnapshotResponse(
        ProtocolResponse result,
        string? snapshotId,
        SnapshotPackageMetadataDto? package,
        ActiveRaidRecoveryDto? activeRaidRecovery = null)
    {
        Result = result;
        SnapshotId = snapshotId;
        Package = package;
        ActiveRaidRecovery = activeRaidRecovery;
    }

    public ProtocolResponse Result { get; }

    public string? SnapshotId { get; }

    public SnapshotPackageMetadataDto? Package { get; }

    public ActiveRaidRecoveryDto? ActiveRaidRecovery { get; }
}

public sealed class ActiveRaidRecoveryDto
{
    public ActiveRaidRecoveryDto(
        string eventId,
        string status,
        DateTimeOffset serverNowUtc,
        DateTimeOffset startedAtUtc,
        DateTimeOffset deadlineUtc,
        DateTimeOffset finalDeadlineUtc,
        string defenderUserId,
        string? defenderColonyId,
        string defenderSnapshotId)
    {
        EventId = eventId;
        Status = status;
        ServerNowUtc = serverNowUtc;
        StartedAtUtc = startedAtUtc;
        DeadlineUtc = deadlineUtc;
        FinalDeadlineUtc = finalDeadlineUtc;
        DefenderUserId = defenderUserId;
        DefenderColonyId = defenderColonyId;
        DefenderSnapshotId = defenderSnapshotId;
    }

    public string EventId { get; }

    public string Status { get; }

    public DateTimeOffset ServerNowUtc { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset DeadlineUtc { get; }

    public DateTimeOffset FinalDeadlineUtc { get; }

    public string DefenderUserId { get; }

    public string? DefenderColonyId { get; }

    public string DefenderSnapshotId { get; }
}

public sealed class DownloadLatestSnapshotPayloadRequest
{
    public DownloadLatestSnapshotPayloadRequest(
        string userId,
        string colonyId,
        string snapshotId,
        string? authToken = null,
        string? authorizationEventId = null,
        string? authorizationScope = null)
    {
        UserId = userId;
        ColonyId = colonyId;
        SnapshotId = snapshotId;
        AuthToken = authToken;
        AuthorizationEventId = authorizationEventId;
        AuthorizationScope = authorizationScope;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string SnapshotId { get; }

    public string? AuthToken { get; }

    public string? AuthorizationEventId { get; }

    public string? AuthorizationScope { get; }
}
