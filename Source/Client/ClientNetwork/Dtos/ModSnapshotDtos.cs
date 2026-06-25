using System.Runtime.Serialization;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.ClientNetwork;

public static class ModSnapshotUploadKinds
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
    public const string MercenaryGuardHireConfirmation = "MercenaryGuardHireConfirmation";
    public const string RaidCreationConfirmation = "RaidCreationConfirmation";
    public const string RaidSettlementEvidence = "RaidSettlementEvidence";
    public const string SupportPawnCreationConfirmation = "SupportPawnCreationConfirmation";
    public const string ColonyRelocation = "ColonyRelocation";
    public const string EndgameAchievement = "EndgameAchievement";
}

[DataContract]
public sealed class ModSnapshotPackageMetadataDto
{
    [DataMember(Name = "packageVersion")]
    public string PackageVersion { get; set; } = string.Empty;

    [DataMember(Name = "ownerId")]
    public string OwnerId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "snapshotId")]
    public string SnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "rimWorldVersion")]
    public string? RimWorldVersion { get; set; }

    [DataMember(Name = "payloadEncoding")]
    public string PayloadEncoding { get; set; } = "GzipRws";

    [DataMember(Name = "originalSaveBytes")]
    public long OriginalSaveBytes { get; set; }

    [DataMember(Name = "payloadBytes")]
    public long PayloadBytes { get; set; }

    [DataMember(Name = "originalSha256")]
    public string OriginalSha256 { get; set; } = string.Empty;

    [DataMember(Name = "payloadSha256")]
    public string PayloadSha256 { get; set; } = string.Empty;

    [DataMember(Name = "previousSnapshotId")]
    public string? PreviousSnapshotId { get; set; }

    [DataMember(Name = "lineageToken")]
    public string? LineageToken { get; set; }

    [DataMember(Name = "nextLineageToken")]
    public string? NextLineageToken { get; set; }

    [DataMember(Name = "gameTicks")]
    public long? GameTicks { get; set; }

    [DataMember(Name = "snapshotUploadKind")]
    public string? SnapshotUploadKind { get; set; }
}

[DataContract]
public sealed class ModUploadSnapshotRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "snapshotId")]
    public string SnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "package")]
    public ModSnapshotPackageMetadataDto? Package { get; set; }

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }

    [DataMember(Name = "confirmationOperation")]
    public string? ConfirmationOperation { get; set; }

    [DataMember(Name = "achievementCandidates")]
    public List<ModSnapshotAchievementCandidateDto> AchievementCandidates { get; set; } = new();
}

[DataContract]
public sealed class ModSnapshotAchievementCandidateDto
{
    [DataMember(Name = "achievementId")]
    public string AchievementId { get; set; } = string.Empty;

    [DataMember(Name = "eventKey")]
    public string EventKey { get; set; } = string.Empty;

    [DataMember(Name = "value")]
    public long Value { get; set; }

    [DataMember(Name = "category")]
    public string? Category { get; set; }

    [DataMember(Name = "labelKey")]
    public string? LabelKey { get; set; }

    [DataMember(Name = "iconId")]
    public string? IconId { get; set; }

    [DataMember(Name = "color")]
    public string? Color { get; set; }

    [DataMember(Name = "aggregationKind")]
    public string? AggregationKind { get; set; }

    [DataMember(Name = "metadataJson")]
    public string? MetadataJson { get; set; }
}

[DataContract]
public sealed class ModUploadSnapshotResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "acceptedSnapshotId")]
    public string? AcceptedSnapshotId { get; set; }

    [DataMember(Name = "uploadResult")]
    public string? UploadResult { get; set; }

    [DataMember(Name = "nextLineageToken")]
    public string? NextLineageToken { get; set; }
}

[DataContract]
public sealed class ModDownloadLatestSnapshotRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }

    [DataMember(Name = "authorizationEventId")]
    public string? AuthorizationEventId { get; set; }

    [DataMember(Name = "authorizationScope")]
    public string? AuthorizationScope { get; set; }
}

[DataContract]
public sealed class ModDownloadLatestSnapshotResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "snapshotId")]
    public string? SnapshotId { get; set; }

    [DataMember(Name = "package")]
    public ModSnapshotPackageMetadataDto? Package { get; set; }

    [DataMember(Name = "activeRaidRecovery")]
    public ModActiveRaidRecoveryDto? ActiveRaidRecovery { get; set; }
}

[DataContract]
public sealed class ModDownloadLatestSnapshotPayloadRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "snapshotId")]
    public string SnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }

    [DataMember(Name = "authorizationEventId")]
    public string? AuthorizationEventId { get; set; }

    [DataMember(Name = "authorizationScope")]
    public string? AuthorizationScope { get; set; }
}
